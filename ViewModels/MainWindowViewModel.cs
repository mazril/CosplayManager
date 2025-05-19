// Plik: ViewModels/MainWindowViewModel.cs
using CosplayManager.Models;
using CosplayManager.Services;
using CosplayManager.ViewModels.Base;
using CosplayManager.Views;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace CosplayManager.ViewModels
{
    public class MainWindowViewModel : ObservableObject
    {
        private readonly ProfileService _profileService;
        private readonly FileScannerService _fileScannerService;
        private readonly ImageMetadataService _imageMetadataService;
        private readonly SettingsService _settingsService;

        private List<Models.ProposedMove> _lastModelSpecificSuggestions = new List<Models.ProposedMove>();
        private string? _lastScannedModelNameForSuggestions; // Jeśli null, oznacza to, że _lastModelSpecificSuggestions zawiera wyniki globalnego skanowania
        private bool _isRefreshingProfilesPostMove = false;

        private const double DUPLICATE_SIMILARITY_THRESHOLD = 0.98;

        private CancellationTokenSource? _activeLongOperationCts;

        private ObservableCollection<ModelDisplayViewModel> _hierarchicalProfilesList;
        public ObservableCollection<ModelDisplayViewModel> HierarchicalProfilesList
        {
            get => _hierarchicalProfilesList;
            private set => SetProperty(ref _hierarchicalProfilesList, value);
        }

        private CategoryProfile? _selectedProfile;
        public CategoryProfile? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                string? oldSelectedProfileName = _selectedProfile?.CategoryName;
                if (SetProperty(ref _selectedProfile, value))
                {
                    UpdateEditFieldsFromSelectedProfile();
                    OnPropertyChanged(nameof(IsProfileSelected));
                    CommandManager.InvalidateRequerySuggested();
                }
                if (_selectedProfile == null && oldSelectedProfileName != null &&
                    !_profileService.GetAllProfiles().Any(p => p.CategoryName == oldSelectedProfileName))
                {
                    UpdateEditFieldsFromSelectedProfile();
                }
            }
        }
        public bool IsProfileSelected => SelectedProfile != null;
        public bool AnyProfilesLoaded => HierarchicalProfilesList.Any(m => m.HasCharacterProfiles);


        private string _currentProfileNameForEdit = string.Empty;
        public string CurrentProfileNameForEdit
        {
            get => _currentProfileNameForEdit;
            set
            {
                if (SetProperty(ref _currentProfileNameForEdit, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private string _modelNameInput = string.Empty;
        public string ModelNameInput { get => _modelNameInput; set { if (SetProperty(ref _modelNameInput, value)) UpdateCurrentProfileNameForEdit(); } }

        private string _characterNameInput = string.Empty;
        public string CharacterNameInput { get => _characterNameInput; set { if (SetProperty(ref _characterNameInput, value)) UpdateCurrentProfileNameForEdit(); } }


        private ObservableCollection<ImageFileEntry> _imageFiles;
        public ObservableCollection<ImageFileEntry> ImageFiles
        {
            get => _imageFiles;
            private set
            {
                if (SetProperty(ref _imageFiles, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private string _statusMessage = "Gotowy.";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        private string _libraryRootPath = string.Empty;
        public string LibraryRootPath
        {
            get => _libraryRootPath;
            set
            {
                if (SetProperty(ref _libraryRootPath, value))
                {
                    ClearModelSpecificSuggestionsCache();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private string _sourceFolderNamesInput = "Mix,Mieszane,Unsorted,Downloaded";
        public string SourceFolderNamesInput
        {
            get => _sourceFolderNamesInput;
            set
            {
                if (SetProperty(ref _sourceFolderNamesInput, value))
                {
                    ClearModelSpecificSuggestionsCache();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private double _suggestionSimilarityThreshold = 0.85;
        public double SuggestionSimilarityThreshold
        {
            get => _suggestionSimilarityThreshold;
            set
            {
                if (SetProperty(ref _suggestionSimilarityThreshold, value))
                {
                    // If suggestions are cached, re-evaluate pending counts based on new threshold
                    if (_lastModelSpecificSuggestions.Any())
                    {
                        RefreshPendingSuggestionCountsFromCache();
                    }
                }
            }
        }

        private bool _areAdvancedSettingsExpanded;
        public bool AreAdvancedSettingsExpanded
        {
            get => _areAdvancedSettingsExpanded;
            set => SetProperty(ref _areAdvancedSettingsExpanded, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public ICommand LoadProfilesCommand { get; }
        public ICommand GenerateProfileCommand { get; }
        public ICommand SaveProfilesCommand { get; }
        public ICommand RemoveProfileCommand { get; }
        public ICommand AddFilesToProfileCommand { get; }
        public ICommand ClearFilesFromProfileCommand { get; }
        public ICommand CreateNewProfileSetupCommand { get; }
        public ICommand SelectLibraryPathCommand { get; }
        public ICommand AutoCreateProfilesCommand { get; }
        public ICommand SuggestImagesCommand { get; } // Global suggestions
        public ICommand SaveAppSettingsCommand { get; }
        public ICommand MatchModelSpecificCommand { get; } // For model context menu
        public ICommand CheckCharacterSuggestionsCommand { get; } // For character context menu
        public ICommand RemoveModelTreeCommand { get; }
        public ICommand AnalyzeModelForSplittingCommand { get; }
        public ICommand OpenSplitProfileDialogCommand { get; }
        public ICommand CancelCurrentOperationCommand { get; }
        public ICommand EnsureThumbnailsLoadedCommand { get; }
        public ICommand RemoveDuplicatesInModelCommand { get; }
        public ICommand ApplyAllMatchesForModelCommand { get; }


        public MainWindowViewModel(
            ProfileService profileService,
            FileScannerService fileScannerService,
            ImageMetadataService imageMetadataService,
            SettingsService settingsService)
        {
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _fileScannerService = fileScannerService ?? throw new ArgumentNullException(nameof(fileScannerService));
            _imageMetadataService = imageMetadataService ?? throw new ArgumentNullException(nameof(imageMetadataService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            HierarchicalProfilesList = new ObservableCollection<ModelDisplayViewModel>();
            ImageFiles = new ObservableCollection<ImageFileEntry>();

            LoadProfilesCommand = new AsyncRelayCommand(ExecuteLoadProfilesAsync, CanExecuteLoadProfiles);
            GenerateProfileCommand = new AsyncRelayCommand(ExecuteGenerateProfileAsync, CanExecuteGenerateProfile);
            SaveProfilesCommand = new AsyncRelayCommand(ExecuteSaveAllProfilesAsync, CanExecuteSaveAllProfiles);
            RemoveProfileCommand = new AsyncRelayCommand(ExecuteRemoveProfileAsync, CanExecuteRemoveProfile);
            AddFilesToProfileCommand = new RelayCommand(ExecuteAddFilesToProfile, CanExecuteAddFilesToProfile);
            ClearFilesFromProfileCommand = new RelayCommand(ExecuteClearFilesFromProfile, CanExecuteClearFilesFromProfile);
            CreateNewProfileSetupCommand = new RelayCommand(ExecuteCreateNewProfileSetup, CanExecuteCreateNewProfileSetup);
            SelectLibraryPathCommand = new RelayCommand(ExecuteSelectLibraryPath, CanExecuteSelectLibraryPath);
            AutoCreateProfilesCommand = new AsyncRelayCommand(ExecuteAutoCreateProfilesAsync, CanExecuteAutoCreateProfiles);
            SuggestImagesCommand = new AsyncRelayCommand(ExecuteSuggestImagesAsync, CanExecuteSuggestImages);
            SaveAppSettingsCommand = new AsyncRelayCommand(ExecuteSaveAppSettingsAsync, CanExecuteSaveAppSettings);
            MatchModelSpecificCommand = new AsyncRelayCommand(ExecuteMatchModelSpecificAsync, CanExecuteMatchModelSpecific);
            CheckCharacterSuggestionsCommand = new AsyncRelayCommand(ExecuteCheckCharacterSuggestionsAsync, CanExecuteCheckCharacterSuggestions);
            RemoveModelTreeCommand = new AsyncRelayCommand(ExecuteRemoveModelTreeAsync, CanExecuteRemoveModelTree);
            AnalyzeModelForSplittingCommand = new AsyncRelayCommand(ExecuteAnalyzeModelForSplittingAsync, CanExecuteAnalyzeModelForSplitting);
            OpenSplitProfileDialogCommand = new AsyncRelayCommand(ExecuteOpenSplitProfileDialogAsync, CanExecuteOpenSplitProfileDialog);
            RemoveDuplicatesInModelCommand = new AsyncRelayCommand(ExecuteRemoveDuplicatesInModelAsync, CanExecuteRemoveDuplicatesInModel);
            ApplyAllMatchesForModelCommand = new AsyncRelayCommand(ExecuteApplyAllMatchesForModelAsync, CanExecuteApplyAllMatchesForModel);


            CancelCurrentOperationCommand = new RelayCommand(ExecuteCancelCurrentOperation, CanExecuteCancelCurrentOperation);
            EnsureThumbnailsLoadedCommand = new AsyncRelayCommand(ExecuteEnsureThumbnailsLoadedAsync, CanExecuteEnsureThumbnailsLoaded);
        }

        private async Task RunLongOperation(Func<CancellationToken, Task> operation, string statusMessagePrefix)
        {
            CancellationTokenSource? previousCts = _activeLongOperationCts;
            _activeLongOperationCts = new CancellationTokenSource();
            var token = _activeLongOperationCts.Token;

            if (previousCts != null)
            {
                SimpleFileLogger.Log($"RunLongOperation: Anulowanie poprzedniej operacji (token: {previousCts.Token.GetHashCode()}). Nowy token: {token.GetHashCode()}");
                previousCts.Cancel();
                previousCts.Dispose();
            }
            else
            {
                SimpleFileLogger.Log($"RunLongOperation: Brak poprzedniej operacji. Nowy token: {token.GetHashCode()}");
            }

            IsBusy = true;
            StatusMessage = $"{statusMessagePrefix}... (Można anulować)";
            SimpleFileLogger.Log($"RunLongOperation: Rozpoczęto '{statusMessagePrefix}'. Token: {token.GetHashCode()}");

            try
            {
                await operation(token);
                if (token.IsCancellationRequested)
                {
                    StatusMessage = $"{statusMessagePrefix} - Anulowano.";
                    SimpleFileLogger.Log($"RunLongOperation: Operacja '{statusMessagePrefix}' (token: {token.GetHashCode()}) anulowana przez użytkownika.");
                }
                else
                {
                    SimpleFileLogger.Log($"RunLongOperation: Operacja '{statusMessagePrefix}' (token: {token.GetHashCode()}) zakończona (lub przerwana wewnętrznie).");
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = $"{statusMessagePrefix} - Anulowano.";
                SimpleFileLogger.Log($"RunLongOperation: Operacja '{statusMessagePrefix}' (token: {token.GetHashCode()}) anulowana (OperationCanceledException).");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Błąd podczas: {statusMessagePrefix}: {ex.Message}";
                SimpleFileLogger.LogError($"RunLongOperation: Błąd podczas operacji '{statusMessagePrefix}' (token: {token.GetHashCode()})", ex);
                MessageBox.Show($"Wystąpił nieoczekiwany błąd podczas operacji '{statusMessagePrefix}':\n{ex.Message}\n\nSprawdź logi aplikacji.", "Błąd Krytyczny Operacji", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                if (_activeLongOperationCts != null && _activeLongOperationCts.Token == token)
                {
                    _activeLongOperationCts.Dispose();
                    _activeLongOperationCts = null;
                    SimpleFileLogger.Log($"RunLongOperation: CTS dla tokenu {token.GetHashCode()} został usunięty.");
                }
                else if (_activeLongOperationCts != null)
                {
                    SimpleFileLogger.Log($"RunLongOperation: CTS dla tokenu {token.GetHashCode()} NIE został usunięty, aktywny CTS ma token {_activeLongOperationCts.Token.GetHashCode()}.");
                }
                else
                {
                    SimpleFileLogger.Log($"RunLongOperation: CTS dla tokenu {token.GetHashCode()} NIE został usunięty, _activeLongOperationCts jest null.");
                }
                // StatusMessage could have been updated by the operation itself on completion or cancellation
                // Avoid overwriting a more specific status message if the operation set one.
                if (StatusMessage.EndsWith("... (Można anulować)"))
                {
                    StatusMessage = $"{statusMessagePrefix} - Zakończono.";
                }
                SimpleFileLogger.Log($"RunLongOperation: Zakończono (finally) dla '{statusMessagePrefix}' (token: {token.GetHashCode()}). Aktualny StatusMessage: {StatusMessage}");
            }
        }

        private void UpdateCurrentProfileNameForEdit()
        {
            if (!string.IsNullOrWhiteSpace(ModelNameInput) && !string.IsNullOrWhiteSpace(CharacterNameInput))
            {
                CurrentProfileNameForEdit = $"{ModelNameInput} - {CharacterNameInput}";
            }
            else if (!string.IsNullOrWhiteSpace(ModelNameInput))
            {
                CurrentProfileNameForEdit = $"{ModelNameInput} - General";
            }
            else
            {
                CurrentProfileNameForEdit = string.Empty;
            }
        }

        private (string model, string character) ParseCategoryName(string? categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return ("UnknownModel", "UnknownCharacter");
            var parts = categoryName.Split(new[] { " - " }, 2, StringSplitOptions.None);
            string model = parts.Length > 0 ? parts[0].Trim() : categoryName.Trim();
            string character = parts.Length > 1 ? parts[1].Trim() : "General";

            if (string.IsNullOrWhiteSpace(model)) model = "UnknownModel";
            if (string.IsNullOrWhiteSpace(character)) character = "General";
            return (model, character);
        }

        private string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "_";
            string invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            string sanitizedName = name;
            foreach (char invalidChar in invalidChars.Distinct())
            {
                sanitizedName = sanitizedName.Replace(invalidChar.ToString(), "_");
            }
            sanitizedName = sanitizedName.Replace(":", "_").Replace("?", "_").Replace("*", "_")
                                       .Replace("\"", "_").Replace("<", "_").Replace(">", "_")
                                       .Replace("|", "_").Replace("/", "_").Replace("\\", "_");
            sanitizedName = sanitizedName.Trim().TrimStart('.').TrimEnd('.');
            if (string.IsNullOrWhiteSpace(sanitizedName)) return "_";
            return sanitizedName;
        }

        private void UpdateEditFieldsFromSelectedProfile()
        {
            if (_selectedProfile != null)
            {
                CurrentProfileNameForEdit = _selectedProfile.CategoryName;
                var (model, characterFullName) = ParseCategoryName(_selectedProfile.CategoryName);
                ModelNameInput = model;
                CharacterNameInput = characterFullName;

                var newImageFiles = new ObservableCollection<ImageFileEntry>();
                if (_selectedProfile.SourceImagePaths != null)
                {
                    foreach (var path in _selectedProfile.SourceImagePaths)
                    {
                        if (File.Exists(path))
                        {
                            newImageFiles.Add(new ImageFileEntry { FilePath = path, FileName = Path.GetFileName(path) });
                        }
                        else
                        {
                            SimpleFileLogger.LogWarning($"OSTRZEŻENIE (UpdateEditFields): Ścieżka '{path}' dla profilu '{_selectedProfile.CategoryName}' nie istnieje.");
                        }
                    }
                }
                ImageFiles = newImageFiles;
            }
            else
            {
                CurrentProfileNameForEdit = string.Empty;
                ModelNameInput = string.Empty;
                CharacterNameInput = string.Empty;
                ImageFiles = new ObservableCollection<ImageFileEntry>();
            }
        }

        private void ClearModelSpecificSuggestionsCache()
        {
            SimpleFileLogger.Log("ClearModelSpecificSuggestionsCache: Czyszczenie cache sugestii.");
            _lastModelSpecificSuggestions.Clear();
            _lastScannedModelNameForSuggestions = "__CACHE_CLEARED__"; // Use a distinct value that won't match a model name
            RefreshPendingSuggestionCountsFromCache(); // This will effectively reset counts if the marker is handled
        }

        private UserSettings GetCurrentSettings() => new UserSettings
        {
            LibraryRootPath = this.LibraryRootPath,
            SourceFolderNamesInput = this.SourceFolderNamesInput,
            SuggestionSimilarityThreshold = this.SuggestionSimilarityThreshold
        };

        private void ApplySettings(UserSettings settings)
        {
            if (settings == null) return;
            LibraryRootPath = settings.LibraryRootPath;
            SourceFolderNamesInput = settings.SourceFolderNamesInput;
            SuggestionSimilarityThreshold = settings.SuggestionSimilarityThreshold;
            SimpleFileLogger.Log("Zastosowano wczytane ustawienia.");
        }

        private Task ExecuteSaveAppSettingsAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                token.ThrowIfCancellationRequested();
                await _settingsService.SaveSettingsAsync(GetCurrentSettings());
                StatusMessage = "Ustawienia aplikacji zapisane.";
                SimpleFileLogger.Log("Ustawienia aplikacji zapisane (na żądanie).");
            }, "Zapisywanie ustawień aplikacji");

        public Task InitializeAsync() =>
            RunLongOperation(async token =>
            {
                SimpleFileLogger.Log("ViewModel: InitializeAsync start.");
                ApplySettings(await _settingsService.LoadSettingsAsync());
                token.ThrowIfCancellationRequested();
                await InternalExecuteLoadProfilesAsync(token);
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(LibraryRootPath)) StatusMessage = "Gotowy. Wybierz folder biblioteki.";
                else if (!Directory.Exists(LibraryRootPath)) StatusMessage = $"Uwaga: Folder biblioteki '{LibraryRootPath}' nie istnieje.";
                else StatusMessage = "Gotowy.";
                SimpleFileLogger.Log("ViewModel: InitializeAsync koniec.");
            }, "Inicjalizacja aplikacji");

        public async Task OnAppClosingAsync()
        {
            SimpleFileLogger.Log("ViewModel: OnAppClosingAsync - Anulowanie operacji i zapis ustawień...");
            if (_activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested)
            {
                _activeLongOperationCts.Cancel();
                try
                {
                    // Give a short time for the operation to acknowledge cancellation
                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1), _activeLongOperationCts.Token), Task.Run(() => { }));
                }
                catch (OperationCanceledException) { /* Expected */ }
                _activeLongOperationCts.Dispose();
                _activeLongOperationCts = null;
            }
            await _settingsService.SaveSettingsAsync(GetCurrentSettings());
            SimpleFileLogger.Log("ViewModel: OnAppClosingAsync - Ustawienia zapisane.");
        }

        private bool CanExecuteLoadProfiles(object? arg) => !IsBusy;
        private bool CanExecuteSaveAllProfiles(object? arg) => !IsBusy && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles);
        private bool CanExecuteAutoCreateProfiles(object? arg) => !IsBusy && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath);
        private bool CanExecuteGenerateProfile(object? parameter = null) => !IsBusy && !string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) && !string.IsNullOrWhiteSpace(ModelNameInput) && !string.IsNullOrWhiteSpace(CharacterNameInput) && ImageFiles.Any();

        // SuggestImagesCommand (Global Scan)
        private bool CanExecuteSuggestImages(object? parameter = null) => !IsBusy && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles) && !string.IsNullOrWhiteSpace(SourceFolderNamesInput);

        private bool CanExecuteRemoveProfile(object? parameter) => !IsBusy && (parameter is CategoryProfile || SelectedProfile != null);

        // CheckCharacterSuggestionsCommand (Character Context Menu)
        private bool CanExecuteCheckCharacterSuggestions(object? parameter) =>
            !IsBusy && (parameter is CategoryProfile profile ? profile : SelectedProfile) != null &&
            !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) &&
            !string.IsNullOrWhiteSpace(SourceFolderNamesInput) &&
            (parameter is CategoryProfile p ? (p.CentroidEmbedding != null && p.PendingSuggestionsCount > 0) : (SelectedProfile?.CentroidEmbedding != null && SelectedProfile?.PendingSuggestionsCount > 0));

        // MatchModelSpecificCommand (Model Context Menu)
        private bool CanExecuteMatchModelSpecific(object? parameter)
        {
            if (IsBusy) return false;
            if (!(parameter is ModelDisplayViewModel modelVM)) return false;
            return !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) &&
                   modelVM.HasCharacterProfiles && !string.IsNullOrWhiteSpace(SourceFolderNamesInput) &&
                   modelVM.PendingSuggestionsCount > 0; // Only enable if there are pending suggestions for this model
        }

        private bool CanExecuteRemoveModelTree(object? parameter) => !IsBusy && parameter is ModelDisplayViewModel;
        private bool CanExecuteSaveAppSettings(object? arg) => !IsBusy;
        private bool CanExecuteAddFilesToProfile(object? arg) => !IsBusy;
        private bool CanExecuteClearFilesFromProfile(object? arg) => !IsBusy && ImageFiles.Any();
        private bool CanExecuteCreateNewProfileSetup(object? arg) => !IsBusy;
        private bool CanExecuteSelectLibraryPath(object? arg) => !IsBusy;
        private bool CanExecuteAnalyzeModelForSplitting(object? parameter) => !IsBusy && parameter is ModelDisplayViewModel modelVM && modelVM.HasCharacterProfiles;
        private bool CanExecuteOpenSplitProfileDialog(object? parameter) => !IsBusy && parameter is CategoryProfile characterProfile && characterProfile.HasSplitSuggestion;
        private bool CanExecuteCancelCurrentOperation(object? parameter) => IsBusy && _activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested;
        private bool CanExecuteEnsureThumbnailsLoaded(object? parameter) => !IsBusy && parameter is IEnumerable<ImageFileEntry> images && images.Any();
        private bool CanExecuteRemoveDuplicatesInModel(object? parameter)
        {
            if (parameter is ModelDisplayViewModel modelVM)
            {
                return !IsBusy && modelVM.HasCharacterProfiles;
            }
            return false;
        }
        private bool CanExecuteApplyAllMatchesForModel(object? parameter)
        {
            if (parameter is ModelDisplayViewModel modelVM)
            {
                // Check if there are any suggestions specifically cached for this model OR if global suggestions exist that might apply to this model
                bool hasApplicableSuggestions = (_lastScannedModelNameForSuggestions == modelVM.ModelName && _lastModelSpecificSuggestions.Any(m => m.Similarity >= SuggestionSimilarityThreshold && _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelVM.ModelName)) ||
                                                (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) && _lastModelSpecificSuggestions.Any(m => m.Similarity >= SuggestionSimilarityThreshold && _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelVM.ModelName));
                return !IsBusy && modelVM.HasCharacterProfiles && modelVM.PendingSuggestionsCount > 0 && hasApplicableSuggestions;
            }
            return false;
        }


        private Task ExecuteLoadProfilesAsync(object? parameter = null) =>
            RunLongOperation(InternalExecuteLoadProfilesAsync, "Ładowanie profili");

        private async Task InternalExecuteLoadProfilesAsync(CancellationToken token)
        {
            SimpleFileLogger.Log($"InternalExecuteLoadProfilesAsync. RefreshFlag: {_isRefreshingProfilesPostMove}. Token: {token.GetHashCode()}");
            if (!_isRefreshingProfilesPostMove) // Only clear if not a post-move refresh
            {
                // Don't clear _lastModelSpecificSuggestions if it might contain global scan results not yet acted upon.
                // ClearModelSpecificSuggestionsCache(); // This was too aggressive.
            }
            token.ThrowIfCancellationRequested();
            string? prevSelectedName = SelectedProfile?.CategoryName;
            await _profileService.LoadProfilesAsync();
            token.ThrowIfCancellationRequested();
            var flatProfiles = _profileService.GetAllProfiles()?.OrderBy(p => p.CategoryName).ToList();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HierarchicalProfilesList.Clear();
                if (flatProfiles?.Any() == true)
                {
                    var grouped = flatProfiles.GroupBy(p => _profileService.GetModelNameFromCategory(p.CategoryName)).OrderBy(g => g.Key);
                    foreach (var modelGroup in grouped)
                    {
                        if (token.IsCancellationRequested) return;
                        var modelVM = new ModelDisplayViewModel(modelGroup.Key);
                        foreach (var charProfile in modelGroup.OrderBy(p => _profileService.GetCharacterNameFromCategory(p.CategoryName)))
                        {
                            modelVM.AddCharacterProfile(charProfile);
                        }
                        HierarchicalProfilesList.Add(modelVM);
                    }
                }
                StatusMessage = $"Załadowano {HierarchicalProfilesList.Sum(m => m.CharacterProfiles.Count)} profili dla {HierarchicalProfilesList.Count} modelek.";
                SimpleFileLogger.Log($"Wątek UI: Załadowano profile. Status: {StatusMessage}");

                if (!string.IsNullOrEmpty(prevSelectedName))
                {
                    SelectedProfile = flatProfiles?.FirstOrDefault(p => p.CategoryName.Equals(prevSelectedName, StringComparison.OrdinalIgnoreCase));
                }
                else if (SelectedProfile != null && !(flatProfiles?.Any(p => p.CategoryName == SelectedProfile.CategoryName) ?? false))
                {
                    SelectedProfile = null;
                }
                OnPropertyChanged(nameof(AnyProfilesLoaded));

                // Refresh pending counts if we have cached suggestions (either global or model-specific)
                if (_lastModelSpecificSuggestions.Any())
                {
                    RefreshPendingSuggestionCountsFromCache();
                }
            });
        }

        private Task ExecuteGenerateProfileAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                bool profilesActuallyRegenerated = false;
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) ||
                    string.IsNullOrWhiteSpace(ModelNameInput) ||
                    string.IsNullOrWhiteSpace(CharacterNameInput))
                {
                    StatusMessage = "Błąd: Nazwa modelki i postaci oraz pełna nazwa profilu muszą być zdefiniowane.";
                    MessageBox.Show(StatusMessage, "Błąd danych profilu", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                string catName = CurrentProfileNameForEdit;
                SimpleFileLogger.Log($"Generowanie profilu '{catName}' ({ImageFiles.Count} obr.). Token: {token.GetHashCode()}");

                List<ImageFileEntry> entriesToProcess = new List<ImageFileEntry>();
                foreach (var file in ImageFiles)
                {
                    token.ThrowIfCancellationRequested();
                    // Ensure metadata (filesize, last modified) is fresh if it's missing
                    if (file.FileSize == 0 || file.FileLastModifiedUtc == DateTime.MinValue)
                    {
                        var updatedEntry = await _imageMetadataService.ExtractMetadataAsync(file.FilePath);
                        if (updatedEntry != null)
                        {
                            entriesToProcess.Add(updatedEntry);
                        }
                        else
                        {
                            SimpleFileLogger.LogWarning($"ExecuteGenerateProfileAsync: Nie udało się załadować metadanych dla {file.FilePath} (z listy ImageFiles), pomijam.");
                        }
                    }
                    else
                    {
                        entriesToProcess.Add(file);
                    }
                }
                token.ThrowIfCancellationRequested();
                if (!entriesToProcess.Any() && ImageFiles.Any()) // If original list had files but none could be processed
                {
                    StatusMessage = "Błąd: Nie udało się przetworzyć żadnego z wybranych plików (brak metadanych).";
                    MessageBox.Show(StatusMessage, "Błąd przetwarzania plików", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await _profileService.GenerateProfileAsync(catName, entriesToProcess);
                profilesActuallyRegenerated = true;

                token.ThrowIfCancellationRequested();
                StatusMessage = $"Profil '{catName}' wygenerowany/zaktualizowany.";

                if (profilesActuallyRegenerated)
                {
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                    SelectedProfile = _profileService.GetProfile(catName); // Reselect the profile
                }
            }, "Generowanie profilu");

        private Task ExecuteSaveAllProfilesAsync(object? parameter = null) =>
           RunLongOperation(async token =>
           {
               SimpleFileLogger.Log($"Zapis wszystkich profili. Token: {token.GetHashCode()}");
               await _profileService.SaveAllProfilesAsync(); // This already saves embedding cache
               token.ThrowIfCancellationRequested();
               StatusMessage = "Wszystkie profile i cache embeddingów zapisane.";
               MessageBox.Show(StatusMessage, "Zapisano", MessageBoxButton.OK, MessageBoxImage.Information);
           }, "Zapisywanie wszystkich profili");

        private Task ExecuteRemoveProfileAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                bool profileActuallyRemoved = false;
                var profileToRemove = (parameter as CategoryProfile) ?? SelectedProfile;
                if (profileToRemove == null) { MessageBox.Show("Wybierz profil do usunięcia z listy.", "Brak wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();
                if (MessageBox.Show($"Czy na pewno chcesz usunąć profil '{profileToRemove.CategoryName}'?\nTa operacja usunie tylko definicję profilu, nie pliki graficzne z dysku.", "Potwierdź usunięcie profilu", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    string name = profileToRemove.CategoryName;
                    SimpleFileLogger.Log($"Usuwanie profilu '{name}'. Token: {token.GetHashCode()}");
                    if (await _profileService.RemoveProfileAsync(name))
                    {
                        StatusMessage = $"Profil '{name}' usunięty.";
                        if (SelectedProfile?.CategoryName == name) SelectedProfile = null; // Deselect if it was the selected one
                        profileActuallyRemoved = true;
                    }
                    else StatusMessage = $"Nie udało się usunąć profilu '{name}'.";
                }

                if (profileActuallyRemoved)
                {
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }
            }, "Usuwanie profilu");

        private async void ExecuteAddFilesToProfile(object? parameter = null)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "Obrazy|*.jpg;*.jpeg;*.png;*.webp|Wszystkie pliki|*.*", Title = "Wybierz obrazy do dodania", Multiselect = true };
            if (openFileDialog.ShowDialog() == true)
            {
                IsBusy = true; // Manually set IsBusy as this is not a RunLongOperation
                StatusMessage = "Dodawanie plików i ładowanie metadanych...";
                int addedCount = 0;
                try
                {
                    foreach (string filePath in openFileDialog.FileNames)
                    {
                        if (!ImageFiles.Any(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            var entry = await _imageMetadataService.ExtractMetadataAsync(filePath);
                            if (entry != null)
                            {
                                ImageFiles.Add(entry); // Add to the UI list for the current profile being edited
                                addedCount++;
                            }
                            else
                            {
                                SimpleFileLogger.LogWarning($"ExecuteAddFilesToProfile: Nie udało się załadować metadanych dla pliku: {filePath}, pominięto.");
                            }
                        }
                    }
                    if (addedCount > 0) StatusMessage = $"Dodano {addedCount} plików. Miniaturki można załadować osobno.";
                    else StatusMessage = "Nie dodano nowych plików (mogły już istnieć na liście).";
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError("Błąd podczas dodawania plików do profilu.", ex);
                    StatusMessage = "Błąd podczas dodawania plików.";
                    MessageBox.Show($"Wystąpił błąd: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        private void ExecuteClearFilesFromProfile(object? parameter = null) => ImageFiles.Clear();
        private void ExecuteCreateNewProfileSetup(object? parameter = null)
        {
            SelectedProfile = null; // This will trigger UpdateEditFieldsFromSelectedProfile to clear fields
            ModelNameInput = string.Empty; // Explicitly clear
            CharacterNameInput = string.Empty; // Explicitly clear
            ImageFiles.Clear(); // Ensure this is also cleared
            StatusMessage = "Gotowy do utworzenia nowego profilu. Wprowadź nazwę modelki i postaci.";
        }
        private void ExecuteSelectLibraryPath(object? parameter = null)
        {
            // This method is synchronous and uses a dialog, so direct IsBusy management is okay.
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog { Description = "Wybierz główny folder biblioteki dla Cosplay Managera", UseDescriptionForTitle = true, ShowNewFolderButton = true };
                if (!string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath)) dialog.SelectedPath = LibraryRootPath;
                else if (Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))) dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

                if (dialog.ShowDialog(Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive)) == true)
                {
                    LibraryRootPath = dialog.SelectedPath; // Setter will clear suggestion cache
                    StatusMessage = $"Wybrano folder biblioteki: {LibraryRootPath}";
                }
            }
            catch (Exception ex) { SimpleFileLogger.LogError("Błąd wyboru folderu biblioteki przez użytkownika", ex); MessageBox.Show($"Błąd podczas otwierania dialogu wyboru folderu: {ex.Message}", "Błąd Dialogu", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { IsBusy = false; }
        }

        private Task ExecuteAutoCreateProfilesAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                SimpleFileLogger.Log($"AutoCreateProfiles: Rozpoczęto skanowanie folderu biblioteki: {LibraryRootPath}. Token: {token.GetHashCode()}");
                var mixedFoldersToIgnore = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                token.ThrowIfCancellationRequested();
                int totalProfilesCreatedOrUpdated = 0;
                bool anyProfileDataChangedDuringOperation = false;
                List<string> modelDirectories;
                try
                {
                    modelDirectories = Directory.GetDirectories(LibraryRootPath).ToList();
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"Błąd pobierania folderów modelek z '{LibraryRootPath}' podczas AutoCreateProfiles", ex);
                    StatusMessage = $"Błąd dostępu do folderu biblioteki: {ex.Message}";
                    MessageBox.Show(StatusMessage, "Błąd Biblioteki", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                token.ThrowIfCancellationRequested();

                foreach (var modelDir in modelDirectories)
                {
                    token.ThrowIfCancellationRequested();
                    string currentModelName = Path.GetFileName(modelDir);
                    if (string.IsNullOrWhiteSpace(currentModelName) || mixedFoldersToIgnore.Contains(currentModelName))
                    {
                        SimpleFileLogger.Log($"AutoCreateProfiles: Pomijanie folderu na poziomie biblioteki: '{currentModelName}' (może być folderem Mix lub ignorowanym).");
                        continue;
                    }

                    int profilesChangedForThisModel = await InternalProcessDirectoryForProfileCreationAsync(
                        modelDir,
                        currentModelName, // Pass the top-level directory name as the model name
                        new List<string>(), // Initial parent character path is empty
                        mixedFoldersToIgnore,
                        token);

                    if (profilesChangedForThisModel > 0)
                    {
                        anyProfileDataChangedDuringOperation = true;
                        totalProfilesCreatedOrUpdated += profilesChangedForThisModel;
                    }
                }
                token.ThrowIfCancellationRequested();
                StatusMessage = $"Automatyczne tworzenie profili zakończone. Utworzono/zaktualizowano: {totalProfilesCreatedOrUpdated} profili.";

                if (anyProfileDataChangedDuringOperation)
                {
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token); // Refresh the UI list
                    _isRefreshingProfilesPostMove = false;
                }
                MessageBox.Show(StatusMessage, "Skanowanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "Automatyczne tworzenie profili");

        // Processes a directory and its subdirectories to create/update character profiles under a given model.
        private async Task<int> InternalProcessDirectoryForProfileCreationAsync(
            string currentDirectoryPath,      // The current directory being processed
            string modelNameForProfile,       // The model name this directory structure belongs to
            List<string> parentCharacterPathParts, // Path parts from parent directories (e.g., ["Outfit A", "Variant 1"])
            HashSet<string> mixedFoldersToIgnore, // Folders like "Mix" to ignore for character path creation
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int profilesGeneratedOrUpdatedThisCall = 0;
            string currentSegmentName = Path.GetFileName(currentDirectoryPath); // e.g., "Character X", "Outfit A", "Variant 1", or even "Mix"

            var currentCharacterPathSegments = new List<string>(parentCharacterPathParts);

            // Add current segment to path only if it's not a "Mix" folder and not the root model folder itself
            // The root model folder (e.g., "LibraryRoot/ModelName") images go to "ModelName - General"
            string modelRootForCurrentModel = Path.Combine(LibraryRootPath, modelNameForProfile);
            if (!currentDirectoryPath.Equals(modelRootForCurrentModel, StringComparison.OrdinalIgnoreCase) &&
                !mixedFoldersToIgnore.Contains(currentSegmentName))
            {
                currentCharacterPathSegments.Add(currentSegmentName);
            }

            // Construct the character name from the path segments
            string characterFullName = string.Join(" - ", currentCharacterPathSegments);
            string categoryName;

            if (string.IsNullOrWhiteSpace(characterFullName)) // This happens for images directly in "LibraryRoot/ModelName"
            {
                categoryName = $"{modelNameForProfile} - General";
            }
            else
            {
                categoryName = $"{modelNameForProfile} - {characterFullName}";
            }

            SimpleFileLogger.Log($"InternalProcessDir: Przetwarzanie folderu '{currentDirectoryPath}'. Nazwa modelki: '{modelNameForProfile}'. Ścieżka postaci: '{characterFullName}'. Wynikowa kategoria: '{categoryName}'.");

            List<string> imagePathsInThisExactDirectory = new List<string>();
            try
            {
                imagePathsInThisExactDirectory = Directory.GetFiles(currentDirectoryPath, "*.*", SearchOption.TopDirectoryOnly)
                                           .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f)))
                                           .ToList();
            }
            catch (Exception ex) { SimpleFileLogger.LogWarning($"InternalProcessDir: Błąd odczytu plików z '{currentDirectoryPath}': {ex.Message}"); }
            token.ThrowIfCancellationRequested();

            if (imagePathsInThisExactDirectory.Any())
            {
                var entriesForProfile = new List<ImageFileEntry>();
                foreach (var path in imagePathsInThisExactDirectory)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                    if (entry != null) entriesForProfile.Add(entry);
                }
                token.ThrowIfCancellationRequested();
                if (entriesForProfile.Any())
                {
                    await _profileService.GenerateProfileAsync(categoryName, entriesForProfile);
                    profilesGeneratedOrUpdatedThisCall++;
                    SimpleFileLogger.Log($"InternalProcessDir: Wygenerowano/zaktualizowano profil '{categoryName}' z {entriesForProfile.Count} obrazami z folderu '{currentDirectoryPath}'.");
                }
            }
            // If the profile exists but the folder is now empty, clear the profile (remove its images)
            else if (_profileService.GetProfile(categoryName) != null &&
                     (categoryName.Equals($"{modelNameForProfile} - General", StringComparison.OrdinalIgnoreCase) || // For "Model - General"
                      !string.IsNullOrWhiteSpace(characterFullName))) // For specific character subfolders
            {
                // Only clear if it's not a "Mix" folder itself that we're processing for profile generation
                if (!mixedFoldersToIgnore.Contains(Path.GetFileName(currentDirectoryPath)))
                {
                    await _profileService.GenerateProfileAsync(categoryName, new List<ImageFileEntry>()); // Pass empty list to clear
                    SimpleFileLogger.Log($"InternalProcessDir: Profil '{categoryName}' istniał, ale folder '{currentDirectoryPath}' jest pusty. Profil wyczyszczony (usunięto obrazy z profilu).");
                    profilesGeneratedOrUpdatedThisCall++; // Counts as an update
                }
            }
            token.ThrowIfCancellationRequested();

            // Recursively process subdirectories
            try
            {
                foreach (var subDirectoryPath in Directory.GetDirectories(currentDirectoryPath))
                {
                    token.ThrowIfCancellationRequested();
                    string subDirectoryName = Path.GetFileName(subDirectoryPath);
                    // Pass the *same* modelNameForProfile, but updated parentCharacterPathParts
                    profilesGeneratedOrUpdatedThisCall += await InternalProcessDirectoryForProfileCreationAsync(
                        subDirectoryPath,
                        modelNameForProfile, // Model name remains constant for this branch
                        new List<string>(currentCharacterPathSegments), // Pass down the current character path
                        mixedFoldersToIgnore,
                        token);
                }
            }
            catch (OperationCanceledException) { throw; } // Propagate cancellation
            catch (Exception ex) { SimpleFileLogger.LogError($"InternalProcessDir: Błąd przetwarzania podfolderów dla '{currentDirectoryPath}'", ex); }

            return profilesGeneratedOrUpdatedThisCall;
        }

        private bool IsImageBetter(ImageFileEntry entry1, ImageFileEntry entry2)
        {
            if (entry1 == null || entry2 == null) return false; // Should not happen if called correctly

            // Compare resolution first
            long resolution1 = (long)entry1.Width * entry1.Height;
            long resolution2 = (long)entry2.Width * entry2.Height;

            if (resolution1 > resolution2) return true;
            if (resolution1 < resolution2) return false;

            // If resolutions are equal, compare file size (larger is generally better, assuming similar compression)
            return entry1.FileSize > entry2.FileSize;
        }

        // This method updates profile definitions in memory and triggers save if files are moved/deleted.
        private async Task<bool> HandleFileMovedOrDeletedUpdateProfilesAsync(string? oldPath, string? newPathIfMoved, string? targetCategoryNameIfMoved, CancellationToken token)
        {
            SimpleFileLogger.Log($"[ProfileUpdate] Rozpoczęto aktualizację profili. OldPath='{oldPath}', NewPath='{newPathIfMoved}', TargetCat='{targetCategoryNameIfMoved}'");
            var affectedProfileNamesForRegeneration = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyProfileDataActuallyRegenerated = false;

            // Iterate over a snapshot of profiles, as the underlying collection might change if profiles are removed.
            var allProfilesInMemory = _profileService.GetAllProfiles().ToList();

            foreach (var profile in allProfilesInMemory)
            {
                token.ThrowIfCancellationRequested();
                bool currentProfileNeedsRegeneration = false;

                // If an old path is provided (meaning a file was moved from here or deleted)
                if (!string.IsNullOrWhiteSpace(oldPath) && profile.SourceImagePaths != null)
                {
                    if (profile.SourceImagePaths.Any(p => p.Equals(oldPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        int removedCount = profile.SourceImagePaths.RemoveAll(p => p.Equals(oldPath, StringComparison.OrdinalIgnoreCase));
                        if (removedCount > 0)
                        {
                            SimpleFileLogger.Log($"[ProfileUpdate] Usunięto '{oldPath}' ({removedCount}x) z SourceImagePaths (w pamięci) profilu '{profile.CategoryName}'.");
                            currentProfileNeedsRegeneration = true;
                        }
                    }
                }

                // If a new path is provided (meaning a file was moved here)
                if (!string.IsNullOrWhiteSpace(newPathIfMoved) &&
                    !string.IsNullOrWhiteSpace(targetCategoryNameIfMoved) &&
                    profile.CategoryName.Equals(targetCategoryNameIfMoved, StringComparison.OrdinalIgnoreCase))
                {
                    if (profile.SourceImagePaths == null) profile.SourceImagePaths = new List<string>();

                    if (!profile.SourceImagePaths.Any(p => p.Equals(newPathIfMoved, StringComparison.OrdinalIgnoreCase)))
                    {
                        profile.SourceImagePaths.Add(newPathIfMoved);
                        SimpleFileLogger.Log($"[ProfileUpdate] Dodano '{newPathIfMoved}' do SourceImagePaths (w pamięci) profilu '{profile.CategoryName}'.");
                        currentProfileNeedsRegeneration = true;
                    }
                }

                if (currentProfileNeedsRegeneration)
                {
                    affectedProfileNamesForRegeneration.Add(profile.CategoryName);
                }
            }
            token.ThrowIfCancellationRequested();

            if (affectedProfileNamesForRegeneration.Any())
            {
                SimpleFileLogger.Log($"[ProfileUpdate] Zidentyfikowano {affectedProfileNamesForRegeneration.Count} profili do regeneracji (aktualizacji centroidu i zapisu JSON): {string.Join(", ", affectedProfileNamesForRegeneration)}");

                foreach (var affectedName in affectedProfileNamesForRegeneration)
                {
                    token.ThrowIfCancellationRequested();
                    var affectedProfile = _profileService.GetProfile(affectedName); // Get the potentially modified profile from service
                    if (affectedProfile == null)
                    {
                        // This could happen if the profile became empty and was removed by another logic path.
                        SimpleFileLogger.LogWarning($"[ProfileUpdate] Nie można znaleźć profilu '{affectedName}' w ProfileService do regeneracji (mógł zostać usunięty).");
                        continue;
                    }

                    // Prepare list of valid ImageFileEntry for regeneration
                    var entriesForAffectedProfile = new List<ImageFileEntry>();
                    if (affectedProfile.SourceImagePaths != null)
                    {
                        foreach (var path in affectedProfile.SourceImagePaths)
                        {
                            token.ThrowIfCancellationRequested();
                            // Only add if file exists, as it might have been deleted by another part of the operation
                            if (File.Exists(path))
                            {
                                var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                                if (entry != null) entriesForAffectedProfile.Add(entry);
                            }
                            else
                            {
                                SimpleFileLogger.LogWarning($"[ProfileUpdate] Ścieżka '{path}' w profilu '{affectedName}' nie istnieje na dysku podczas przygotowywania do regeneracji.");
                            }
                        }
                    }
                    SimpleFileLogger.Log($"[ProfileUpdate] Regenerowanie (GenerateProfileAsync) profilu '{affectedName}' z {entriesForAffectedProfile.Count} obrazami.");
                    await _profileService.GenerateProfileAsync(affectedName, entriesForAffectedProfile); // This will update centroid and save
                    anyProfileDataActuallyRegenerated = true;
                }
                SimpleFileLogger.Log($"[ProfileUpdate] Zakończono regenerację dla {affectedProfileNamesForRegeneration.Count} profili.");
            }
            else
            {
                SimpleFileLogger.Log($"[ProfileUpdate] Nie zidentyfikowano profili wymagających regeneracji dla operacji (oldPath: '{oldPath}').");
            }
            return anyProfileDataActuallyRegenerated;
        }

        // Handles suggestions for a single source image against a single target profile
        // Returns the proposed move (if any), whether an auto-action was handled, and if profiles were modified.
        private async Task<(Models.ProposedMove? proposedMove, bool wasActionAutoHandled, bool profilesWereModified)> ProcessDuplicateOrSuggestNewAsync(
            ImageFileEntry sourceImageEntry,    // The image from "Mix" or other source
            CategoryProfile targetProfile,      // The character profile it's being compared against
            double similarityToCentroid,        // Similarity of source image to targetProfile's centroid
            string modelDirectoryPath,          // Root path for the current model (e.g., "Library/ModelName")
            float[] sourceImageEmbedding,       // Embedding of the source image
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            bool profilesModifiedByThisCall = false;

            var (_, characterFolderNamePart) = ParseCategoryName(targetProfile.CategoryName);
            string targetCharacterFolderPath = Path.Combine(modelDirectoryPath, SanitizeFolderName(characterFolderNamePart));
            Directory.CreateDirectory(targetCharacterFolderPath); // Ensure target character folder exists

            // Check for graphic duplicates already in the target character folder
            List<string> filesInTargetDir;
            try
            {
                filesInTargetDir = Directory.EnumerateFiles(targetCharacterFolderPath, "*.*", SearchOption.TopDirectoryOnly)
                                        .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f))).ToList();
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"ProcessDuplicateOrSuggestNewAsync: Błąd odczytu plików z folderu docelowego: {targetCharacterFolderPath}", ex);
                filesInTargetDir = new List<string>(); // Default to empty if error
            }


            foreach (string existingFilePathInTarget in filesInTargetDir)
            {
                token.ThrowIfCancellationRequested();
                // Don't compare the file with itself if, for some reason, it's already in the target and also in Mix
                if (string.Equals(Path.GetFullPath(existingFilePathInTarget), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var existingTargetEntry = await _imageMetadataService.ExtractMetadataAsync(existingFilePathInTarget);
                if (existingTargetEntry == null) continue;

                // Get embedding for the existing file in target (use cache)
                float[]? existingTargetEmbedding = existingTargetEntry.FeatureVector ?? await _profileService.GetImageEmbeddingAsync(existingTargetEntry);
                if (existingTargetEmbedding == null)
                {
                    SimpleFileLogger.LogWarning($"[ProcessDuplicateOrSuggestNewAsync] Nie udało się pobrać embeddingu dla istniejącego pliku w folderze docelowym: {existingTargetEntry.FilePath}");
                    continue;
                }

                double similarityBetweenSourceAndExisting = Utils.MathUtils.CalculateCosineSimilarity(sourceImageEmbedding, existingTargetEmbedding);

                if (similarityBetweenSourceAndExisting >= DUPLICATE_SIMILARITY_THRESHOLD)
                {
                    // We found a graphic duplicate. Decide which one to keep.
                    bool sourceIsCurrentlyBetter = IsImageBetter(sourceImageEntry, existingTargetEntry);

                    if (sourceIsCurrentlyBetter)
                    {
                        // Source is better, replace existing in target
                        SimpleFileLogger.Log($"[AutoReplace] Znaleziono lepszą wersję. Źródło: '{sourceImageEntry.FilePath}' ({sourceImageEntry.Width}x{sourceImageEntry.Height}, {sourceImageEntry.FileSize}B). Cel: '{existingTargetEntry.FilePath}' ({existingTargetEntry.Width}x{existingTargetEntry.Height}, {existingTargetEntry.FileSize}B).");
                        try
                        {
                            // It's critical to update the profile if the file it references (existingTargetEntry.FilePath) is overwritten
                            // However, the content changes, so its embedding might too.
                            // For now, we assume the target profile will re-evaluate if needed.
                            File.Copy(sourceImageEntry.FilePath, existingTargetEntry.FilePath, true); // Overwrite
                            SimpleFileLogger.Log($"[AutoReplace] Nadpisano '{existingTargetEntry.FilePath}' plikiem '{sourceImageEntry.FilePath}'.");

                            string oldSourcePath = sourceImageEntry.FilePath; // Path of the file that was in Mix
                            File.Delete(sourceImageEntry.FilePath); // Delete from Mix
                            SimpleFileLogger.Log($"[AutoReplace] Usunięto oryginalny plik źródłowy '{oldSourcePath}'.");

                            // The file 'oldSourcePath' is gone. Update any profile that referenced it.
                            // The file 'existingTargetEntry.FilePath' has new content, its profile might need update,
                            // but its path remains. For simplicity, we only signal removal from old path.
                            // The target profile will naturally re-calculate with the new content if it's part of its source images.
                            if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldSourcePath, null, null, token)) // oldSourcePath is gone, no new path for it
                                profilesModifiedByThisCall = true;
                        }
                        catch (Exception ex)
                        {
                            SimpleFileLogger.LogError($"[AutoReplace] Błąd podczas automatycznego zastępowania lepszym zdjęciem: {sourceImageEntry.FilePath} -> {existingTargetEntry.FilePath}", ex);
                        }
                        return (null, true, profilesModifiedByThisCall); // Auto-handled, no suggestion needed
                    }
                    else
                    {
                        // Existing in target is better or equal, delete source from Mix
                        SimpleFileLogger.Log($"[AutoDeleteSource] Istniejąca wersja w '{targetCharacterFolderPath}' jest lepsza/równa od '{sourceImageEntry.FilePath}'. Usuwanie pliku źródłowego.");
                        try
                        {
                            string oldSourcePath = sourceImageEntry.FilePath;
                            File.Delete(sourceImageEntry.FilePath);
                            SimpleFileLogger.Log($"[AutoDeleteSource] Usunięto plik źródłowy '{oldSourcePath}'.");
                            if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldSourcePath, null, null, token)) // oldSourcePath is gone
                                profilesModifiedByThisCall = true;
                        }
                        catch (Exception ex)
                        {
                            SimpleFileLogger.LogError($"[AutoDeleteSource] Błąd podczas automatycznego usuwania gorszego/równego zdjęcia ze źródła: {sourceImageEntry.FilePath}", ex);
                        }
                        return (null, true, profilesModifiedByThisCall); // Auto-handled
                    }
                }
            }
            token.ThrowIfCancellationRequested();

            // If no exact graphic duplicate was found and auto-handled, consider similarity to centroid
            if (similarityToCentroid >= SuggestionSimilarityThreshold)
            {
                string proposedPathStandard = Path.Combine(targetCharacterFolderPath, sourceImageEntry.FileName);
                ProposedMoveActionType actionStandard;
                ImageFileEntry? displayTargetStandard = null;

                if (File.Exists(proposedPathStandard) &&
                    !string.Equals(Path.GetFullPath(proposedPathStandard), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase)) // File with same name exists at target
                {
                    actionStandard = ProposedMoveActionType.ConflictKeepBoth; // Default to keep both if name conflict
                    displayTargetStandard = await _imageMetadataService.ExtractMetadataAsync(proposedPathStandard);
                }
                else if (string.Equals(Path.GetFullPath(proposedPathStandard), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    // Source is already in the correct target location, no move needed.
                    SimpleFileLogger.Log($"[Suggest] Plik '{sourceImageEntry.FilePath}' jest już w docelowej lokalizacji '{proposedPathStandard}' i nie jest duplikatem o innej jakości. Brak sugestii.");
                    return (null, false, profilesModifiedByThisCall); // No move, not auto-handled this path
                }
                else // No file with same name at target, or source is not already there
                {
                    actionStandard = ProposedMoveActionType.CopyNew;
                }

                var move = new Models.ProposedMove(sourceImageEntry, displayTargetStandard, proposedPathStandard, similarityToCentroid, targetProfile.CategoryName, actionStandard, sourceImageEmbedding);
                SimpleFileLogger.Log($"[Suggest] Utworzono sugestię: {actionStandard} dla '{sourceImageEntry.FileName}' do '{targetProfile.CategoryName}', SimToCentroid: {similarityToCentroid:F4}");
                return (move, false, profilesModifiedByThisCall);
            }

            // No graphic duplicate and not similar enough to centroid
            SimpleFileLogger.Log($"[Suggest] Plik '{sourceImageEntry.FileName}' (SimToCentroid: {similarityToCentroid:F4}) nie pasuje wystarczająco ({SuggestionSimilarityThreshold:F2}) do profilu '{targetProfile.CategoryName}' i nie jest duplikatem graficznym.");
            return (null, false, profilesModifiedByThisCall);
        }


        // For Model context menu: MatchModelSpecificCommand
        private Task ExecuteMatchModelSpecificAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nie wybrano modelki z listy."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                SimpleFileLogger.Log($"MatchModelSpecific dla '{modelVM.ModelName}'. Token: {token.GetHashCode()}");
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe (np. 'Mix') w ustawieniach."; MessageBox.Show(StatusMessage, "Brak Folderów Źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                var movesForSuggestionWindow = new List<Models.ProposedMove>();
                string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName); // e.g., "Library/ModelName"
                if (!Directory.Exists(modelPath))
                {
                    StatusMessage = $"Błąd: Folder dla modelki '{modelVM.ModelName}' nie istnieje: {modelPath}";
                    MessageBox.Show(StatusMessage, "Błąd Folderu Modelki", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Reset pending suggestions for this model in UI before scan
                await Application.Current.Dispatcher.InvokeAsync(() => {
                    modelVM.PendingSuggestionsCount = 0;
                    foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0;
                });
                token.ThrowIfCancellationRequested();
                int filesFoundInMix = 0, filesWithEmbeddings = 0, autoActionsCount = 0;
                bool anyProfileDataChangedDuringEntireOperation = false;
                var alreadySuggestedGraphicDuplicates = new List<(float[] embedding, string targetCategoryName, string sourceFilePath)>();


                foreach (var mixFolderName in mixedFolders) // e.g., "Mix", "Unsorted"
                {
                    token.ThrowIfCancellationRequested();
                    string currentMixPath = Path.Combine(modelPath, mixFolderName); // e.g., "Library/ModelName/Mix"
                    if (Directory.Exists(currentMixPath))
                    {
                        // ScanDirectoryAsync now correctly scans subdirectories of currentMixPath if any.
                        var imagePathsInMix = await _fileScannerService.ScanDirectoryAsync(currentMixPath);
                        filesFoundInMix += imagePathsInMix.Count;
                        SimpleFileLogger.Log($"MatchModelSpecific: W folderze '{currentMixPath}' (i podfolderach) znaleziono {imagePathsInMix.Count} obrazów.");

                        foreach (var imgPathFromMix in imagePathsInMix)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!File.Exists(imgPathFromMix)) // File might have been moved/deleted by a previous step
                            {
                                SimpleFileLogger.Log($"MatchModelSpecific: Plik '{imgPathFromMix}' już nie istnieje, pomijam.");
                                continue;
                            }
                            var sourceEntry = await _imageMetadataService.ExtractMetadataAsync(imgPathFromMix);
                            if (sourceEntry == null) { SimpleFileLogger.LogWarning($"MatchModelSpecific: Nie udało się załadować metadanych dla: {imgPathFromMix}, pomijam."); continue; }

                            var sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceEntry);
                            if (sourceEmbedding == null) { SimpleFileLogger.LogWarning($"MatchModelSpecific: Nie udało się załadować embeddingu dla: {sourceEntry.FilePath}, pomijam."); continue; }
                            filesWithEmbeddings++;
                            token.ThrowIfCancellationRequested();

                            // Suggest category only within the current modelVM.ModelName
                            var bestSuggestionForThisSourceImage = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName);

                            if (bestSuggestionForThisSourceImage != null)
                            {
                                CategoryProfile targetProfile = bestSuggestionForThisSourceImage.Item1; // This is a CategoryProfile object
                                double similarityToCentroid = bestSuggestionForThisSourceImage.Item2;

                                var (proposedMove, wasActionAutoHandled, profilesModifiedByCall) = await ProcessDuplicateOrSuggestNewAsync(
                                    sourceEntry,
                                    targetProfile,
                                    similarityToCentroid,
                                    modelPath, // Pass the root model directory
                                    sourceEmbedding,
                                    token);

                                if (profilesModifiedByCall) anyProfileDataChangedDuringEntireOperation = true;

                                if (wasActionAutoHandled)
                                {
                                    autoActionsCount++;
                                }
                                else if (proposedMove != null)
                                {
                                    // Filter out suggestions for graphically identical images already proposed to the same target category
                                    bool addThisSuggestionToWindow = true;
                                    if (proposedMove.SourceImageEmbedding != null &&
                                        (proposedMove.Action == ProposedMoveActionType.CopyNew || proposedMove.Action == ProposedMoveActionType.ConflictKeepBoth))
                                    {
                                        foreach (var (existingEmb, existingTargetCat, existingSourcePath) in alreadySuggestedGraphicDuplicates)
                                        {
                                            if (existingTargetCat.Equals(proposedMove.TargetCategoryProfileName, StringComparison.OrdinalIgnoreCase))
                                            {
                                                double simToExistingSuggested = Utils.MathUtils.CalculateCosineSimilarity(proposedMove.SourceImageEmbedding, existingEmb);
                                                if (simToExistingSuggested >= DUPLICATE_SIMILARITY_THRESHOLD)
                                                {
                                                    addThisSuggestionToWindow = false;
                                                    SimpleFileLogger.Log($"[SuggestWindowFilter] Pomijanie sugestii dla '{proposedMove.SourceImage.FilePath}', ponieważ graficznie identyczna sugestia (z '{existingSourcePath}') została już wygenerowana do '{existingTargetCat}'.");

                                                    // Auto-delete the worse one (current proposedMove.SourceImage)
                                                    string pathToDeleteForFilter = proposedMove.SourceImage.FilePath;
                                                    try
                                                    {
                                                        if (File.Exists(pathToDeleteForFilter))
                                                        {
                                                            File.Delete(pathToDeleteForFilter);
                                                            SimpleFileLogger.Log($"[SuggestWindowFilterCleanup] Usunięto plik źródłowy duplikatu sugestii: {pathToDeleteForFilter}");
                                                            if (await HandleFileMovedOrDeletedUpdateProfilesAsync(pathToDeleteForFilter, null, null, token))
                                                                anyProfileDataChangedDuringEntireOperation = true;
                                                        }
                                                    }
                                                    catch (Exception ex) { SimpleFileLogger.LogError($"[SuggestWindowFilterCleanup] Błąd usuwania {proposedMove.SourceImage.FilePath}: {ex.Message}", ex); }
                                                    break;
                                                }
                                            }
                                        }
                                    }

                                    if (addThisSuggestionToWindow)
                                    {
                                        movesForSuggestionWindow.Add(proposedMove);
                                        // Add to list for filtering subsequent suggestions if it's a copy/conflict type
                                        if (proposedMove.SourceImageEmbedding != null && (proposedMove.Action == ProposedMoveActionType.CopyNew || proposedMove.Action == ProposedMoveActionType.ConflictKeepBoth))
                                        {
                                            alreadySuggestedGraphicDuplicates.Add((proposedMove.SourceImageEmbedding, proposedMove.TargetCategoryProfileName, proposedMove.SourceImage.FilePath));
                                        }
                                    }
                                }
                            }
                            else
                            {
                                SimpleFileLogger.Log($"MatchModelSpecific: Brak sugestii kategorii dla '{sourceEntry.FilePath}' (model: {modelVM.ModelName}).");
                            }
                        } // end foreach imagePathInMix
                    }
                    else { SimpleFileLogger.LogWarning($"MatchModelSpecific: Folder źródłowy '{currentMixPath}' nie istnieje."); }
                } // end foreach mixFolderName
                token.ThrowIfCancellationRequested();
                SimpleFileLogger.Log($"MatchModelSpecific dla '{modelVM.ModelName}': Podsumowanie - Znaleziono w Mix: {filesFoundInMix}, z embeddingami: {filesWithEmbeddings}. Akcje automatyczne: {autoActionsCount}. Sugestie do okna: {movesForSuggestionWindow.Count}. Profile zmodyfikowane: {anyProfileDataChangedDuringEntireOperation}");

                // Store these model-specific suggestions
                _lastModelSpecificSuggestions = new List<Models.ProposedMove>(movesForSuggestionWindow);
                _lastScannedModelNameForSuggestions = modelVM.ModelName;

                if (movesForSuggestionWindow.Any())
                {
                    bool? dialogOutcome = false;
                    List<Models.ProposedMove> approvedMoves = new List<Models.ProposedMove>();
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var previewVM = new PreviewChangesViewModel(movesForSuggestionWindow, SuggestionSimilarityThreshold);
                        var previewWindow = new PreviewChangesWindow { DataContext = previewVM, Owner = Application.Current.MainWindow };
                        previewWindow.SetViewModelCloseAction(previewVM); // Ensure VM can close window

                        dialogOutcome = previewWindow.ShowDialog();
                        if (dialogOutcome == true)
                        {
                            approvedMoves = previewVM.GetApprovedMoves();
                        }
                    }); // End Dispatcher.InvokeAsync
                    token.ThrowIfCancellationRequested(); // Check cancellation after dialog
                    if (dialogOutcome == true && approvedMoves.Any())
                    {
                        // Pass the specific modelVM to update its cache correctly
                        if (await InternalHandleApprovedMovesAsync(approvedMoves, modelVM, null, token))
                            anyProfileDataChangedDuringEntireOperation = true;

                        // After handling, clear the suggestions for this model as they've been actioned or dismissed by the window
                        // This specific model's suggestions are now handled.
                        _lastModelSpecificSuggestions.RemoveAll(sugg => movesForSuggestionWindow.Contains(sugg));

                    }
                    else if (dialogOutcome == false) // User cancelled the preview window
                    {
                        StatusMessage = $"Anulowano zmiany sugestii dla '{modelVM.ModelName}'. Sugestie pozostają w pamięci podręcznej.";
                        // Do not clear _lastModelSpecificSuggestions here, user might want to re-open via context menu.
                    }
                }
                // else: No suggestions to show in window, auto actions might have handled everything or nothing found.

                if (anyProfileDataChangedDuringEntireOperation)
                {
                    SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Wykryto zmiany w profilach dla '{modelVM.ModelName}'. Odświeżanie widoku UI.");
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }
                RefreshPendingSuggestionCountsFromCache(); // Update UI counts based on remaining _lastModelSpecificSuggestions
                StatusMessage = $"Dla '{modelVM.ModelName}': {autoActionsCount} akcji auto., {modelVM.PendingSuggestionsCount} sugestii pozostało do przejrzenia.";


                if (!movesForSuggestionWindow.Any() && autoActionsCount > 0 && !anyProfileDataChangedDuringEntireOperation)
                {
                    MessageBox.Show($"Zakończono automatyczne operacje dla '{modelVM.ModelName}'. Wykonano {autoActionsCount} akcji. Brak dodatkowych sugestii do przejrzenia.", "Operacje Automatyczne Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (!movesForSuggestionWindow.Any() && autoActionsCount == 0 && !anyProfileDataChangedDuringEntireOperation && filesFoundInMix > 0)
                {
                    MessageBox.Show($"Brak nowych sugestii lub automatycznych akcji dla '{modelVM.ModelName}'. Sprawdź, czy foldery 'Mix' zawierają odpowiednie obrazy lub dostosuj próg podobieństwa.", "Brak Zmian", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (filesFoundInMix == 0)
                {
                    MessageBox.Show($"Nie znaleziono obrazów w folderach źródłowych (np. Mix) dla modelki '{modelVM.ModelName}'.", "Brak Plików Źródłowych", MessageBoxButton.OK, MessageBoxImage.Information);
                }

            }, "Dopasowywanie dla modelki");

        // Global Scan: SuggestImagesCommand
        private Task ExecuteSuggestImagesAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                // Clear any previous scan results before starting a new global scan
                ClearModelSpecificSuggestionsCache();
                token.ThrowIfCancellationRequested();
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe (np. 'Mix') w ustawieniach."; MessageBox.Show(StatusMessage, "Brak Folderów Źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                var allCollectedSuggestionsGlobal = new List<Models.ProposedMove>();
                // Reset all UI pending counts before scan
                await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var mVM_ui in HierarchicalProfilesList) { mVM_ui.PendingSuggestionsCount = 0; foreach (var cp_ui in mVM_ui.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; } });
                token.ThrowIfCancellationRequested();

                var allModelsCurrentlyInList = HierarchicalProfilesList.ToList(); // Snapshot
                int totalFilesFound = 0, totalFilesWithEmbeddings = 0, totalAutoActions = 0;
                bool anyProfileDataChangedDuringEntireOperation = false;
                var alreadySuggestedGraphicDuplicatesGlobal = new List<(float[] embedding, string targetCategoryName, string sourceFilePath)>();


                foreach (var modelVM in allModelsCurrentlyInList)
                {
                    token.ThrowIfCancellationRequested();
                    string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName);
                    if (!Directory.Exists(modelPath) || !modelVM.HasCharacterProfiles)
                    {
                        SimpleFileLogger.Log($"ExecuteSuggestImagesAsync: Pomijanie modelki '{modelVM.ModelName}' - folder nie istnieje lub brak profili postaci.");
                        continue;
                    }
                    SimpleFileLogger.Log($"ExecuteSuggestImagesAsync (Global Scan): Przetwarzanie modelki '{modelVM.ModelName}'.");

                    foreach (var mixFolderName in mixedFolders)
                    {
                        token.ThrowIfCancellationRequested();
                        string currentMixPath = Path.Combine(modelPath, mixFolderName);
                        if (Directory.Exists(currentMixPath))
                        {
                            var imagePathsInMix = await _fileScannerService.ScanDirectoryAsync(currentMixPath);
                            totalFilesFound += imagePathsInMix.Count;

                            foreach (var imgPathFromMix in imagePathsInMix)
                            {
                                token.ThrowIfCancellationRequested();
                                if (!File.Exists(imgPathFromMix)) continue;
                                var sourceEntry = await _imageMetadataService.ExtractMetadataAsync(imgPathFromMix);
                                if (sourceEntry == null) continue;

                                var sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceEntry);
                                if (sourceEmbedding == null) continue;
                                totalFilesWithEmbeddings++;
                                token.ThrowIfCancellationRequested();

                                var bestSuggestionForThisSourceImage = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName);

                                if (bestSuggestionForThisSourceImage != null)
                                {
                                    CategoryProfile targetProfile = bestSuggestionForThisSourceImage.Item1;
                                    double similarityToCentroid = bestSuggestionForThisSourceImage.Item2;

                                    var (proposedMove, wasActionAutoHandled, profilesModifiedByCall) = await ProcessDuplicateOrSuggestNewAsync(
                                        sourceEntry,
                                        targetProfile,
                                        similarityToCentroid,
                                        modelPath,
                                        sourceEmbedding,
                                        token);

                                    if (profilesModifiedByCall) anyProfileDataChangedDuringEntireOperation = true;

                                    if (wasActionAutoHandled)
                                    {
                                        totalAutoActions++;
                                    }
                                    else if (proposedMove != null)
                                    {
                                        bool addThisSuggestionToGlobalList = true;
                                        if (proposedMove.SourceImageEmbedding != null &&
                                            (proposedMove.Action == ProposedMoveActionType.CopyNew || proposedMove.Action == ProposedMoveActionType.ConflictKeepBoth))
                                        {
                                            foreach (var (existingEmb, existingTargetCat, existingSourcePath) in alreadySuggestedGraphicDuplicatesGlobal)
                                            {
                                                if (existingTargetCat.Equals(proposedMove.TargetCategoryProfileName, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    double simToExistingSuggested = Utils.MathUtils.CalculateCosineSimilarity(proposedMove.SourceImageEmbedding, existingEmb);
                                                    if (simToExistingSuggested >= DUPLICATE_SIMILARITY_THRESHOLD)
                                                    {
                                                        addThisSuggestionToGlobalList = false;
                                                        SimpleFileLogger.Log($"[SuggestWindowFilter-Global] Pomijanie sugestii dla '{proposedMove.SourceImage.FilePath}', graficznie identyczna sugestia (z '{existingSourcePath}') już istnieje dla '{existingTargetCat}'.");
                                                        string pathToDeleteForFilter = proposedMove.SourceImage.FilePath;
                                                        try
                                                        {
                                                            if (File.Exists(pathToDeleteForFilter))
                                                            {
                                                                File.Delete(pathToDeleteForFilter);
                                                                SimpleFileLogger.Log($"[SuggestWindowFilterCleanup-Global] Usunięto plik źródłowy duplikatu sugestii: {pathToDeleteForFilter}");
                                                                if (await HandleFileMovedOrDeletedUpdateProfilesAsync(pathToDeleteForFilter, null, null, token))
                                                                    anyProfileDataChangedDuringEntireOperation = true;
                                                            }
                                                        }
                                                        catch (Exception ex) { SimpleFileLogger.LogError($"[SuggestWindowFilterCleanup-Global] Błąd usuwania {pathToDeleteForFilter}: {ex.Message}", ex); }
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                        if (addThisSuggestionToGlobalList)
                                        {
                                            allCollectedSuggestionsGlobal.Add(proposedMove);
                                            if (proposedMove.SourceImageEmbedding != null && (proposedMove.Action == ProposedMoveActionType.CopyNew || proposedMove.Action == ProposedMoveActionType.ConflictKeepBoth))
                                            {
                                                alreadySuggestedGraphicDuplicatesGlobal.Add((proposedMove.SourceImageEmbedding, proposedMove.TargetCategoryProfileName, proposedMove.SourceImage.FilePath));
                                            }
                                        }
                                    }
                                }
                            } // end foreach imagePathInMix
                        } // end if Directory.Exists(currentMixPath)
                    } // end foreach mixFolderName
                } // end foreach modelVM
                token.ThrowIfCancellationRequested();
                SimpleFileLogger.Log($"ExecuteSuggestImagesAsync: Podsumowanie globalne - Znaleziono: {totalFilesFound}, Z embeddingami: {totalFilesWithEmbeddings}, Akcje auto: {totalAutoActions}, Sugestie zebrane: {allCollectedSuggestionsGlobal.Count}. Profile zmodyfikowane: {anyProfileDataChangedDuringEntireOperation}");

                // Store all collected suggestions globally
                _lastModelSpecificSuggestions = new List<Models.ProposedMove>(allCollectedSuggestionsGlobal);
                _lastScannedModelNameForSuggestions = null; // Mark as global results

                StatusMessage = $"Globalne wyszukiwanie zakończone. {totalAutoActions} akcji auto. {allCollectedSuggestionsGlobal.Count} potencjalnych sugestii znaleziono.";

                if (anyProfileDataChangedDuringEntireOperation)
                {
                    SimpleFileLogger.Log($"ExecuteSuggestImagesAsync: Wykryto zmiany w profilach podczas globalnego skanowania. Odświeżanie widoku UI.");
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }

                RefreshPendingSuggestionCountsFromCache(); // This will update UI based on _lastModelSpecificSuggestions (now global)

                // MODIFICATION: Do NOT show PreviewChangesWindow automatically here.
                // Inform user about completion and how to view suggestions.
                string completionMessage = StatusMessage;
                if (allCollectedSuggestionsGlobal.Any())
                {
                    completionMessage += " Użyj menu kontekstowego na modelkach/postaciach, aby przejrzeć znalezione sugestie.";
                }
                else if (totalAutoActions == 0)
                {
                    completionMessage = "Globalne wyszukiwanie nie znalazło nowych sugestii ani nie wykonało akcji automatycznych.";
                }
                MessageBox.Show(completionMessage, "Globalne Wyszukiwanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);


            }, "Globalne wyszukiwanie sugestii");

        // For Character context menu: CheckCharacterSuggestionsCommand
        private Task ExecuteCheckCharacterSuggestionsAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                var charProfileForSuggestions = (parameter as CategoryProfile) ?? SelectedProfile;
                if (charProfileForSuggestions == null) { StatusMessage = "Błąd: Wybierz profil postaci z listy."; MessageBox.Show(StatusMessage, "Brak Wyboru Postaci", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                SimpleFileLogger.Log($"CheckCharacterSuggestions dla '{charProfileForSuggestions.CategoryName}'. Token: {token.GetHashCode()}");
                token.ThrowIfCancellationRequested();
                string modelName = _profileService.GetModelNameFromCategory(charProfileForSuggestions.CategoryName);
                var modelVM = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));

                var movesForThisCharacterWindow = new List<Models.ProposedMove>();
                bool anyProfileDataChangedDuringEntireOperation = false;
                var alreadySuggestedGraphicDuplicatesForChar = new List<(float[] embedding, string targetCategoryName, string sourceFilePath)>();


                // If we have cached global suggestions, or suggestions for this specific model, filter them first.
                if (_lastModelSpecificSuggestions.Any())
                {
                    if (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) || // Global scan results
                        _lastScannedModelNameForSuggestions.Equals(modelName, StringComparison.OrdinalIgnoreCase)) // Model-specific scan results
                    {
                        movesForThisCharacterWindow = _lastModelSpecificSuggestions
                            .Where(m => m.TargetCategoryProfileName.Equals(charProfileForSuggestions.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                                        m.Similarity >= SuggestionSimilarityThreshold)
                            .ToList();
                        SimpleFileLogger.Log($"CheckCharacterSuggestions: Użyto cache'owanych sugestii ({_lastModelSpecificSuggestions.Count} total in cache) dla postaci '{charProfileForSuggestions.CategoryName}'. Przefiltrowano do {movesForThisCharacterWindow.Count}.");
                    }
                }

                // If no relevant suggestions were found in cache, OR if the user explicitly wants to re-scan Mix for this character (current behavior implies using cache if available)
                // For now, this command primarily relies on the cache populated by global scan or model-specific scan.
                // If the cache is empty or doesn't apply, it means the user needs to run one of those scans first.
                // The original version of this command DID re-scan Mix folders. We can choose to keep that, or rely on cache.
                // Let's assume for now it only shows *cached* suggestions for this character. If empty, inform user.

                if (!movesForThisCharacterWindow.Any())
                {
                    StatusMessage = $"Brak aktualnie zakolejkowanych sugestii dla '{charProfileForSuggestions.CategoryName}'. Uruchom globalne skanowanie lub skanowanie dla modelki '{modelName}'.";
                    MessageBox.Show(StatusMessage, "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Information);
                    // Ensure pending count is 0 if no suggestions shown
                    var uiProfile = modelVM?.CharacterProfiles.FirstOrDefault(cp => cp.CategoryName == charProfileForSuggestions.CategoryName);
                    if (uiProfile != null) uiProfile.PendingSuggestionsCount = 0;
                    if (modelVM != null) modelVM.PendingSuggestionsCount = modelVM.CharacterProfiles.Sum(cp => cp.PendingSuggestionsCount);
                    return;
                }

                token.ThrowIfCancellationRequested();

                if (movesForThisCharacterWindow.Any())
                {
                    bool? outcome = false; List<Models.ProposedMove> approved = new List<Models.ProposedMove>();
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var vm = new PreviewChangesViewModel(movesForThisCharacterWindow, SuggestionSimilarityThreshold);
                        var win = new PreviewChangesWindow { DataContext = vm, Owner = Application.Current.MainWindow };
                        win.SetViewModelCloseAction(vm);
                        outcome = win.ShowDialog();
                        if (outcome == true) approved = vm.GetApprovedMoves();
                    });
                    token.ThrowIfCancellationRequested();
                    if (outcome == true && approved.Any())
                    {
                        // Pass the parent ModelDisplayViewModel and the specific CharacterProfile for context
                        if (await InternalHandleApprovedMovesAsync(approved, modelVM, charProfileForSuggestions, token))
                            anyProfileDataChangedDuringEntireOperation = true;

                        // Remove the approved/actioned moves from the main cache
                        _lastModelSpecificSuggestions.RemoveAll(sugg => approved.Any(ap => ap.SourceImage.FilePath == sugg.SourceImage.FilePath));
                    }
                    else if (outcome == false)
                    {
                        StatusMessage = $"Anulowano przeglądanie sugestii dla '{charProfileForSuggestions.CategoryName}'. Pozostają w kolejce.";
                    }
                }

                if (anyProfileDataChangedDuringEntireOperation)
                {
                    SimpleFileLogger.Log($"CheckCharacterSuggestionsAsync: Wykryto zmiany w profilach dla '{charProfileForSuggestions.CategoryName}'. Odświeżanie widoku UI.");
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }
                RefreshPendingSuggestionCountsFromCache(); // Update counts after actions

            }, "Sprawdzanie sugestii dla postaci");

        // Refreshes UI counts based on _lastModelSpecificSuggestions
        private void RefreshPendingSuggestionCountsFromCache()
        {
            Application.Current.Dispatcher.Invoke(() => {
                // Reset all UI counts first
                foreach (var mVM_iter in HierarchicalProfilesList)
                {
                    mVM_iter.PendingSuggestionsCount = 0; // Reset model total
                    foreach (var cp_iter in mVM_iter.CharacterProfiles)
                    {
                        cp_iter.PendingSuggestionsCount = 0; // Reset character count
                    }
                }

                if (_lastModelSpecificSuggestions.Any())
                {
                    // If _lastScannedModelNameForSuggestions is null or a special marker, it means _lastModelSpecificSuggestions contains global results
                    // If it has a model name, it's model-specific results.

                    var relevantSuggestions = _lastModelSpecificSuggestions
                        .Where(sugg => sugg.Similarity >= SuggestionSimilarityThreshold)
                        .ToList();

                    if (!string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) && _lastScannedModelNameForSuggestions != "__CACHE_CLEARED__")
                    {
                        // Specific model scan results are in cache
                        var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(_lastScannedModelNameForSuggestions, StringComparison.OrdinalIgnoreCase));
                        if (modelToUpdate != null)
                        {
                            int totalForModel = 0;
                            foreach (var cp_ui in modelToUpdate.CharacterProfiles)
                            {
                                cp_ui.PendingSuggestionsCount = relevantSuggestions.Count(
                                    sugg => sugg.TargetCategoryProfileName.Equals(cp_ui.CategoryName, StringComparison.OrdinalIgnoreCase));
                                totalForModel += cp_ui.PendingSuggestionsCount;
                            }
                            modelToUpdate.PendingSuggestionsCount = totalForModel;
                            SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache (Specific Model '{_lastScannedModelNameForSuggestions}'): Updated counts. Total for model: {totalForModel}.");
                        }
                    }
                    else // Global scan results are in cache (or cache was just cleared - check marker)
                    {
                        if (_lastScannedModelNameForSuggestions != "__CACHE_CLEARED__")
                        {
                            SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache (Global Scan Results): Attributing {relevantSuggestions.Count} suggestions to all relevant models.");
                            var suggestionsByModel = relevantSuggestions
                                .GroupBy(sugg => _profileService.GetModelNameFromCategory(sugg.TargetCategoryProfileName));

                            foreach (var group in suggestionsByModel)
                            {
                                var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
                                if (modelToUpdate != null)
                                {
                                    int totalForModel = 0;
                                    foreach (var cp_ui in modelToUpdate.CharacterProfiles)
                                    {
                                        cp_ui.PendingSuggestionsCount = group.Count(
                                            sugg => sugg.TargetCategoryProfileName.Equals(cp_ui.CategoryName, StringComparison.OrdinalIgnoreCase));
                                        totalForModel += cp_ui.PendingSuggestionsCount;
                                    }
                                    modelToUpdate.PendingSuggestionsCount = totalForModel;
                                    SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache (Global Scan): Model '{modelToUpdate.ModelName}' updated with {totalForModel} pending suggestions.");
                                }
                            }
                        }
                        else
                        {
                            SimpleFileLogger.Log("RefreshPendingSuggestionCountsFromCache: Cache was cleared. All counts remain 0.");
                        }
                    }
                }
                else
                {
                    SimpleFileLogger.Log("RefreshPendingSuggestionCountsFromCache: No suggestions in cache. All UI counts remain/set to 0.");
                }
                // Force re-evaluation of CanExecute for context menu commands
                CommandManager.InvalidateRequerySuggested();
            });
        }


        private async Task<bool> InternalHandleApprovedMovesAsync(List<Models.ProposedMove> approvedMoves, ModelDisplayViewModel? specificModelVM, CategoryProfile? specificCharacterProfile, CancellationToken token)
        {
            int successfulMoves = 0, copyErrors = 0, deleteErrors = 0, skippedQuality = 0, skippedOther = 0;
            bool anyProfileActuallyModified = false;
            var processedSourcePathsForThisBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Track sources processed in this batch

            foreach (var move in approvedMoves)
            {
                token.ThrowIfCancellationRequested();
                string sourcePath = move.SourceImage.FilePath;
                string targetPath = move.ProposedTargetPath; // This is the initially proposed path
                string originalProposedTargetPathForLogging = move.ProposedTargetPath; // For logging if targetPath changes
                var actionType = move.Action;
                bool operationSuccessfulThisMove = false;
                bool deleteSourceAfterCopy = false; // Flag to delete source file from Mix/etc.

                try
                {
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    {
                        SimpleFileLogger.LogWarning($"[HandleApproved] Plik źródłowy nie istnieje lub ścieżka jest pusta: '{sourcePath}'. Pomijanie.");
                        skippedOther++;
                        continue;
                    }
                    string targetDirectory = Path.GetDirectoryName(targetPath);
                    if (string.IsNullOrEmpty(targetDirectory)) { SimpleFileLogger.LogWarning($"[HandleApproved] Nie można określić folderu docelowego dla: '{targetPath}'. Pomijanie."); skippedOther++; continue; }
                    Directory.CreateDirectory(targetDirectory); // Ensure target directory exists

                    switch (actionType)
                    {
                        case ProposedMoveActionType.CopyNew:
                            // If target exists and is not the same file, generate unique name
                            if (File.Exists(targetPath) && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                targetPath = GenerateUniqueTargetPath(targetDirectory, Path.GetFileName(sourcePath), "_new_approved");
                                SimpleFileLogger.Log($"[HandleApproved] CopyNew: Plik docelowy '{originalProposedTargetPathForLogging}' już istniał. Zmieniono na '{targetPath}'.");
                            }
                            else if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                SimpleFileLogger.LogWarning($"[HandleApproved] CopyNew: Plik źródłowy i docelowy to ten sam plik ('{sourcePath}'). Nie ma potrzeby kopiowania. Rozważ usunięcie ze źródła jeśli to duplikat wpisu.");
                                // If it's the same file, we might still want to "delete" it from the source list if it was a redundant suggestion
                                deleteSourceAfterCopy = true; // Mark to remove from source lists/profiles
                                operationSuccessfulThisMove = true; // Considered successful as no file op error
                                break; // Skip file copy
                            }
                            await Task.Run(() => File.Copy(sourcePath, targetPath, false), token); // false = do not overwrite
                            operationSuccessfulThisMove = true;
                            deleteSourceAfterCopy = true;
                            SimpleFileLogger.Log($"[HandleApproved] CopyNew: Skopiowano '{sourcePath}' do '{targetPath}'.");
                            break;

                        case ProposedMoveActionType.OverwriteExisting:
                            // Check if source and target are the same file. If so, no actual overwrite needed.
                            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                SimpleFileLogger.LogWarning($"[HandleApproved] OverwriteExisting: Plik źródłowy i docelowy to ten sam plik ('{sourcePath}'). Nie ma potrzeby nadpisywania.");
                                deleteSourceAfterCopy = true; // Still "delete" from source list
                                operationSuccessfulThisMove = true;
                                break;
                            }
                            await Task.Run(() => File.Copy(sourcePath, targetPath, true), token); // true = overwrite
                            operationSuccessfulThisMove = true;
                            deleteSourceAfterCopy = true;
                            SimpleFileLogger.Log($"[HandleApproved] OverwriteExisting: Nadpisano '{targetPath}' plikiem '{sourcePath}'.");
                            break;

                        case ProposedMoveActionType.KeepExistingDeleteSource:
                            // Ensure source and target are different before attempting delete,
                            // otherwise, we'd delete the file we want to keep.
                            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                deleteSourceAfterCopy = true; // Source will be deleted
                                operationSuccessfulThisMove = true; // No copy error
                                SimpleFileLogger.Log($"[HandleApproved] KeepExistingDeleteSource: Zachowano istniejący '{targetPath}'. Źródło '{sourcePath}' zostanie usunięte.");
                            }
                            else // Source and target are the same file.
                            {
                                SimpleFileLogger.LogWarning($"[HandleApproved] KeepExistingDeleteSource: Plik źródłowy i docelowy to ten sam plik ('{sourcePath}'). Nic do zrobienia (plik już jest na miejscu, źródło nie jest usuwane, bo to ten sam plik).");
                                // deleteSourceAfterCopy remains false, operationSuccessfulThisMove true because no error.
                                operationSuccessfulThisMove = true;
                                skippedQuality++; // Or skippedOther, depending on interpretation
                            }
                            break;

                        case ProposedMoveActionType.ConflictKeepBoth:
                            // TargetPath here is the original proposed path which has a conflict.
                            // Generate a new unique name for the source file being copied.
                            string newTargetPathForConflict = GenerateUniqueTargetPath(targetDirectory, Path.GetFileName(sourcePath), "_conflict_approved");
                            await Task.Run(() => File.Copy(sourcePath, newTargetPathForConflict, false), token);
                            targetPath = newTargetPathForConflict; // Update targetPath to the actual new path for profile update
                            operationSuccessfulThisMove = true;
                            deleteSourceAfterCopy = true;
                            SimpleFileLogger.Log($"[HandleApproved] ConflictKeepBoth: Skopiowano '{sourcePath}' do '{targetPath}' (rozwiązanie konfliktu).");
                            break;
                    }
                    token.ThrowIfCancellationRequested();

                    if (operationSuccessfulThisMove)
                    {
                        successfulMoves++;
                        processedSourcePathsForThisBatch.Add(sourcePath); // Add original source path

                        string? oldPathForProfileUpdate = null; // Path of the file removed from its original location
                        string? newPathForProfileUpdate = targetPath; // Path of the file in its new/final location for the target profile

                        if (deleteSourceAfterCopy && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                        {
                            oldPathForProfileUpdate = sourcePath; // This file is being removed from its original place (Mix)
                            try
                            {
                                if (File.Exists(sourcePath)) // Double check
                                {
                                    await Task.Run(() => File.Delete(sourcePath), token);
                                    SimpleFileLogger.Log($"[HandleApproved] Usunięto plik źródłowy: '{sourcePath}'.");
                                }
                            }
                            catch (Exception exDelete) { deleteErrors++; SimpleFileLogger.LogError($"[HandleApproved] Błąd podczas usuwania pliku źródłowego '{sourcePath}'.", exDelete); }
                        }
                        else if (deleteSourceAfterCopy && string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                        {
                            // Source was same as target, no actual file move/copy. oldPath is still sourcePath because it's "removed" from suggestions.
                            // newPathForProfileUpdate should be the targetPath (which is same as sourcePath).
                            // The target profile should already contain this path.
                            // We still need to notify HandleFileMovedOrDeletedUpdateProfilesAsync that sourcePath is "handled".
                            oldPathForProfileUpdate = sourcePath; // It's handled from the source perspective
                            newPathForProfileUpdate = targetPath; // It's confirmed in the target profile
                            SimpleFileLogger.Log($"[HandleApproved] Plik źródłowy '{sourcePath}' był tożsamy z docelowym. Oznaczony jako obsłużony.");
                        }


                        // Update profiles: remove oldPath (if applicable), add newPath to targetCategory.
                        if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldPathForProfileUpdate, newPathForProfileUpdate, move.TargetCategoryProfileName, token))
                            anyProfileActuallyModified = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exCopy) { copyErrors++; SimpleFileLogger.LogError($"[HandleApproved] Błąd podczas przetwarzania ruchu dla '{sourcePath}' -> '{originalProposedTargetPathForLogging}'. Akcja: {actionType}.", exCopy); }
            } // End foreach move
            token.ThrowIfCancellationRequested();

            // Update the main cache (_lastModelSpecificSuggestions) by removing items that were just processed.
            if (processedSourcePathsForThisBatch.Any())
            {
                int removedFromCacheCount = _lastModelSpecificSuggestions.RemoveAll(s => processedSourcePathsForThisBatch.Contains(s.SourceImage.FilePath));
                SimpleFileLogger.Log($"[HandleApproved] Usunięto {removedFromCacheCount} przetworzonych sugestii z głównego cache'u.");
            }


            StatusMessage = $"Zakończono zatwierdzone operacje: {successfulMoves} pomyślnie, {skippedQuality} pom. (jakość), {skippedOther} pom. (inne), {copyErrors} bł. kopiowania, {deleteErrors} bł. usuwania.";
            if (successfulMoves > 0 || copyErrors > 0 || deleteErrors > 0) // Only show pop-up if actual file operations happened or tried to.
            {
                MessageBox.Show(StatusMessage, "Operacja Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return anyProfileActuallyModified;
        }


        private string GenerateUniqueTargetPath(string targetDirectory, string originalFileNameWithExtension, string suffixIfConflict = "_conflict")
        {
            string baseName = Path.GetFileNameWithoutExtension(originalFileNameWithExtension);
            string extension = Path.GetExtension(originalFileNameWithExtension);
            string finalPath = Path.Combine(targetDirectory, originalFileNameWithExtension);
            int counter = 1;

            while (File.Exists(finalPath))
            {
                string newFileName = $"{baseName}{suffixIfConflict}{counter}{extension}";
                finalPath = Path.Combine(targetDirectory, newFileName);
                counter++;
                if (counter > 9999) // Safety break for extreme cases
                {
                    newFileName = $"{baseName}_{Guid.NewGuid():N}{extension}";
                    finalPath = Path.Combine(targetDirectory, newFileName);
                    SimpleFileLogger.LogWarning($"GenerateUniqueTargetPath: Wygenerowano nazwę z GUID po wielu konfliktach: {finalPath}");
                    break;
                }
            }
            return finalPath;
        }

        private Task ExecuteRemoveModelTreeAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                bool profilesActuallyChanged = false;
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Wybierz modelkę z listy."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();
                if (MessageBox.Show($"Czy na pewno chcesz usunąć całą modelkę '{modelVM.ModelName}' wraz ze wszystkimi jej profilami postaci?\nTa operacja usunie definicje profili oraz plik JSON tej modelki, ale NIE usunie plików graficznych z dysku.", "Potwierdź usunięcie modelki", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    if (await _profileService.RemoveAllProfilesForModelAsync(modelVM.ModelName))
                    {
                        StatusMessage = $"Modelka '{modelVM.ModelName}' i jej profile zostały usunięte.";
                        // If the deleted model was the one for which suggestions were cached, clear that cache.
                        if (_lastScannedModelNameForSuggestions == modelVM.ModelName) ClearModelSpecificSuggestionsCache();

                        // If the currently selected profile belonged to the deleted model, deselect it.
                        if (SelectedProfile != null && _profileService.GetModelNameFromCategory(SelectedProfile.CategoryName) == modelVM.ModelName)
                        {
                            SelectedProfile = null;
                        }
                        profilesActuallyChanged = true;
                    }
                    else
                    {
                        StatusMessage = $"Nie udało się usunąć modelki '{modelVM.ModelName}'. Sprawdź logi.";
                        // Even if removal failed, underlying data might have changed, or we want to reflect potential partial changes.
                        profilesActuallyChanged = true;
                    }
                }
                if (profilesActuallyChanged)
                {
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }
            }, "Usuwanie całej modelki");

        private Task ExecuteAnalyzeModelForSplittingAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Wybierz modelkę z listy."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();
                int profilesMarkedForSplit = 0;

                // Reset split suggestion flags for the UI
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.HasSplitSuggestion = false;
                });

                var characterProfilesForModel = modelVM.CharacterProfiles.ToList(); // Work on a snapshot
                foreach (var characterProfile in characterProfilesForModel)
                {
                    token.ThrowIfCancellationRequested();
                    const int minImagesForConsideration = 10; // Min images to even look at it
                    const int minImagesToSuggestSplit = 20; // Min images to actually flag for split

                    if (characterProfile.SourceImagePaths == null || characterProfile.SourceImagePaths.Count < minImagesForConsideration)
                    {
                        SimpleFileLogger.Log($"AnalyzeModelForSplitting: Profil '{characterProfile.CategoryName}' ma mniej niż {minImagesForConsideration} obrazów ({characterProfile.SourceImagePaths?.Count ?? 0}), pomijanie.");
                        continue;
                    }

                    // We don't need embeddings for this analysis, just counts.
                    // However, it might be good to verify file existence.
                    var validImagePathsCount = 0;
                    if (characterProfile.SourceImagePaths != null)
                    {
                        foreach (var p in characterProfile.SourceImagePaths)
                        {
                            if (File.Exists(p)) validImagePathsCount++;
                        }
                    }
                    token.ThrowIfCancellationRequested();

                    if (validImagePathsCount < minImagesForConsideration)
                    {
                        SimpleFileLogger.Log($"AnalyzeModelForSplitting: Profil '{characterProfile.CategoryName}' po weryfikacji plików ma mniej niż {minImagesForConsideration} istniejących obrazów ({validImagePathsCount}), pomijanie.");
                        continue;
                    }

                    bool shouldSuggestSplit = validImagePathsCount >= minImagesToSuggestSplit;

                    // Update the UI-bound CategoryProfile object
                    var uiCharacterProfile = modelVM.CharacterProfiles.FirstOrDefault(p => p.CategoryName == characterProfile.CategoryName);
                    if (uiCharacterProfile != null)
                    {
                        uiCharacterProfile.HasSplitSuggestion = shouldSuggestSplit;
                        if (shouldSuggestSplit) profilesMarkedForSplit++;
                    }
                    SimpleFileLogger.Log($"AnalyzeModelForSplitting: Profil '{characterProfile.CategoryName}', obrazy: {validImagePathsCount}, sugestia podziału: {shouldSuggestSplit}.");
                }
                token.ThrowIfCancellationRequested();
                StatusMessage = $"Analiza podziału dla modelki '{modelVM.ModelName}': {profilesMarkedForSplit} profili oznaczonych jako kandydaci do podziału.";
                if (profilesMarkedForSplit > 0) MessageBox.Show(StatusMessage, "Analiza Podziału Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
                else MessageBox.Show($"Brak profili dla modelki '{modelVM.ModelName}', które kwalifikowałyby się do podziału wg obecnych kryteriów.", "Analiza Podziału Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "Analiza profili pod kątem podziału");

        private Task ExecuteOpenSplitProfileDialogAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is CategoryProfile originalCharacterProfile)) { StatusMessage = "Błąd: Wybierz profil postaci z flagą sugestii podziału."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();
                bool dataChanged = false;

                var imagesInProfile = new List<ImageFileEntry>();
                if (originalCharacterProfile.SourceImagePaths != null)
                {
                    foreach (var path in originalCharacterProfile.SourceImagePaths)
                    {
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(path)) // Only process existing files
                        {
                            var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                            if (entry != null) imagesInProfile.Add(entry);
                        }
                    }
                }
                token.ThrowIfCancellationRequested();

                if (!imagesInProfile.Any())
                {
                    StatusMessage = $"Profil '{originalCharacterProfile.CategoryName}' jest pusty lub nie zawiera prawidłowych plików.";
                    MessageBox.Show(StatusMessage, "Profil Pusty", MessageBoxButton.OK, MessageBoxImage.Warning);
                    var uiProfile = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == originalCharacterProfile.CategoryName);
                    if (uiProfile != null) uiProfile.HasSplitSuggestion = false; // Clear flag if profile is now empty
                    return;
                }

                // Simple split: first half, second half. User can adjust in dialog.
                var group1Images = imagesInProfile.Take(imagesInProfile.Count / 2).ToList();
                var group2Images = imagesInProfile.Skip(imagesInProfile.Count / 2).ToList();

                string modelName = _profileService.GetModelNameFromCategory(originalCharacterProfile.CategoryName);
                string baseCharacterName = _profileService.GetCharacterNameFromCategory(originalCharacterProfile.CategoryName);
                // Handle case where original is "ModelName - General" or just "ModelName" (which implies General)
                if (baseCharacterName.Equals("General", StringComparison.OrdinalIgnoreCase) &&
                    originalCharacterProfile.CategoryName.Equals($"{modelName} - General", StringComparison.OrdinalIgnoreCase) ||
                    originalCharacterProfile.CategoryName.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                {
                    baseCharacterName = modelName; // Use model name as base for new part names
                }


                string suggestedName1 = $"{baseCharacterName} - Part 1";
                string suggestedName2 = $"{baseCharacterName} - Part 2";
                bool? dialogResult = false;
                SplitProfileViewModel? splitVM = null; // To hold the ViewModel instance

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    splitVM = new SplitProfileViewModel(originalCharacterProfile, group1Images, group2Images, suggestedName1, suggestedName2);
                    var splitWindow = new SplitProfileWindow { DataContext = splitVM, Owner = Application.Current.MainWindow };
                    splitWindow.SetViewModelCloseAction(splitVM);
                    dialogResult = splitWindow.ShowDialog();
                });
                token.ThrowIfCancellationRequested();

                if (dialogResult == true && splitVM != null) // User confirmed split
                {
                    StatusMessage = $"Podział profilu '{originalCharacterProfile.CategoryName}' został zatwierdzony. Rozpoczynanie operacji...";
                    SimpleFileLogger.Log($"SplitProfile: Zatwierdzono podział dla '{originalCharacterProfile.CategoryName}'. Nowe profile: '{splitVM.NewProfile1Name}' i '{splitVM.NewProfile2Name}'.");

                    // Construct full new profile names (Model - Character Part)
                    string fullNewProfile1Name = $"{modelName} - {splitVM.NewProfile1Name}";
                    string fullNewProfile2Name = $"{modelName} - {splitVM.NewProfile2Name}";

                    var entriesForProfile1 = splitVM.Group1Images.Select(vmItem => vmItem.OriginalImageEntry).ToList();
                    var entriesForProfile2 = splitVM.Group2Images.Select(vmItem => vmItem.OriginalImageEntry).ToList();

                    // Create new physical folders for the split parts
                    string newProfile1Path = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(splitVM.NewProfile1Name));
                    string newProfile2Path = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(splitVM.NewProfile2Name));
                    Directory.CreateDirectory(newProfile1Path);
                    Directory.CreateDirectory(newProfile2Path);
                    SimpleFileLogger.Log($"SplitProfile: Utworzono foldery: '{newProfile1Path}' i '{newProfile2Path}'.");

                    // Move files and update their paths in ImageFileEntry objects
                    foreach (var entry in entriesForProfile1)
                    {
                        token.ThrowIfCancellationRequested();
                        string newFilePath = Path.Combine(newProfile1Path, entry.FileName);
                        try
                        {
                            if (!File.Exists(newFilePath) && File.Exists(entry.FilePath)) File.Move(entry.FilePath, newFilePath);
                            else if (!File.Exists(entry.FilePath) && File.Exists(newFilePath)) { /* Already moved? Or error */ }
                            else if (File.Exists(entry.FilePath) && File.Exists(newFilePath) && !entry.FilePath.Equals(newFilePath, StringComparison.OrdinalIgnoreCase)) {/* Conflict */}

                            if (File.Exists(newFilePath)) entry.FilePath = newFilePath; // Update entry path
                        }
                        catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd przenoszenia pliku '{entry.FilePath}' do '{newFilePath}'", ex); }
                    }
                    foreach (var entry in entriesForProfile2)
                    {
                        token.ThrowIfCancellationRequested();
                        string newFilePath = Path.Combine(newProfile2Path, entry.FileName);
                        try
                        {
                            if (!File.Exists(newFilePath) && File.Exists(entry.FilePath)) File.Move(entry.FilePath, newFilePath);
                            else if (!File.Exists(entry.FilePath) && File.Exists(newFilePath)) { /* */ }
                            else if (File.Exists(entry.FilePath) && File.Exists(newFilePath) && !entry.FilePath.Equals(newFilePath, StringComparison.OrdinalIgnoreCase)) {/* */}
                            if (File.Exists(newFilePath)) entry.FilePath = newFilePath; // Update entry path
                        }
                        catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd przenoszenia pliku '{entry.FilePath}' do '{newFilePath}'", ex); }
                    }
                    SimpleFileLogger.Log($"SplitProfile: Przeniesiono pliki do nowych folderów.");

                    // Generate new profiles based on the moved files
                    await _profileService.GenerateProfileAsync(fullNewProfile1Name, entriesForProfile1);
                    SimpleFileLogger.Log($"SplitProfile: Wygenerowano profil '{fullNewProfile1Name}'.");
                    token.ThrowIfCancellationRequested();
                    await _profileService.GenerateProfileAsync(fullNewProfile2Name, entriesForProfile2);
                    SimpleFileLogger.Log($"SplitProfile: Wygenerowano profil '{fullNewProfile2Name}'.");
                    token.ThrowIfCancellationRequested();

                    // Remove the original profile
                    await _profileService.RemoveProfileAsync(originalCharacterProfile.CategoryName);
                    SimpleFileLogger.Log($"SplitProfile: Usunięto stary profil '{originalCharacterProfile.CategoryName}'.");
                    dataChanged = true;

                    StatusMessage = $"Profil '{originalCharacterProfile.CategoryName}' został podzielony na '{fullNewProfile1Name}' i '{fullNewProfile2Name}'.";
                    var uiProfile = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == originalCharacterProfile.CategoryName);
                    if (uiProfile != null) uiProfile.HasSplitSuggestion = false; // Clear flag
                }
                else // User cancelled the split dialog
                {
                    StatusMessage = $"Podział profilu '{originalCharacterProfile.CategoryName}' został anulowany przez użytkownika.";
                }

                if (dataChanged)
                {
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token); // Refresh UI
                    _isRefreshingProfilesPostMove = false;
                }

            }, "Otwieranie i przetwarzanie okna podziału profilu");

        private void ExecuteCancelCurrentOperation(object? parameter)
        {
            SimpleFileLogger.Log($"ExecuteCancelCurrentOperation. CTS: {_activeLongOperationCts != null}. Token: {_activeLongOperationCts?.Token.GetHashCode()}");
            if (_activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested)
            {
                _activeLongOperationCts.Cancel(); StatusMessage = "Anulowanie operacji..."; SimpleFileLogger.Log("Sygnał anulowania wysłany.");
            }
            else SimpleFileLogger.Log("Brak operacji do anulowania lub już anulowano.");
        }

        private Task ExecuteEnsureThumbnailsLoadedAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                var imagesToLoadThumbs = (parameter as IEnumerable<ImageFileEntry>)?.ToList() ?? ImageFiles.ToList();
                if (!imagesToLoadThumbs.Any()) { StatusMessage = "Brak obrazów do załadowania miniaturek."; return; }

                SimpleFileLogger.Log($"EnsureThumbnailsLoaded: Ładowanie dla {imagesToLoadThumbs.Count} obrazów. Token: {token.GetHashCode()}");
                int requestedCount = 0;
                var tasks = new List<Task>();

                foreach (var entry in imagesToLoadThumbs)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Thumbnail == null && !entry.IsLoadingThumbnail)
                    {
                        tasks.Add(entry.LoadThumbnailAsync()); // LoadThumbnailAsync is awaitable
                        requestedCount++;
                    }
                }
                StatusMessage = $"Rozpoczęto ładowanie {requestedCount} miniaturek...";
                await Task.WhenAll(tasks); // Wait for all thumbnail loading tasks to complete
                token.ThrowIfCancellationRequested(); // Check cancellation after waiting
                int loadedCount = imagesToLoadThumbs.Count(img => img.Thumbnail != null);
                StatusMessage = $"Załadowano {loadedCount} z {imagesToLoadThumbs.Count} miniaturek (poproszono o {requestedCount}).";
                SimpleFileLogger.Log(StatusMessage);
            }, "Ładowanie miniaturek");

        private Task ExecuteRemoveDuplicatesInModelAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                SimpleFileLogger.Log($"[ExecuteRemoveDuplicatesInModelAsync] WYWOŁANO. Parameter: {parameter?.GetType().FullName ?? "null"}");
                if (!(parameter is ModelDisplayViewModel modelVM))
                {
                    StatusMessage = "Błąd: Nieprawidłowy parametr dla usuwania duplikatów (oczekiwano ModelDisplayViewModel).";
                    SimpleFileLogger.LogWarning(StatusMessage);
                    return;
                }

                string modelName = modelVM.ModelName;
                if (MessageBox.Show($"Czy na pewno chcesz wyszukać i usunąć graficznie identyczne duplikaty (pozostawiając tylko najlepszą jakość) we wszystkich profilach postaci dla modelki '{modelName}'?\nTa operacja usunie gorsze kopie plików z dysku.", "Potwierdź Usuwanie Duplikatów", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    StatusMessage = "Usuwanie duplikatów anulowane przez użytkownika.";
                    SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Anulowano dla modelki: {modelName}.");
                    return;
                }

                StatusMessage = $"Rozpoczynanie usuwania duplikatów dla modelki: {modelName}...";
                SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Rozpoczęto dla modelki: {modelName}. Token: {token.GetHashCode()}");

                int totalDuplicatesRemovedSystemWide = 0;
                bool anyProfileDataActuallyChangedDuringThisOperation = false;

                var characterProfilesSnapshot = modelVM.CharacterProfiles.ToList(); // Iterate over a snapshot

                foreach (var characterProfile in characterProfilesSnapshot)
                {
                    token.ThrowIfCancellationRequested();
                    if (characterProfile.SourceImagePaths == null || characterProfile.SourceImagePaths.Count < 2)
                    {
                        SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Profil '{characterProfile.CategoryName}' ma mniej niż 2 obrazy, pomijanie.");
                        continue;
                    }

                    StatusMessage = $"Przetwarzanie profilu postaci: {characterProfile.CategoryName}...";
                    SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Przetwarzanie profilu: {characterProfile.CategoryName}");

                    var imageEntriesInProfile = new List<ImageFileEntry>();
                    bool profileHadMissingFiles = false;
                    foreach (string imagePath in characterProfile.SourceImagePaths) // Use the paths from the profile
                    {
                        token.ThrowIfCancellationRequested();
                        if (!File.Exists(imagePath))
                        {
                            SimpleFileLogger.LogWarning($"[RemoveDuplicatesInModel] Plik '{imagePath}' z profilu '{characterProfile.CategoryName}' nie istnieje na dysku. Zostanie usunięty z definicji profilu.");
                            profileHadMissingFiles = true; // Mark that profile needs update even if no duplicates found
                            continue;
                        }
                        var entry = await _imageMetadataService.ExtractMetadataAsync(imagePath);
                        if (entry != null)
                        {
                            entry.FeatureVector = await _profileService.GetImageEmbeddingAsync(entry); // Ensure embedding is loaded
                            if (entry.FeatureVector != null)
                            {
                                imageEntriesInProfile.Add(entry);
                            }
                            else
                            {
                                SimpleFileLogger.LogWarning($"[RemoveDuplicatesInModel] Nie udało się pobrać embeddingu dla '{entry.FilePath}' w profilu '{characterProfile.CategoryName}'. Pomijanie tego obrazu w analizie duplikatów.");
                            }
                        }
                    }
                    token.ThrowIfCancellationRequested();

                    if (imageEntriesInProfile.Count < 2) // If after loading, not enough images with embeddings
                    {
                        SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Profil '{characterProfile.CategoryName}' po weryfikacji plików i embeddingów ma mniej niż 2 obrazy. Pomijanie.");
                        if (profileHadMissingFiles) // If files were missing, the profile definition needs update
                        {
                            SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Profil '{characterProfile.CategoryName}' wymaga aktualizacji z powodu brakujących plików. Aktualna liczba wpisów: {imageEntriesInProfile.Count}");
                            await _profileService.GenerateProfileAsync(characterProfile.CategoryName, imageEntriesInProfile); // This will use the valid entries
                            anyProfileDataActuallyChangedDuringThisOperation = true;
                        }
                        continue;
                    }

                    var filesToRemovePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var processedInThisProfileForDuplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // To avoid re-comparing already processed images

                    for (int i = 0; i < imageEntriesInProfile.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var currentImage = imageEntriesInProfile[i];
                        if (filesToRemovePaths.Contains(currentImage.FilePath) || processedInThisProfileForDuplicates.Contains(currentImage.FilePath)) continue;

                        var duplicateGroupForCurrentImage = new List<ImageFileEntry> { currentImage };

                        for (int j = i + 1; j < imageEntriesInProfile.Count; j++)
                        {
                            token.ThrowIfCancellationRequested();
                            var otherImage = imageEntriesInProfile[j];
                            if (filesToRemovePaths.Contains(otherImage.FilePath) || processedInThisProfileForDuplicates.Contains(otherImage.FilePath)) continue;

                            if (currentImage.FeatureVector != null && otherImage.FeatureVector != null) // Both must have embeddings
                            {
                                double similarity = Utils.MathUtils.CalculateCosineSimilarity(currentImage.FeatureVector, otherImage.FeatureVector);
                                if (similarity >= DUPLICATE_SIMILARITY_THRESHOLD)
                                {
                                    duplicateGroupForCurrentImage.Add(otherImage);
                                }
                            }
                        }

                        if (duplicateGroupForCurrentImage.Count > 1)
                        {
                            ImageFileEntry bestImageInGroup = duplicateGroupForCurrentImage.First();
                            foreach (var imageInGroup in duplicateGroupForCurrentImage.Skip(1))
                            {
                                if (IsImageBetter(imageInGroup, bestImageInGroup))
                                {
                                    bestImageInGroup = imageInGroup;
                                }
                            }

                            // Mark all others in the group for removal
                            foreach (var imageInGroup in duplicateGroupForCurrentImage)
                            {
                                if (!imageInGroup.FilePath.Equals(bestImageInGroup.FilePath, StringComparison.OrdinalIgnoreCase))
                                {
                                    filesToRemovePaths.Add(imageInGroup.FilePath);
                                    SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Oznaczono duplikat do usunięcia: '{imageInGroup.FilePath}' (lepsza wersja: '{bestImageInGroup.FilePath}') w profilu '{characterProfile.CategoryName}'.");
                                }
                                processedInThisProfileForDuplicates.Add(imageInGroup.FilePath); // Mark all in group as processed
                            }
                        }
                        else
                        {
                            processedInThisProfileForDuplicates.Add(currentImage.FilePath); // Mark as processed even if not part of a duplicate group
                        }
                    } // End for each image in profile
                    token.ThrowIfCancellationRequested();

                    if (filesToRemovePaths.Any())
                    {
                        SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Profil '{characterProfile.CategoryName}': Znaleziono {filesToRemovePaths.Count} duplikatów do usunięcia z dysku.");
                        int removedThisProfileCount = 0;
                        foreach (var pathToRemoveLoopVar in filesToRemovePaths) // Use a different loop variable name
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                if (File.Exists(pathToRemoveLoopVar)) // Check again before deleting
                                {
                                    File.Delete(pathToRemoveLoopVar);
                                    SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Usunięto plik z dysku: {pathToRemoveLoopVar}");
                                    totalDuplicatesRemovedSystemWide++;
                                    removedThisProfileCount++;
                                }
                            }
                            catch (Exception ex) { SimpleFileLogger.LogError($"[RemoveDuplicatesInModel] Błąd podczas usuwania pliku duplikatu '{pathToRemoveLoopVar}' z dysku.", ex); }
                        }

                        // Update the profile with only the kept images
                        var keptImageEntries = imageEntriesInProfile.Where(e => !filesToRemovePaths.Contains(e.FilePath)).ToList();
                        await _profileService.GenerateProfileAsync(characterProfile.CategoryName, keptImageEntries);
                        anyProfileDataActuallyChangedDuringThisOperation = true;
                        SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Profil '{characterProfile.CategoryName}' zaktualizowany. Usunięto {removedThisProfileCount} plików. Pozostało: {keptImageEntries.Count}.");
                    }
                    else if (profileHadMissingFiles) // No duplicates found, but files were missing earlier
                    {
                        SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Profil '{characterProfile.CategoryName}' nie miał duplikatów, ale wymaga aktualizacji z powodu wcześniejszych brakujących plików.");
                        var validEntries = imageEntriesInProfile.Where(e => File.Exists(e.FilePath)).ToList(); // Ensure we only use existing files
                        await _profileService.GenerateProfileAsync(characterProfile.CategoryName, validEntries);
                        anyProfileDataActuallyChangedDuringThisOperation = true;
                    }
                    else
                    {
                        SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Profil '{characterProfile.CategoryName}': Nie znaleziono duplikatów do usunięcia.");
                    }

                } // End foreach characterProfile in model
                token.ThrowIfCancellationRequested();

                if (totalDuplicatesRemovedSystemWide > 0 || anyProfileDataActuallyChangedDuringThisOperation)
                {
                    StatusMessage = $"Zakończono usuwanie duplikatów dla modelki '{modelName}'. Usunięto łącznie: {totalDuplicatesRemovedSystemWide} plików. Odświeżanie widoku...";
                    _isRefreshingProfilesPostMove = true; // Signal that a refresh is needed
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                    MessageBox.Show($"Usunięto {totalDuplicatesRemovedSystemWide} zduplikowanych plików dla modelki '{modelName}'.", "Usuwanie Duplikatów Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    StatusMessage = $"Nie znaleziono żadnych duplikatów do usunięcia dla modelki '{modelName}'.";
                    MessageBox.Show(StatusMessage, "Usuwanie Duplikatów Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
                }

            }, "Usuwanie duplikatów w profilach modelki");

        private Task ExecuteApplyAllMatchesForModelAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                SimpleFileLogger.Log($"[ExecuteApplyAllMatchesForModelAsync] WYWOŁANO. Parameter: {parameter?.GetType().FullName ?? "null"}");
                if (!(parameter is ModelDisplayViewModel modelVM))
                {
                    StatusMessage = "Błąd: Nieprawidłowy parametr dla 'Zastosuj wszystkie dopasowania' (oczekiwano ModelDisplayViewModel).";
                    SimpleFileLogger.LogWarning(StatusMessage);
                    MessageBox.Show(StatusMessage, "Błąd Operacji", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string modelName = modelVM.ModelName;
                // Check if the cached suggestions are for the current model or if they are global and relevant
                bool hasRelevantCachedSuggestions =
                    (_lastScannedModelNameForSuggestions == modelName && _lastModelSpecificSuggestions.Any(m => _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelName)) ||
                    (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) && _lastModelSpecificSuggestions.Any(m => _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelName));


                if (!hasRelevantCachedSuggestions)
                {
                    StatusMessage = $"Brak zapisanych sugestii do zastosowania dla modelki '{modelName}'. Najpierw uruchom skanowanie dopasowań ('Szukaj dopasowań dla tej modelki' lub 'Globalne wyszukiwanie').";
                    MessageBox.Show(StatusMessage, "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                    return;
                }

                var movesToApply = _lastModelSpecificSuggestions
                    .Where(m => m.Similarity >= SuggestionSimilarityThreshold && _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelName)
                    .ToList();

                if (!movesToApply.Any())
                {
                    StatusMessage = $"Brak sugestii (powyżej progu {SuggestionSimilarityThreshold:F2}) do zastosowania dla modelki '{modelName}'.";
                    MessageBox.Show(StatusMessage, "Brak Sugestii do Zastosowania", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                    return;
                }

                if (MessageBox.Show($"Czy na pewno chcesz automatycznie zastosować {movesToApply.Count} dopasowań dla modelki '{modelName}'?\nSpowoduje to przeniesienie/usunięcie plików bez dodatkowego podglądu.",
                                    "Potwierdź Zastosowanie Wszystkich Dopasowań", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    StatusMessage = "Zastosowanie wszystkich dopasowań anulowane przez użytkownika.";
                    SimpleFileLogger.Log($"[ApplyAllMatchesForModel] Anulowano dla modelki: {modelName}.");
                    return;
                }

                StatusMessage = $"Rozpoczynanie automatycznego stosowania {movesToApply.Count} dopasowań dla modelki: {modelName}...";
                SimpleFileLogger.Log($"[ApplyAllMatchesForModel] Rozpoczęto dla modelki: {modelName}. Liczba ruchów: {movesToApply.Count}. Token: {token.GetHashCode()}");

                // InternalHandleApprovedMovesAsync will modify _lastModelSpecificSuggestions by removing processed items.
                bool anyProfileDataActuallyChanged = await InternalHandleApprovedMovesAsync(new List<Models.ProposedMove>(movesToApply), modelVM, null, token);
                token.ThrowIfCancellationRequested();

                // Since InternalHandleApprovedMovesAsync now removes processed items from _lastModelSpecificSuggestions,
                // we don't need to call ClearModelSpecificSuggestionsCache() if we want to preserve any remaining global suggestions
                // that were not part of this model's batch.
                // However, if this command is strictly for THIS model's cached suggestions, then the filtered `movesToApply`
                // covers what was intended. The `_lastModelSpecificSuggestions` will be updated by `InternalHandleApprovedMovesAsync`.


                if (anyProfileDataActuallyChanged)
                {
                    StatusMessage = $"Zakończono automatyczne stosowanie dopasowań dla modelki '{modelName}'. Odświeżanie widoku...";
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }
                else
                {
                    StatusMessage = $"Zakończono automatyczne stosowanie dopasowań dla modelki '{modelName}'. Nie wykryto zmian w profilach do odświeżenia UI.";
                }
                RefreshPendingSuggestionCountsFromCache(); // Update UI counts based on remaining suggestions in cache
                MessageBox.Show($"Zastosowano {movesToApply.Count} dopasowań dla modelki '{modelName}'.", "Zastosowano Wszystkie Dopasowania", MessageBoxButton.OK, MessageBoxImage.Information);


            }, "Automatyczne stosowanie wszystkich dopasowań dla modelki");


        // Helper for comparing ImageFileEntry objects, e.g., for removing duplicates from lists.
        private class ImageFileEntryPathComparer : IEqualityComparer<ImageFileEntry>
        {
            public bool Equals(ImageFileEntry? x, ImageFileEntry? y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x is null || y is null) return false;
                return x.FilePath.Equals(y.FilePath, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(ImageFileEntry obj)
            {
                return obj.FilePath?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0;
            }
        }
    }
}