// Plik: ViewModels/MainWindowViewModel.cs
using CosplayManager.Models;
using CosplayManager.Services;
using CosplayManager.ViewModels.Base;
using CosplayManager.Views;
using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
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
        private string? _lastScannedModelNameForSuggestions;
        private bool _isRefreshingProfilesPostMove = false;

        private const double DUPLICATE_SIMILARITY_THRESHOLD = 0.98;
        private CancellationTokenSource? _activeLongOperationCts;

        private const int MAX_CONCURRENT_EMBEDDING_REQUESTS = 4;
        private readonly SemaphoreSlim _embeddingSemaphore = new SemaphoreSlim(MAX_CONCURRENT_EMBEDDING_REQUESTS, MAX_CONCURRENT_EMBEDDING_REQUESTS);
        private readonly object _profileChangeLock = new object();

        private class ProcessingResult
        {
            public int FilesWithEmbeddingsIncrement { get; set; } = 0;
            public int AutoActionsIncrement { get; set; } = 0;
            public bool ProfileDataChanged { get; set; } = false;
        }

        private bool _enableDebugLogging = false;
        public bool EnableDebugLogging
        {
            get => _enableDebugLogging;
            set
            {
                if (SetProperty(ref _enableDebugLogging, value))
                {
                    SimpleFileLogger.IsDebugLoggingEnabled = value;
                    SimpleFileLogger.LogHighLevelInfo($"Debug logging {(value ? "enabled" : "disabled")} by user.");
                }
            }
        }

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
        public ICommand SuggestImagesCommand { get; }
        public ICommand SaveAppSettingsCommand { get; }
        public ICommand MatchModelSpecificCommand { get; }
        public ICommand CheckCharacterSuggestionsCommand { get; }
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

            // Wczytaj ustawienia i zainicjuj stan logowania debugowania
            // Ta część została przeniesiona do InitializeAsync, aby była wołana po konstruktorze
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
            SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Rozpoczęto '{statusMessagePrefix}'. Token: {token.GetHashCode()}");

            try
            {
                await operation(token);
                if (token.IsCancellationRequested)
                {
                    StatusMessage = $"{statusMessagePrefix} - Anulowano.";
                    SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Operacja '{statusMessagePrefix}' (token: {token.GetHashCode()}) anulowana przez użytkownika.");
                }
                else
                {
                    SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Operacja '{statusMessagePrefix}' (token: {token.GetHashCode()}) zakończona (lub przerwana wewnętrznie).");
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = $"{statusMessagePrefix} - Anulowano.";
                SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Operacja '{statusMessagePrefix}' (token: {token.GetHashCode()}) anulowana (OperationCanceledException).");
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
                if (StatusMessage.EndsWith("... (Można anulować)"))
                {
                    StatusMessage = $"{statusMessagePrefix} - Zakończono.";
                }
                SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Zakończono (finally) dla '{statusMessagePrefix}' (token: {token.GetHashCode()}). Aktualny StatusMessage: {StatusMessage}");
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
            SimpleFileLogger.LogHighLevelInfo("ClearModelSpecificSuggestionsCache: Czyszczenie cache sugestii.");
            _lastModelSpecificSuggestions.Clear();
            _lastScannedModelNameForSuggestions = "__CACHE_CLEARED__";
            RefreshPendingSuggestionCountsFromCache();
        }

        private UserSettings GetCurrentSettings() => new UserSettings
        {
            LibraryRootPath = this.LibraryRootPath,
            SourceFolderNamesInput = this.SourceFolderNamesInput,
            SuggestionSimilarityThreshold = this.SuggestionSimilarityThreshold,
            EnableDebugLogging = this.EnableDebugLogging // <-- ZAPIS USTAWIENIA
        };

        private void ApplySettings(UserSettings settings)
        {
            if (settings == null) return;
            LibraryRootPath = settings.LibraryRootPath;
            SourceFolderNamesInput = settings.SourceFolderNamesInput;
            SuggestionSimilarityThreshold = settings.SuggestionSimilarityThreshold;
            EnableDebugLogging = settings.EnableDebugLogging; // <-- ODCZYT USTAWIENIA I AKTUALIZACJA SimpleFileLogger
            SimpleFileLogger.IsDebugLoggingEnabled = this.EnableDebugLogging;
            SimpleFileLogger.LogHighLevelInfo($"Zastosowano wczytane ustawienia. Debug logging: {(EnableDebugLogging ? "Enabled" : "Disabled")}.");
        }

        private Task ExecuteSaveAppSettingsAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                token.ThrowIfCancellationRequested();
                await _settingsService.SaveSettingsAsync(GetCurrentSettings());
                StatusMessage = "Ustawienia aplikacji zapisane.";
                SimpleFileLogger.LogHighLevelInfo("Ustawienia aplikacji zapisane (na żądanie).");
            }, "Zapisywanie ustawień aplikacji");

        public Task InitializeAsync() =>
            RunLongOperation(async token =>
            {
                SimpleFileLogger.LogHighLevelInfo("ViewModel: InitializeAsync start.");
                ApplySettings(await _settingsService.LoadSettingsAsync()); // ApplySettings ustawi SimpleFileLogger.IsDebugLoggingEnabled
                token.ThrowIfCancellationRequested();
                await InternalExecuteLoadProfilesAsync(token);
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(LibraryRootPath)) StatusMessage = "Gotowy. Wybierz folder biblioteki.";
                else if (!Directory.Exists(LibraryRootPath)) StatusMessage = $"Uwaga: Folder biblioteki '{LibraryRootPath}' nie istnieje.";
                else StatusMessage = "Gotowy.";
                SimpleFileLogger.LogHighLevelInfo("ViewModel: InitializeAsync koniec.");
            }, "Inicjalizacja aplikacji");

        public async Task OnAppClosingAsync()
        {
            SimpleFileLogger.LogHighLevelInfo("ViewModel: OnAppClosingAsync - Anulowanie operacji i zapis ustawień...");
            if (_activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested)
            {
                _activeLongOperationCts.Cancel();
                try
                {
                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1), _activeLongOperationCts.Token), Task.Run(() => { }, _activeLongOperationCts.Token));
                }
                catch (OperationCanceledException) { /* Expected */ }
                _activeLongOperationCts.Dispose();
                _activeLongOperationCts = null;
            }
            await _settingsService.SaveSettingsAsync(GetCurrentSettings());
            SimpleFileLogger.LogHighLevelInfo("ViewModel: OnAppClosingAsync - Ustawienia zapisane.");
        }

        private bool CanExecuteLoadProfiles(object? arg) => !IsBusy;
        private bool CanExecuteSaveAllProfiles(object? arg) => !IsBusy && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles);
        private bool CanExecuteAutoCreateProfiles(object? arg) => !IsBusy && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath);
        private bool CanExecuteGenerateProfile(object? parameter = null) => !IsBusy && !string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) && !string.IsNullOrWhiteSpace(ModelNameInput) && !string.IsNullOrWhiteSpace(CharacterNameInput) && ImageFiles.Any();
        private bool CanExecuteSuggestImages(object? parameter = null) => !IsBusy && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles) && !string.IsNullOrWhiteSpace(SourceFolderNamesInput);
        private bool CanExecuteRemoveProfile(object? parameter) => !IsBusy && (parameter is CategoryProfile || SelectedProfile != null);
        private bool CanExecuteCheckCharacterSuggestions(object? parameter)
        {
            if (IsBusy) return false;
            var profileToCheck = (parameter as CategoryProfile) ?? SelectedProfile;
            if (profileToCheck == null) return false;

            bool hasCentroid = profileToCheck.CentroidEmbedding != null;
            bool hasPending = profileToCheck.PendingSuggestionsCount > 0;

            return !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) &&
                   !string.IsNullOrWhiteSpace(SourceFolderNamesInput) && hasCentroid && hasPending;
        }
        private bool CanExecuteMatchModelSpecific(object? parameter)
        {
            if (IsBusy) return false;
            if (!(parameter is ModelDisplayViewModel modelVM)) return false;
            return !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) &&
                   modelVM.HasCharacterProfiles && !string.IsNullOrWhiteSpace(SourceFolderNamesInput);
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
                bool hasApplicableSuggestions =
                    (_lastScannedModelNameForSuggestions == modelVM.ModelName && _lastModelSpecificSuggestions.Any(m => m.Similarity >= SuggestionSimilarityThreshold && _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelVM.ModelName)) ||
                    (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) && _lastModelSpecificSuggestions.Any(m => m.Similarity >= SuggestionSimilarityThreshold && _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelVM.ModelName));

                return !IsBusy && modelVM.HasCharacterProfiles && hasApplicableSuggestions;
            }
            return false;
        }

        private Task ExecuteLoadProfilesAsync(object? parameter = null) =>
            RunLongOperation(InternalExecuteLoadProfilesAsync, "Ładowanie profili");

        private async Task InternalExecuteLoadProfilesAsync(CancellationToken token)
        {
            SimpleFileLogger.LogHighLevelInfo($"InternalExecuteLoadProfilesAsync. RefreshFlag: {_isRefreshingProfilesPostMove}. Token: {token.GetHashCode()}");
            if (!_isRefreshingProfilesPostMove)
            {
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
                SimpleFileLogger.LogHighLevelInfo($"Wątek UI: Załadowano profile. Status: {StatusMessage}");

                if (!string.IsNullOrEmpty(prevSelectedName))
                {
                    SelectedProfile = flatProfiles?.FirstOrDefault(p => p.CategoryName.Equals(prevSelectedName, StringComparison.OrdinalIgnoreCase));
                }
                else if (SelectedProfile != null && !(flatProfiles?.Any(p => p.CategoryName == SelectedProfile.CategoryName) ?? false))
                {
                    SelectedProfile = null;
                }
                OnPropertyChanged(nameof(AnyProfilesLoaded));

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
                SimpleFileLogger.LogHighLevelInfo($"Generowanie profilu '{catName}' ({ImageFiles.Count} obr.). Token: {token.GetHashCode()}");

                List<ImageFileEntry> entriesToProcess = new List<ImageFileEntry>();
                foreach (var file in ImageFiles)
                {
                    token.ThrowIfCancellationRequested();
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
                if (!entriesToProcess.Any() && ImageFiles.Any())
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
                    SelectedProfile = _profileService.GetProfile(catName);
                }
            }, "Generowanie profilu");

        private Task ExecuteSaveAllProfilesAsync(object? parameter = null) =>
           RunLongOperation(async token =>
           {
               SimpleFileLogger.LogHighLevelInfo($"Zapis wszystkich profili. Token: {token.GetHashCode()}");
               await _profileService.SaveAllProfilesAsync();
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
                    SimpleFileLogger.LogHighLevelInfo($"Usuwanie profilu '{name}'. Token: {token.GetHashCode()}");
                    if (await _profileService.RemoveProfileAsync(name))
                    {
                        StatusMessage = $"Profil '{name}' usunięty.";
                        if (SelectedProfile?.CategoryName == name) SelectedProfile = null;
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
                IsBusy = true;
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
                                ImageFiles.Add(entry);
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
            SelectedProfile = null;
            ModelNameInput = string.Empty;
            CharacterNameInput = string.Empty;
            ImageFiles.Clear();
            StatusMessage = "Gotowy do utworzenia nowego profilu. Wprowadź nazwę modelki i postaci.";
        }
        private void ExecuteSelectLibraryPath(object? parameter = null)
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog { Description = "Wybierz główny folder biblioteki dla Cosplay Managera", UseDescriptionForTitle = true, ShowNewFolderButton = true };
                if (!string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath)) dialog.SelectedPath = LibraryRootPath;
                else if (Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))) dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

                if (dialog.ShowDialog(Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive)) == true)
                {
                    LibraryRootPath = dialog.SelectedPath;
                    StatusMessage = $"Wybrano folder biblioteki: {LibraryRootPath}";
                }
            }
            catch (Exception ex) { SimpleFileLogger.LogError("Błąd wyboru folderu biblioteki przez użytkownika", ex); MessageBox.Show($"Błąd podczas otwierania dialogu wyboru folderu: {ex.Message}", "Błąd Dialogu", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { IsBusy = false; }
        }

        private Task ExecuteAutoCreateProfilesAsync(object? parameter) =>
        RunLongOperation(async token =>
        {
            SimpleFileLogger.LogHighLevelInfo($"AutoCreateProfiles: Rozpoczęto skanowanie folderu biblioteki: {LibraryRootPath}. Token: {token.GetHashCode()}");
            var mixedFoldersToIgnore = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
            token.ThrowIfCancellationRequested();

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

            var modelProcessingTasks = new List<Task<(int profilesChanged, bool anyDataChanged)>>();
            foreach (var modelDir in modelDirectories)
            {
                token.ThrowIfCancellationRequested();
                string currentModelName = Path.GetFileName(modelDir);
                if (string.IsNullOrWhiteSpace(currentModelName) || mixedFoldersToIgnore.Contains(currentModelName))
                {
                    SimpleFileLogger.Log($"AutoCreateProfiles: Pomijanie folderu na poziomie biblioteki: '{currentModelName}' (może być folderem Mix lub ignorowanym).");
                    continue;
                }

                modelProcessingTasks.Add(Task.Run(async () => {
                    int profilesChangedForThisModel = await InternalProcessDirectoryForProfileCreationAsync(
                        modelDir,
                        currentModelName,
                        new List<string>(),
                        mixedFoldersToIgnore,
                        token);
                    return (profilesChangedForThisModel, profilesChangedForThisModel > 0);
                }, token));
            }

            var results = await Task.WhenAll(modelProcessingTasks);
            token.ThrowIfCancellationRequested();

            int totalProfilesCreatedOrUpdated = results.Sum(r => r.profilesChanged);
            bool anyProfileDataChangedDuringOperation = results.Any(r => r.anyDataChanged);

            StatusMessage = $"Automatyczne tworzenie profili zakończone. Utworzono/zaktualizowano: {totalProfilesCreatedOrUpdated} profili.";

            if (anyProfileDataChangedDuringOperation)
            {
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token);
                _isRefreshingProfilesPostMove = false;
            }
            MessageBox.Show(StatusMessage, "Skanowanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
        }, "Automatyczne tworzenie profili (wielowątkowe)");


        private async Task<int> InternalProcessDirectoryForProfileCreationAsync(
            string currentDirectoryPath,
            string modelNameForProfile,
            List<string> parentCharacterPathParts,
            HashSet<string> mixedFoldersToIgnore,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int profilesGeneratedOrUpdatedThisCall = 0;
            string currentSegmentName = Path.GetFileName(currentDirectoryPath);

            var currentCharacterPathSegments = new List<string>(parentCharacterPathParts);

            string modelRootForCurrentModel = Path.Combine(LibraryRootPath, modelNameForProfile);
            if (!currentDirectoryPath.Equals(modelRootForCurrentModel, StringComparison.OrdinalIgnoreCase) &&
                !mixedFoldersToIgnore.Contains(currentSegmentName))
            {
                currentCharacterPathSegments.Add(currentSegmentName);
            }

            string characterFullName = string.Join(" - ", currentCharacterPathSegments);
            string categoryName;

            if (string.IsNullOrWhiteSpace(characterFullName))
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
                var entriesForProfile = new ConcurrentBag<ImageFileEntry>();
                var metadataTasks = imagePathsInThisExactDirectory.Select(async path =>
                {
                    token.ThrowIfCancellationRequested();
                    var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                    if (entry != null) entriesForProfile.Add(entry);
                }).ToList();
                await Task.WhenAll(metadataTasks);

                token.ThrowIfCancellationRequested();
                if (entriesForProfile.Any())
                {
                    await _profileService.GenerateProfileAsync(categoryName, entriesForProfile.ToList());
                    profilesGeneratedOrUpdatedThisCall++;
                    SimpleFileLogger.Log($"InternalProcessDir: Wygenerowano/zaktualizowano profil '{categoryName}' z {entriesForProfile.Count} obrazami z folderu '{currentDirectoryPath}'.");
                }
            }
            else if (_profileService.GetProfile(categoryName) != null &&
                     (categoryName.Equals($"{modelNameForProfile} - General", StringComparison.OrdinalIgnoreCase) ||
                      !string.IsNullOrWhiteSpace(characterFullName)))
            {
                if (!mixedFoldersToIgnore.Contains(Path.GetFileName(currentDirectoryPath)))
                {
                    await _profileService.GenerateProfileAsync(categoryName, new List<ImageFileEntry>());
                    SimpleFileLogger.Log($"InternalProcessDir: Profil '{categoryName}' istniał, ale folder '{currentDirectoryPath}' jest pusty. Profil wyczyszczony.");
                    profilesGeneratedOrUpdatedThisCall++;
                }
            }
            token.ThrowIfCancellationRequested();

            try
            {
                var subDirectories = Directory.GetDirectories(currentDirectoryPath);
                var subDirProcessingTasks = new List<Task<int>>();

                foreach (var subDirectoryPath in subDirectories)
                {
                    token.ThrowIfCancellationRequested();
                    subDirProcessingTasks.Add(Task.Run(async () =>
                        await InternalProcessDirectoryForProfileCreationAsync(
                            subDirectoryPath,
                            modelNameForProfile,
                            new List<string>(currentCharacterPathSegments),
                            mixedFoldersToIgnore,
                            token), token));
                }

                var subDirResults = await Task.WhenAll(subDirProcessingTasks);
                profilesGeneratedOrUpdatedThisCall += subDirResults.Sum();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { SimpleFileLogger.LogError($"InternalProcessDir: Błąd przetwarzania podfolderów dla '{currentDirectoryPath}'", ex); }

            return profilesGeneratedOrUpdatedThisCall;
        }


        private bool IsImageBetter(ImageFileEntry entry1, ImageFileEntry entry2)
        {
            if (entry1 == null || entry2 == null) return false;
            long resolution1 = (long)entry1.Width * entry1.Height;
            long resolution2 = (long)entry2.Width * entry2.Height;
            if (resolution1 > resolution2) return true;
            if (resolution1 < resolution2) return false;
            return entry1.FileSize > entry2.FileSize;
        }

        private async Task<bool> HandleFileMovedOrDeletedUpdateProfilesAsync(string? oldPath, string? newPathIfMoved, string? targetCategoryNameIfMoved, CancellationToken token)
        {
            SimpleFileLogger.Log($"[ProfileUpdate] Rozpoczęto aktualizację profili. OldPath='{oldPath}', NewPath='{newPathIfMoved}', TargetCat='{targetCategoryNameIfMoved}'");
            var affectedProfileNamesForRegeneration = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyProfileDataActuallyRegenerated = false;
            var allProfilesInMemory = _profileService.GetAllProfiles().ToList();

            foreach (var profile in allProfilesInMemory)
            {
                token.ThrowIfCancellationRequested();
                bool currentProfileNeedsRegeneration = false;
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
                SimpleFileLogger.Log($"[ProfileUpdate] Zidentyfikowano {affectedProfileNamesForRegeneration.Count} profili do regeneracji: {string.Join(", ", affectedProfileNamesForRegeneration)}");
                foreach (var affectedName in affectedProfileNamesForRegeneration)
                {
                    token.ThrowIfCancellationRequested();
                    var affectedProfile = _profileService.GetProfile(affectedName);
                    if (affectedProfile == null)
                    {
                        SimpleFileLogger.LogWarning($"[ProfileUpdate] Nie można znaleźć profilu '{affectedName}' do regeneracji.");
                        continue;
                    }
                    var entriesForAffectedProfile = new List<ImageFileEntry>();
                    if (affectedProfile.SourceImagePaths != null)
                    {
                        foreach (var path in affectedProfile.SourceImagePaths)
                        {
                            token.ThrowIfCancellationRequested();
                            if (File.Exists(path))
                            {
                                var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                                if (entry != null) entriesForAffectedProfile.Add(entry);
                            }
                            else
                            {
                                SimpleFileLogger.LogWarning($"[ProfileUpdate] Ścieżka '{path}' w profilu '{affectedName}' nie istnieje na dysku.");
                            }
                        }
                    }
                    SimpleFileLogger.Log($"[ProfileUpdate] Regenerowanie profilu '{affectedName}' z {entriesForAffectedProfile.Count} obrazami.");
                    await _profileService.GenerateProfileAsync(affectedName, entriesForAffectedProfile);
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

        private async Task<(Models.ProposedMove? proposedMove, bool wasActionAutoHandled, bool profilesWereModified)> ProcessDuplicateOrSuggestNewAsync(
            ImageFileEntry sourceImageEntry, CategoryProfile targetProfile, double similarityToCentroid,
            string modelDirectoryPath, float[] sourceImageEmbedding, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            bool profilesModifiedByThisCall = false;
            var (_, characterFolderNamePart) = ParseCategoryName(targetProfile.CategoryName);
            string targetCharacterFolderPath = Path.Combine(modelDirectoryPath, SanitizeFolderName(characterFolderNamePart));
            Directory.CreateDirectory(targetCharacterFolderPath);
            List<string> filesInTargetDir;
            try
            {
                filesInTargetDir = Directory.EnumerateFiles(targetCharacterFolderPath, "*.*", SearchOption.TopDirectoryOnly)
                                        .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f))).ToList();
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"ProcessDuplicateOrSuggestNewAsync: Błąd odczytu plików z folderu docelowego: {targetCharacterFolderPath}", ex);
                filesInTargetDir = new List<string>();
            }

            foreach (string existingFilePathInTarget in filesInTargetDir)
            {
                token.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFullPath(existingFilePathInTarget), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var existingTargetEntry = await _imageMetadataService.ExtractMetadataAsync(existingFilePathInTarget);
                if (existingTargetEntry == null) continue;
                float[]? existingTargetEmbedding = existingTargetEntry.FeatureVector ?? await _profileService.GetImageEmbeddingAsync(existingTargetEntry);
                if (existingTargetEmbedding == null)
                {
                    SimpleFileLogger.LogWarning($"[ProcessDuplicateOrSuggestNewAsync] Nie udało się pobrać embeddingu dla istniejącego pliku: {existingTargetEntry.FilePath}");
                    continue;
                }
                double similarityBetweenSourceAndExisting = Utils.MathUtils.CalculateCosineSimilarity(sourceImageEmbedding, existingTargetEmbedding);
                if (similarityBetweenSourceAndExisting >= DUPLICATE_SIMILARITY_THRESHOLD)
                {
                    bool sourceIsCurrentlyBetter = IsImageBetter(sourceImageEntry, existingTargetEntry);
                    if (sourceIsCurrentlyBetter)
                    {
                        SimpleFileLogger.Log($"[AutoReplace] Lepsza wersja. Źródło: '{sourceImageEntry.FilePath}', Cel: '{existingTargetEntry.FilePath}'.");
                        try
                        {
                            File.Copy(sourceImageEntry.FilePath, existingTargetEntry.FilePath, true);
                            SimpleFileLogger.Log($"[AutoReplace] Nadpisano '{existingTargetEntry.FilePath}'.");
                            string oldSourcePath = sourceImageEntry.FilePath;
                            File.Delete(sourceImageEntry.FilePath);
                            SimpleFileLogger.Log($"[AutoReplace] Usunięto źródło '{oldSourcePath}'.");
                            if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldSourcePath, null, null, token))
                                profilesModifiedByThisCall = true;
                        }
                        catch (Exception ex) { SimpleFileLogger.LogError($"[AutoReplace] Błąd: {sourceImageEntry.FilePath} -> {existingTargetEntry.FilePath}", ex); }
                        return (null, true, profilesModifiedByThisCall);
                    }
                    else
                    {
                        SimpleFileLogger.Log($"[AutoDeleteSource] Istniejąca wersja w '{targetCharacterFolderPath}' lepsza/równa. Usuwanie '{sourceImageEntry.FilePath}'.");
                        try
                        {
                            string oldSourcePath = sourceImageEntry.FilePath;
                            File.Delete(sourceImageEntry.FilePath);
                            SimpleFileLogger.Log($"[AutoDeleteSource] Usunięto '{oldSourcePath}'.");
                            if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldSourcePath, null, null, token))
                                profilesModifiedByThisCall = true;
                        }
                        catch (Exception ex) { SimpleFileLogger.LogError($"[AutoDeleteSource] Błąd usuwania '{sourceImageEntry.FilePath}'", ex); }
                        return (null, true, profilesModifiedByThisCall);
                    }
                }
            }
            token.ThrowIfCancellationRequested();

            if (similarityToCentroid >= SuggestionSimilarityThreshold)
            {
                string proposedPathStandard = Path.Combine(targetCharacterFolderPath, sourceImageEntry.FileName);
                ProposedMoveActionType actionStandard;
                ImageFileEntry? displayTargetStandard = null;
                if (File.Exists(proposedPathStandard) &&
                    !string.Equals(Path.GetFullPath(proposedPathStandard), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    actionStandard = ProposedMoveActionType.ConflictKeepBoth;
                    displayTargetStandard = await _imageMetadataService.ExtractMetadataAsync(proposedPathStandard);
                }
                else if (string.Equals(Path.GetFullPath(proposedPathStandard), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    SimpleFileLogger.Log($"[Suggest] Plik '{sourceImageEntry.FilePath}' jest już w docelowej lokalizacji. Brak sugestii.");
                    return (null, false, profilesModifiedByThisCall);
                }
                else
                {
                    actionStandard = ProposedMoveActionType.CopyNew;
                }
                var move = new Models.ProposedMove(sourceImageEntry, displayTargetStandard, proposedPathStandard, similarityToCentroid, targetProfile.CategoryName, actionStandard, sourceImageEmbedding);
                SimpleFileLogger.Log($"[Suggest] Utworzono sugestię: {actionStandard} dla '{sourceImageEntry.FileName}' do '{targetProfile.CategoryName}', Sim: {similarityToCentroid:F4}");
                return (move, false, profilesModifiedByThisCall);
            }
            SimpleFileLogger.Log($"[Suggest] Plik '{sourceImageEntry.FileName}' (Sim: {similarityToCentroid:F4}) nie pasuje ({SuggestionSimilarityThreshold:F2}) do '{targetProfile.CategoryName}'.");
            return (null, false, profilesModifiedByThisCall);
        }

        private Task ExecuteMatchModelSpecificAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nie wybrano modelki."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                SimpleFileLogger.LogHighLevelInfo($"MatchModelSpecific dla '{modelVM.ModelName}'. Token: {token.GetHashCode()}");
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe."; MessageBox.Show(StatusMessage, "Brak Folderów Źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                var movesForSuggestionWindowConcurrent = new ConcurrentBag<Models.ProposedMove>();
                string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName);
                if (!Directory.Exists(modelPath)) { StatusMessage = $"Błąd: Folder modelki '{modelVM.ModelName}' nie istnieje."; MessageBox.Show(StatusMessage, "Błąd Folderu Modelki", MessageBoxButton.OK, MessageBoxImage.Error); return; }

                await Application.Current.Dispatcher.InvokeAsync(() => { modelVM.PendingSuggestionsCount = 0; foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; });
                token.ThrowIfCancellationRequested();

                long filesFoundInMix = 0;
                long currentFilesWithEmbeddings = 0;
                long currentAutoActionsCount = 0;
                bool currentAnyProfileDataChanged = false;
                var alreadySuggestedGraphicDuplicatesConcurrent = new ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)>();
                var processingTasks = new List<Task<ProcessingResult>>();

                foreach (var mixFolderName in mixedFolders)
                {
                    token.ThrowIfCancellationRequested();
                    string currentMixPath = Path.Combine(modelPath, mixFolderName);
                    if (Directory.Exists(currentMixPath))
                    {
                        var imagePathsInMix = await _fileScannerService.ScanDirectoryAsync(currentMixPath);
                        filesFoundInMix += imagePathsInMix.Count;
                        SimpleFileLogger.Log($"MatchModelSpecific: W '{currentMixPath}' znaleziono {imagePathsInMix.Count} obrazów.");

                        foreach (var imgPathFromMix in imagePathsInMix)
                        {
                            token.ThrowIfCancellationRequested();
                            processingTasks.Add(ProcessSingleImageForModelSpecificScanAsync(
                                imgPathFromMix, modelVM, modelPath, token,
                                movesForSuggestionWindowConcurrent, alreadySuggestedGraphicDuplicatesConcurrent
                            ));
                        }
                    }
                    else { SimpleFileLogger.LogWarning($"MatchModelSpecific: Folder źródłowy '{currentMixPath}' nie istnieje."); }
                }

                var taskResults = await Task.WhenAll(processingTasks);
                token.ThrowIfCancellationRequested();

                foreach (var result in taskResults)
                {
                    currentFilesWithEmbeddings += result.FilesWithEmbeddingsIncrement;
                    currentAutoActionsCount += result.AutoActionsIncrement;
                    if (result.ProfileDataChanged)
                        currentAnyProfileDataChanged = true;
                }

                var movesForSuggestionWindow = movesForSuggestionWindowConcurrent.ToList();
                SimpleFileLogger.LogHighLevelInfo($"MatchModelSpecific dla '{modelVM.ModelName}': Podsumowanie - Znaleziono: {filesFoundInMix}, Z embeddingami: {currentFilesWithEmbeddings}. Akcje auto: {currentAutoActionsCount}. Sugestie: {movesForSuggestionWindow.Count}. Profile zmodyfikowane: {currentAnyProfileDataChanged}");

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
                        previewWindow.SetViewModelCloseAction(previewVM);
                        dialogOutcome = previewWindow.ShowDialog();
                        if (dialogOutcome == true) approvedMoves = previewVM.GetApprovedMoves();
                    });
                    token.ThrowIfCancellationRequested();
                    if (dialogOutcome == true && approvedMoves.Any())
                    {
                        if (await InternalHandleApprovedMovesAsync(approvedMoves, modelVM, null, token))
                            currentAnyProfileDataChanged = true;
                        _lastModelSpecificSuggestions.RemoveAll(sugg => movesForSuggestionWindow.Contains(sugg));
                    }
                    else if (dialogOutcome == false) StatusMessage = $"Anulowano zmiany dla '{modelVM.ModelName}'.";
                }

                if (currentAnyProfileDataChanged)
                {
                    SimpleFileLogger.LogHighLevelInfo($"ExecuteMatchModelSpecificAsync: Zmiany w profilach dla '{modelVM.ModelName}'. Odświeżanie.");
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }
                RefreshPendingSuggestionCountsFromCache();
                StatusMessage = $"Dla '{modelVM.ModelName}': {currentAutoActionsCount} akcji auto., {modelVM.PendingSuggestionsCount} sugestii.";

                if (!movesForSuggestionWindow.Any() && currentAutoActionsCount > 0 && !currentAnyProfileDataChanged)
                {
                    MessageBox.Show($"Zakończono automatyczne operacje dla '{modelVM.ModelName}'. Wykonano {currentAutoActionsCount} akcji. Brak dodatkowych sugestii do przejrzenia.", "Operacje Automatyczne Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (!movesForSuggestionWindow.Any() && currentAutoActionsCount == 0 && !currentAnyProfileDataChanged && filesFoundInMix > 0)
                {
                    MessageBox.Show($"Brak nowych sugestii lub automatycznych akcji dla '{modelVM.ModelName}'. Sprawdź, czy foldery 'Mix' zawierają odpowiednie obrazy lub dostosuj próg podobieństwa.", "Brak Zmian", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (filesFoundInMix == 0)
                {
                    MessageBox.Show($"Nie znaleziono obrazów w folderach źródłowych (np. Mix) dla modelki '{modelVM.ModelName}'.", "Brak Plików Źródłowych", MessageBoxButton.OK, MessageBoxImage.Information);
                }

            }, "Dopasowywanie dla modelki (wielowątkowe)");


        private async Task<ProcessingResult> ProcessSingleImageForModelSpecificScanAsync(
            string imgPathFromMix, ModelDisplayViewModel modelVM, string modelPath, CancellationToken token,
            ConcurrentBag<Models.ProposedMove> movesForSuggestionWindowConcurrent,
            ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)> alreadySuggestedGraphicDuplicatesConcurrent)
        {
            var result = new ProcessingResult();
            if (token.IsCancellationRequested || !File.Exists(imgPathFromMix)) return result;

            var sourceEntry = await _imageMetadataService.ExtractMetadataAsync(imgPathFromMix);
            if (sourceEntry == null) { SimpleFileLogger.LogWarning($"MatchModelSpecific(AsyncItem): Nie udało się załadować metadanych dla: {imgPathFromMix}, pomijam."); return result; }

            await _embeddingSemaphore.WaitAsync(token);
            float[]? sourceEmbedding = null;
            try
            {
                if (token.IsCancellationRequested) return result;
                sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceEntry);
            }
            finally { _embeddingSemaphore.Release(); }

            if (sourceEmbedding == null) { SimpleFileLogger.LogWarning($"MatchModelSpecific(AsyncItem): Nie udało się załadować embeddingu dla: {sourceEntry.FilePath}, pomijam."); return result; }
            result.FilesWithEmbeddingsIncrement = 1;
            if (token.IsCancellationRequested) return result;

            var bestSuggestionForThisSourceImage = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName);

            if (bestSuggestionForThisSourceImage != null)
            {
                CategoryProfile targetProfile = bestSuggestionForThisSourceImage.Item1;
                double similarityToCentroid = bestSuggestionForThisSourceImage.Item2;

                var (proposedMove, wasActionAutoHandled, profilesModifiedByCall) = await ProcessDuplicateOrSuggestNewAsync(
                    sourceEntry, targetProfile, similarityToCentroid, modelPath, sourceEmbedding, token);

                if (profilesModifiedByCall) result.ProfileDataChanged = true;

                if (wasActionAutoHandled)
                {
                    result.AutoActionsIncrement = 1;
                }
                else if (proposedMove != null)
                {
                    movesForSuggestionWindowConcurrent.Add(proposedMove);
                }
            }
            else
            {
                SimpleFileLogger.Log($"MatchModelSpecific(AsyncItem): Brak sugestii kategorii dla '{sourceEntry.FilePath}' (model: {modelVM.ModelName}).");
            }
            return result;
        }


        private Task ExecuteSuggestImagesAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                ClearModelSpecificSuggestionsCache();
                token.ThrowIfCancellationRequested();
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe (np. 'Mix') w ustawieniach."; MessageBox.Show(StatusMessage, "Brak Folderów Źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                var allCollectedSuggestionsGlobalConcurrent = new ConcurrentBag<Models.ProposedMove>();
                var alreadySuggestedGraphicDuplicatesGlobalConcurrent = new ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)>();

                await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var mVM_ui in HierarchicalProfilesList) { mVM_ui.PendingSuggestionsCount = 0; foreach (var cp_ui in mVM_ui.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; } });
                token.ThrowIfCancellationRequested();

                var allModelsCurrentlyInList = HierarchicalProfilesList.ToList();

                long totalFilesFound = 0;
                long currentTotalFilesWithEmbeddings = 0;
                long currentTotalAutoActions = 0;
                bool currentAnyProfileDataChanged = false;

                var processingTasks = new List<Task<ProcessingResult>>();

                foreach (var modelVM in allModelsCurrentlyInList)
                {
                    token.ThrowIfCancellationRequested();
                    string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName);
                    if (!Directory.Exists(modelPath) || !modelVM.HasCharacterProfiles)
                    {
                        SimpleFileLogger.Log($"ExecuteSuggestImagesAsync: Pomijanie modelki '{modelVM.ModelName}' - folder nie istnieje lub brak profili postaci.");
                        continue;
                    }
                    SimpleFileLogger.LogHighLevelInfo($"ExecuteSuggestImagesAsync (Global Scan): Rozpoczynanie przetwarzania dla modelki '{modelVM.ModelName}'.");

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
                                processingTasks.Add(ProcessSingleImageForGlobalSuggestionsAsync(
                                    imgPathFromMix, modelVM, modelPath, token,
                                    allCollectedSuggestionsGlobalConcurrent,
                                    alreadySuggestedGraphicDuplicatesGlobalConcurrent
                                ));
                            }
                        }
                    }
                }

                var taskResults = await Task.WhenAll(processingTasks);
                token.ThrowIfCancellationRequested();

                foreach (var result in taskResults)
                {
                    currentTotalFilesWithEmbeddings += result.FilesWithEmbeddingsIncrement;
                    currentTotalAutoActions += result.AutoActionsIncrement;
                    if (result.ProfileDataChanged)
                        currentAnyProfileDataChanged = true;
                }

                var allCollectedSuggestionsGlobal = allCollectedSuggestionsGlobalConcurrent.ToList();
                SimpleFileLogger.LogHighLevelInfo($"ExecuteSuggestImagesAsync: Podsumowanie globalne - Znaleziono: {totalFilesFound}, Z embeddingami: {currentTotalFilesWithEmbeddings}, Akcje auto: {currentTotalAutoActions}, Sugestie zebrane: {allCollectedSuggestionsGlobal.Count}. Profile zmodyfikowane: {currentAnyProfileDataChanged}");

                _lastModelSpecificSuggestions = new List<Models.ProposedMove>(allCollectedSuggestionsGlobal);
                _lastScannedModelNameForSuggestions = null;

                StatusMessage = $"Globalne wyszukiwanie zakończone. {currentTotalAutoActions} akcji auto. {allCollectedSuggestionsGlobal.Count} potencjalnych sugestii znaleziono.";

                if (currentAnyProfileDataChanged)
                {
                    SimpleFileLogger.LogHighLevelInfo($"ExecuteSuggestImagesAsync: Wykryto zmiany w profilach podczas globalnego skanowania. Odświeżanie widoku UI.");
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }

                RefreshPendingSuggestionCountsFromCache();

                string completionMessage = StatusMessage;
                if (allCollectedSuggestionsGlobal.Any())
                {
                    completionMessage += " Użyj menu kontekstowego na modelkach/postaciach, aby przejrzeć znalezione sugestie.";
                }
                else if (currentTotalAutoActions == 0 && totalFilesFound > 0)
                {
                    completionMessage = "Globalne wyszukiwanie nie znalazło nowych sugestii ani nie wykonało akcji automatycznych.";
                }
                else if (totalFilesFound == 0)
                {
                    completionMessage = "Nie znaleziono żadnych plików w folderach źródłowych do globalnego skanowania.";
                }
                MessageBox.Show(completionMessage, "Globalne Wyszukiwanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);

            }, "Globalne wyszukiwanie sugestii (wielowątkowe)");


        private async Task<ProcessingResult> ProcessSingleImageForGlobalSuggestionsAsync(
            string imgPathFromMix, ModelDisplayViewModel modelVM, string modelPath, CancellationToken token,
            ConcurrentBag<Models.ProposedMove> allCollectedSuggestionsGlobalConcurrent,
            ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)> alreadySuggestedGraphicDuplicatesGlobalConcurrent)
        {
            var result = new ProcessingResult();
            if (token.IsCancellationRequested) return result;

            if (!File.Exists(imgPathFromMix))
            {
                SimpleFileLogger.Log($"ProcessSingleImageForGlobalSuggestionsAsync: Plik {imgPathFromMix} nie istnieje, pomijanie.");
                return result;
            }

            var sourceEntry = await _imageMetadataService.ExtractMetadataAsync(imgPathFromMix);
            if (sourceEntry == null) return result;

            float[]? sourceEmbedding = null;
            await _embeddingSemaphore.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested) return result;
                sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceEntry);
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Błąd pobierania embeddingu dla {sourceEntry.FilePath} w ProcessSingleImageForGlobalSuggestionsAsync", ex);
            }
            finally
            {
                _embeddingSemaphore.Release();
            }

            if (sourceEmbedding == null) return result;
            result.FilesWithEmbeddingsIncrement = 1;
            if (token.IsCancellationRequested) return result;

            var bestSuggestionForThisSourceImage = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName);

            if (bestSuggestionForThisSourceImage != null)
            {
                CategoryProfile targetProfile = bestSuggestionForThisSourceImage.Item1;
                double similarityToCentroid = bestSuggestionForThisSourceImage.Item2;

                var (proposedMove, wasActionAutoHandled, profilesModifiedByCall) = await ProcessDuplicateOrSuggestNewAsync(
                    sourceEntry, targetProfile, similarityToCentroid, modelPath, sourceEmbedding, token);

                if (profilesModifiedByCall) result.ProfileDataChanged = true;

                if (wasActionAutoHandled)
                {
                    result.AutoActionsIncrement = 1;
                }
                else if (proposedMove != null)
                {
                    allCollectedSuggestionsGlobalConcurrent.Add(proposedMove);
                }
            }
            return result;
        }


        private Task ExecuteCheckCharacterSuggestionsAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                var charProfileForSuggestions = (parameter as CategoryProfile) ?? SelectedProfile;
                if (charProfileForSuggestions == null) { StatusMessage = "Błąd: Wybierz profil postaci."; MessageBox.Show(StatusMessage, "Brak Wyboru Postaci", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                SimpleFileLogger.LogHighLevelInfo($"CheckCharacterSuggestions dla '{charProfileForSuggestions.CategoryName}'. Token: {token.GetHashCode()}");
                token.ThrowIfCancellationRequested();
                string modelName = _profileService.GetModelNameFromCategory(charProfileForSuggestions.CategoryName);
                var modelVM = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));

                var movesForThisCharacterWindow = new List<Models.ProposedMove>();
                bool anyProfileDataChangedDuringEntireOperation = false;

                if (_lastModelSpecificSuggestions.Any())
                {
                    if (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) ||
                        _lastScannedModelNameForSuggestions.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                    {
                        movesForThisCharacterWindow = _lastModelSpecificSuggestions
                            .Where(m => m.TargetCategoryProfileName.Equals(charProfileForSuggestions.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                                        m.Similarity >= SuggestionSimilarityThreshold)
                            .ToList();
                        SimpleFileLogger.Log($"CheckCharacterSuggestions: Użyto cache ({_lastModelSpecificSuggestions.Count}). Dla '{charProfileForSuggestions.CategoryName}' znaleziono {movesForThisCharacterWindow.Count}.");
                    }
                }

                if (!movesForThisCharacterWindow.Any())
                {
                    StatusMessage = $"Brak sugestii dla '{charProfileForSuggestions.CategoryName}'. Uruchom skanowanie globalne lub dla modelki '{modelName}'.";
                    MessageBox.Show(StatusMessage, "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Information);
                    var uiProfile = modelVM?.CharacterProfiles.FirstOrDefault(cp => cp.CategoryName == charProfileForSuggestions.CategoryName);
                    if (uiProfile != null) uiProfile.PendingSuggestionsCount = 0;
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
                        if (await InternalHandleApprovedMovesAsync(approved, modelVM, charProfileForSuggestions, token))
                            anyProfileDataChangedDuringEntireOperation = true;
                        _lastModelSpecificSuggestions.RemoveAll(sugg => approved.Any(ap => ap.SourceImage.FilePath == sugg.SourceImage.FilePath));
                    }
                    else if (outcome == false)
                    {
                        StatusMessage = $"Anulowano sugestie dla '{charProfileForSuggestions.CategoryName}'.";
                    }
                }

                if (anyProfileDataChangedDuringEntireOperation)
                {
                    SimpleFileLogger.LogHighLevelInfo($"CheckCharacterSuggestionsAsync: Zmiany w profilach dla '{charProfileForSuggestions.CategoryName}'. Odświeżanie.");
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }
                RefreshPendingSuggestionCountsFromCache();
            }, "Sprawdzanie sugestii dla postaci");

        private void RefreshPendingSuggestionCountsFromCache()
        {
            Application.Current.Dispatcher.Invoke(() => {
                foreach (var mVM_iter in HierarchicalProfilesList)
                {
                    mVM_iter.PendingSuggestionsCount = 0;
                    foreach (var cp_iter in mVM_iter.CharacterProfiles)
                    {
                        cp_iter.PendingSuggestionsCount = 0;
                    }
                }

                if (_lastModelSpecificSuggestions.Any())
                {
                    var relevantSuggestions = _lastModelSpecificSuggestions
                        .Where(sugg => sugg.Similarity >= SuggestionSimilarityThreshold)
                        .ToList();

                    if (!string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) && _lastScannedModelNameForSuggestions != "__CACHE_CLEARED__")
                    {
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
                            SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache (Specific Model '{_lastScannedModelNameForSuggestions}'): Updated counts. Total: {totalForModel}.");
                        }
                    }
                    else
                    {
                        if (_lastScannedModelNameForSuggestions != "__CACHE_CLEARED__")
                        {
                            SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache (Global Scan Results): Attributing {relevantSuggestions.Count} suggestions.");
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
                                    SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache (Global Scan): Model '{modelToUpdate.ModelName}' updated: {totalForModel} suggestions.");
                                }
                            }
                        }
                        else
                        {
                            SimpleFileLogger.Log("RefreshPendingSuggestionCountsFromCache: Cache was cleared.");
                        }
                    }
                }
                else
                {
                    SimpleFileLogger.Log("RefreshPendingSuggestionCountsFromCache: No suggestions in cache.");
                }
                CommandManager.InvalidateRequerySuggested();
            });
        }

        private async Task<bool> InternalHandleApprovedMovesAsync(List<Models.ProposedMove> approvedMoves, ModelDisplayViewModel? specificModelVM, CategoryProfile? specificCharacterProfile, CancellationToken token)
        {
            int successfulMoves = 0, copyErrors = 0, deleteErrors = 0, skippedQuality = 0, skippedOther = 0;
            bool anyProfileActuallyModified = false;
            var processedSourcePathsForThisBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var move in approvedMoves)
            {
                token.ThrowIfCancellationRequested();
                string sourcePath = move.SourceImage.FilePath;
                string targetPath = move.ProposedTargetPath;
                string originalProposedTargetPathForLogging = move.ProposedTargetPath;
                var actionType = move.Action;
                bool operationSuccessfulThisMove = false;
                bool deleteSourceAfterCopy = false;

                try
                {
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    {
                        SimpleFileLogger.LogWarning($"[HandleApproved] Plik źródłowy nie istnieje: '{sourcePath}'. Pomijanie.");
                        skippedOther++;
                        continue;
                    }
                    string targetDirectory = Path.GetDirectoryName(targetPath);
                    if (string.IsNullOrEmpty(targetDirectory)) { SimpleFileLogger.LogWarning($"[HandleApproved] Nie można określić folderu docelowego dla: '{targetPath}'."); skippedOther++; continue; }
                    Directory.CreateDirectory(targetDirectory);

                    switch (actionType)
                    {
                        case ProposedMoveActionType.CopyNew:
                            if (File.Exists(targetPath) && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                targetPath = GenerateUniqueTargetPath(targetDirectory, Path.GetFileName(sourcePath), "_new_approved");
                                SimpleFileLogger.Log($"[HandleApproved] CopyNew: Plik docelowy '{originalProposedTargetPathForLogging}' istniał. Zmieniono na '{targetPath}'.");
                            }
                            else if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                SimpleFileLogger.LogWarning($"[HandleApproved] CopyNew: Plik źródłowy i docelowy to ten sam plik. Pomijanie kopiowania.");
                                deleteSourceAfterCopy = true;
                                operationSuccessfulThisMove = true;
                                break;
                            }
                            await Task.Run(() => File.Copy(sourcePath, targetPath, false), token);
                            operationSuccessfulThisMove = true;
                            deleteSourceAfterCopy = true;
                            SimpleFileLogger.Log($"[HandleApproved] CopyNew: Skopiowano '{sourcePath}' do '{targetPath}'.");
                            break;
                        case ProposedMoveActionType.OverwriteExisting:
                            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                SimpleFileLogger.LogWarning($"[HandleApproved] OverwriteExisting: Plik źródłowy i docelowy to ten sam plik. Pomijanie nadpisywania.");
                                deleteSourceAfterCopy = true;
                                operationSuccessfulThisMove = true;
                                break;
                            }
                            await Task.Run(() => File.Copy(sourcePath, targetPath, true), token);
                            operationSuccessfulThisMove = true;
                            deleteSourceAfterCopy = true;
                            SimpleFileLogger.Log($"[HandleApproved] OverwriteExisting: Nadpisano '{targetPath}' plikiem '{sourcePath}'.");
                            break;
                        case ProposedMoveActionType.KeepExistingDeleteSource:
                            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                deleteSourceAfterCopy = true;
                                operationSuccessfulThisMove = true;
                                SimpleFileLogger.Log($"[HandleApproved] KeepExistingDeleteSource: Zachowano '{targetPath}'. Źródło '{sourcePath}' do usunięcia.");
                            }
                            else
                            {
                                SimpleFileLogger.LogWarning($"[HandleApproved] KeepExistingDeleteSource: Plik źródłowy i docelowy to ten sam plik. Nic do zrobienia.");
                                operationSuccessfulThisMove = true;
                                skippedQuality++;
                            }
                            break;
                        case ProposedMoveActionType.ConflictKeepBoth:
                            string newTargetPathForConflict = GenerateUniqueTargetPath(targetDirectory, Path.GetFileName(sourcePath), "_conflict_approved");
                            await Task.Run(() => File.Copy(sourcePath, newTargetPathForConflict, false), token);
                            targetPath = newTargetPathForConflict;
                            operationSuccessfulThisMove = true;
                            deleteSourceAfterCopy = true;
                            SimpleFileLogger.Log($"[HandleApproved] ConflictKeepBoth: Skopiowano '{sourcePath}' do '{targetPath}'.");
                            break;
                    }
                    token.ThrowIfCancellationRequested();

                    if (operationSuccessfulThisMove)
                    {
                        successfulMoves++;
                        processedSourcePathsForThisBatch.Add(sourcePath);
                        string? oldPathForProfileUpdate = null;
                        string? newPathForProfileUpdate = targetPath;

                        if (deleteSourceAfterCopy && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                        {
                            oldPathForProfileUpdate = sourcePath;
                            try
                            {
                                if (File.Exists(sourcePath))
                                {
                                    await Task.Run(() => File.Delete(sourcePath), token);
                                    SimpleFileLogger.Log($"[HandleApproved] Usunięto plik źródłowy: '{sourcePath}'.");
                                }
                            }
                            catch (Exception exDelete) { deleteErrors++; SimpleFileLogger.LogError($"[HandleApproved] Błąd usuwania '{sourcePath}'.", exDelete); }
                        }
                        else if (deleteSourceAfterCopy && string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                        {
                            oldPathForProfileUpdate = sourcePath;
                            newPathForProfileUpdate = targetPath;
                            SimpleFileLogger.Log($"[HandleApproved] Plik źródłowy '{sourcePath}' tożsamy z docelowym. Oznaczony jako obsłużony.");
                        }
                        if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldPathForProfileUpdate, newPathForProfileUpdate, move.TargetCategoryProfileName, token))
                            anyProfileActuallyModified = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exCopy) { copyErrors++; SimpleFileLogger.LogError($"[HandleApproved] Błąd przetwarzania '{sourcePath}' -> '{originalProposedTargetPathForLogging}'. Akcja: {actionType}.", exCopy); }
            }
            token.ThrowIfCancellationRequested();

            if (processedSourcePathsForThisBatch.Any())
            {
                int removedFromCacheCount = _lastModelSpecificSuggestions.RemoveAll(s => processedSourcePathsForThisBatch.Contains(s.SourceImage.FilePath));
                SimpleFileLogger.Log($"[HandleApproved] Usunięto {removedFromCacheCount} przetworzonych sugestii z cache'u.");
            }

            StatusMessage = $"Zakończono: {successfulMoves} pomyślnie, {skippedQuality} pom. (jakość), {skippedOther} pom. (inne), {copyErrors} bł. kopiowania, {deleteErrors} bł. usuwania.";
            if (successfulMoves > 0 || copyErrors > 0 || deleteErrors > 0)
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
                if (counter > 9999)
                {
                    newFileName = $"{baseName}_{Guid.NewGuid():N}{extension}";
                    finalPath = Path.Combine(targetDirectory, newFileName);
                    SimpleFileLogger.LogWarning($"GenerateUniqueTargetPath: Wygenerowano GUID po konfliktach: {finalPath}");
                    break;
                }
            }
            return finalPath;
        }

        private Task ExecuteRemoveModelTreeAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                bool profilesActuallyChanged = false;
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Wybierz modelkę."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();
                if (MessageBox.Show($"Czy na pewno chcesz usunąć modelkę '{modelVM.ModelName}' i jej profile?\nNIE usunie to plików graficznych.", "Potwierdź usunięcie", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    if (await _profileService.RemoveAllProfilesForModelAsync(modelVM.ModelName))
                    {
                        StatusMessage = $"Modelka '{modelVM.ModelName}' usunięta.";
                        if (_lastScannedModelNameForSuggestions == modelVM.ModelName) ClearModelSpecificSuggestionsCache();
                        if (SelectedProfile != null && _profileService.GetModelNameFromCategory(SelectedProfile.CategoryName) == modelVM.ModelName) SelectedProfile = null;
                        profilesActuallyChanged = true;
                    }
                    else { StatusMessage = $"Nie udało się usunąć modelki '{modelVM.ModelName}'."; profilesActuallyChanged = true; }
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
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Wybierz modelkę."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();
                int profilesMarkedForSplit = 0;
                await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.HasSplitSuggestion = false; });
                var characterProfilesForModel = modelVM.CharacterProfiles.ToList();
                foreach (var characterProfile in characterProfilesForModel)
                {
                    token.ThrowIfCancellationRequested();
                    const int minImagesForConsideration = 10; const int minImagesToSuggestSplit = 20;
                    if (characterProfile.SourceImagePaths == null || characterProfile.SourceImagePaths.Count < minImagesForConsideration) continue;
                    var validImagePathsCount = 0;
                    if (characterProfile.SourceImagePaths != null) foreach (var p in characterProfile.SourceImagePaths) if (File.Exists(p)) validImagePathsCount++;
                    token.ThrowIfCancellationRequested();
                    if (validImagePathsCount < minImagesForConsideration) continue;
                    bool shouldSuggestSplit = validImagePathsCount >= minImagesToSuggestSplit;
                    var uiCharacterProfile = modelVM.CharacterProfiles.FirstOrDefault(p => p.CategoryName == characterProfile.CategoryName);
                    if (uiCharacterProfile != null) { uiCharacterProfile.HasSplitSuggestion = shouldSuggestSplit; if (shouldSuggestSplit) profilesMarkedForSplit++; }
                    SimpleFileLogger.Log($"AnalyzeModelForSplitting: Profil '{characterProfile.CategoryName}', obrazy: {validImagePathsCount}, sugestia: {shouldSuggestSplit}.");
                }
                token.ThrowIfCancellationRequested();
                StatusMessage = $"Analiza podziału dla '{modelVM.ModelName}': {profilesMarkedForSplit} kandydatów.";
                if (profilesMarkedForSplit > 0) MessageBox.Show(StatusMessage, "Analiza Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
                else MessageBox.Show($"Brak profili dla '{modelVM.ModelName}' kwalifikujących się do podziału.", "Analiza Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "Analiza profili pod kątem podziału");

        private Task ExecuteOpenSplitProfileDialogAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is CategoryProfile originalCharacterProfile)) { StatusMessage = "Błąd: Wybierz profil postaci."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();
                bool dataChanged = false;
                var imagesInProfile = new List<ImageFileEntry>();
                if (originalCharacterProfile.SourceImagePaths != null)
                {
                    foreach (var path in originalCharacterProfile.SourceImagePaths)
                    {
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(path)) { var entry = await _imageMetadataService.ExtractMetadataAsync(path); if (entry != null) imagesInProfile.Add(entry); }
                    }
                }
                token.ThrowIfCancellationRequested();
                if (!imagesInProfile.Any()) { StatusMessage = $"Profil '{originalCharacterProfile.CategoryName}' pusty."; MessageBox.Show(StatusMessage, "Profil Pusty", MessageBoxButton.OK, MessageBoxImage.Warning); var uiProfile = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == originalCharacterProfile.CategoryName); if (uiProfile != null) uiProfile.HasSplitSuggestion = false; return; }
                var group1Images = imagesInProfile.Take(imagesInProfile.Count / 2).ToList();
                var group2Images = imagesInProfile.Skip(imagesInProfile.Count / 2).ToList();
                string modelName = _profileService.GetModelNameFromCategory(originalCharacterProfile.CategoryName);
                string baseCharacterName = _profileService.GetCharacterNameFromCategory(originalCharacterProfile.CategoryName);
                if (baseCharacterName.Equals("General", StringComparison.OrdinalIgnoreCase) && (originalCharacterProfile.CategoryName.Equals($"{modelName} - General", StringComparison.OrdinalIgnoreCase) || originalCharacterProfile.CategoryName.Equals(modelName, StringComparison.OrdinalIgnoreCase))) baseCharacterName = modelName;
                string suggestedName1 = $"{baseCharacterName} - Part 1"; string suggestedName2 = $"{baseCharacterName} - Part 2";
                bool? dialogResult = false; SplitProfileViewModel? splitVM = null;
                await Application.Current.Dispatcher.InvokeAsync(() => { splitVM = new SplitProfileViewModel(originalCharacterProfile, group1Images, group2Images, suggestedName1, suggestedName2); var splitWindow = new SplitProfileWindow { DataContext = splitVM, Owner = Application.Current.MainWindow }; splitWindow.SetViewModelCloseAction(splitVM); dialogResult = splitWindow.ShowDialog(); });
                token.ThrowIfCancellationRequested();
                if (dialogResult == true && splitVM != null)
                {
                    StatusMessage = $"Dzielenie '{originalCharacterProfile.CategoryName}'..."; SimpleFileLogger.LogHighLevelInfo($"SplitProfile: Potwierdzono dla '{originalCharacterProfile.CategoryName}'. Nowe: '{splitVM.NewProfile1Name}', '{splitVM.NewProfile2Name}'.");
                    string fullNewProfile1Name = $"{modelName} - {splitVM.NewProfile1Name}"; string fullNewProfile2Name = $"{modelName} - {splitVM.NewProfile2Name}";
                    var entriesForProfile1 = splitVM.Group1Images.Select(vmItem => vmItem.OriginalImageEntry).ToList(); var entriesForProfile2 = splitVM.Group2Images.Select(vmItem => vmItem.OriginalImageEntry).ToList();
                    string newProfile1Path = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(splitVM.NewProfile1Name)); string newProfile2Path = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(splitVM.NewProfile2Name));
                    Directory.CreateDirectory(newProfile1Path); Directory.CreateDirectory(newProfile2Path); SimpleFileLogger.Log($"SplitProfile: Utworzono foldery: '{newProfile1Path}', '{newProfile2Path}'.");
                    foreach (var entry in entriesForProfile1) { token.ThrowIfCancellationRequested(); string newFilePath = Path.Combine(newProfile1Path, entry.FileName); try { if (!File.Exists(newFilePath) && File.Exists(entry.FilePath)) File.Move(entry.FilePath, newFilePath); if (File.Exists(newFilePath)) entry.FilePath = newFilePath; } catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd przenoszenia '{entry.FilePath}' do '{newFilePath}'", ex); } }
                    foreach (var entry in entriesForProfile2) { token.ThrowIfCancellationRequested(); string newFilePath = Path.Combine(newProfile2Path, entry.FileName); try { if (!File.Exists(newFilePath) && File.Exists(entry.FilePath)) File.Move(entry.FilePath, newFilePath); if (File.Exists(newFilePath)) entry.FilePath = newFilePath; } catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd przenoszenia '{entry.FilePath}' do '{newFilePath}'", ex); } }
                    SimpleFileLogger.Log($"SplitProfile: Przeniesiono pliki.");
                    await _profileService.GenerateProfileAsync(fullNewProfile1Name, entriesForProfile1); SimpleFileLogger.LogHighLevelInfo($"SplitProfile: Profil '{fullNewProfile1Name}'."); token.ThrowIfCancellationRequested();
                    await _profileService.GenerateProfileAsync(fullNewProfile2Name, entriesForProfile2); SimpleFileLogger.LogHighLevelInfo($"SplitProfile: Profil '{fullNewProfile2Name}'."); token.ThrowIfCancellationRequested();
                    await _profileService.RemoveProfileAsync(originalCharacterProfile.CategoryName); SimpleFileLogger.LogHighLevelInfo($"SplitProfile: Usunięto stary profil '{originalCharacterProfile.CategoryName}'."); dataChanged = true;
                    StatusMessage = $"Profil '{originalCharacterProfile.CategoryName}' podzielony na '{fullNewProfile1Name}' i '{fullNewProfile2Name}'."; var uiProfile = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == originalCharacterProfile.CategoryName); if (uiProfile != null) uiProfile.HasSplitSuggestion = false;
                }
                else { StatusMessage = $"Podział '{originalCharacterProfile.CategoryName}' anulowany."; }
                if (dataChanged) { _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token); _isRefreshingProfilesPostMove = false; }
            }, "Otwieranie okna podziału profilu");

        private void ExecuteCancelCurrentOperation(object? parameter)
        {
            SimpleFileLogger.LogHighLevelInfo($"ExecuteCancelCurrentOperation. CTS: {_activeLongOperationCts != null}. Token: {_activeLongOperationCts?.Token.GetHashCode()}");
            if (_activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested)
            {
                _activeLongOperationCts.Cancel(); StatusMessage = "Anulowanie operacji..."; SimpleFileLogger.LogHighLevelInfo("Sygnał anulowania wysłany.");
            }
            else SimpleFileLogger.Log("Brak operacji do anulowania lub już anulowano.");
        }

        private Task ExecuteEnsureThumbnailsLoadedAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                var imagesToLoadThumbs = (parameter as IEnumerable<ImageFileEntry>)?.ToList() ?? ImageFiles.ToList();
                if (!imagesToLoadThumbs.Any()) { StatusMessage = "Brak obrazów do załadowania miniaturek."; return; }

                SimpleFileLogger.LogHighLevelInfo($"EnsureThumbnailsLoaded: Ładowanie dla {imagesToLoadThumbs.Count} obrazów. Token: {token.GetHashCode()}");

                var tasks = new List<Task>();
                using var thumbnailSemaphore = new SemaphoreSlim(10, 10);

                foreach (var entry in imagesToLoadThumbs)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Thumbnail == null && !entry.IsLoadingThumbnail)
                    {
                        tasks.Add(Task.Run(async () => {
                            await thumbnailSemaphore.WaitAsync(token);
                            try
                            {
                                if (token.IsCancellationRequested) return;
                                await entry.LoadThumbnailAsync();
                            }
                            finally
                            {
                                thumbnailSemaphore.Release();
                            }
                        }, token));
                    }
                }
                StatusMessage = $"Rozpoczęto ładowanie {tasks.Count} miniaturek...";
                await Task.WhenAll(tasks);
                token.ThrowIfCancellationRequested();
                int loadedCount = imagesToLoadThumbs.Count(img => img.Thumbnail != null);
                StatusMessage = $"Załadowano {loadedCount} z {imagesToLoadThumbs.Count} miniaturek (poproszono o {tasks.Count}).";
                SimpleFileLogger.LogHighLevelInfo(StatusMessage);
            }, "Ładowanie miniaturek");

        private Task ExecuteRemoveDuplicatesInModelAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                SimpleFileLogger.LogHighLevelInfo($"[ExecuteRemoveDuplicatesInModelAsync] Parameter: {parameter?.GetType().FullName ?? "null"}");
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nieprawidłowy parametr."; SimpleFileLogger.LogWarning(StatusMessage); return; }
                string modelName = modelVM.ModelName;
                if (MessageBox.Show($"Czy na pewno usunąć duplikaty dla '{modelName}' (pozostawiając najlepszą jakość)?\nSpowoduje to usunięcie plików z dysku.", "Potwierdź", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                { StatusMessage = "Usuwanie duplikatów anulowane."; SimpleFileLogger.LogHighLevelInfo($"[RemoveDuplicatesInModel] Anulowano dla: {modelName}."); return; }

                StatusMessage = $"Usuwanie duplikatów dla: {modelName}..."; SimpleFileLogger.LogHighLevelInfo($"[RemoveDuplicatesInModel] Rozpoczęto dla: {modelName}. Token: {token.GetHashCode()}");
                long totalDuplicatesRemovedSystemWide = 0;
                bool anyProfileDataActuallyChangedDuringThisOperation = false;
                var characterProfilesSnapshot = modelVM.CharacterProfiles.ToList();

                foreach (var characterProfile in characterProfilesSnapshot)
                {
                    token.ThrowIfCancellationRequested();
                    if (characterProfile.SourceImagePaths == null || characterProfile.SourceImagePaths.Count < 2) continue;
                    StatusMessage = $"Przetwarzanie: {characterProfile.CategoryName}..."; SimpleFileLogger.Log($"[RemoveDuplicatesInModel] Profil: {characterProfile.CategoryName}");
                    var imageEntriesInProfile = new ConcurrentBag<ImageFileEntry>();
                    bool profileHadMissingFiles = false;

                    var entryLoadingTasks = characterProfile.SourceImagePaths.Select(async imagePath => {
                        token.ThrowIfCancellationRequested();
                        if (!File.Exists(imagePath)) { SimpleFileLogger.LogWarning($"[RDIM] Plik '{imagePath}' z '{characterProfile.CategoryName}' nie istnieje."); lock (_profileChangeLock) { profileHadMissingFiles = true; } return; }
                        var entry = await _imageMetadataService.ExtractMetadataAsync(imagePath);
                        if (entry != null)
                        {
                            await _embeddingSemaphore.WaitAsync(token); try { if (token.IsCancellationRequested) return; entry.FeatureVector = await _profileService.GetImageEmbeddingAsync(entry); } finally { _embeddingSemaphore.Release(); }
                            if (entry.FeatureVector != null) imageEntriesInProfile.Add(entry); else SimpleFileLogger.LogWarning($"[RDIM] Brak embeddingu dla '{entry.FilePath}'.");
                        }
                    }).ToList();
                    await Task.WhenAll(entryLoadingTasks);
                    token.ThrowIfCancellationRequested();

                    var validImageEntriesList = imageEntriesInProfile.ToList();
                    if (validImageEntriesList.Count < 2) { if (profileHadMissingFiles) { SimpleFileLogger.Log($"[RDIM] '{characterProfile.CategoryName}' wymaga aktualizacji (brakujące pliki)."); await _profileService.GenerateProfileAsync(characterProfile.CategoryName, validImageEntriesList); lock (_profileChangeLock) { anyProfileDataActuallyChangedDuringThisOperation = true; } } continue; }

                    var filesToRemovePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var processedInThisProfileForDuplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < validImageEntriesList.Count; i++)
                    {
                        token.ThrowIfCancellationRequested(); var currentImage = validImageEntriesList[i];
                        if (filesToRemovePaths.Contains(currentImage.FilePath) || processedInThisProfileForDuplicates.Contains(currentImage.FilePath)) continue;
                        var duplicateGroupForCurrentImage = new List<ImageFileEntry> { currentImage };
                        for (int j = i + 1; j < validImageEntriesList.Count; j++)
                        { token.ThrowIfCancellationRequested(); var otherImage = validImageEntriesList[j]; if (filesToRemovePaths.Contains(otherImage.FilePath) || processedInThisProfileForDuplicates.Contains(otherImage.FilePath)) continue; if (currentImage.FeatureVector != null && otherImage.FeatureVector != null) { double similarity = Utils.MathUtils.CalculateCosineSimilarity(currentImage.FeatureVector, otherImage.FeatureVector); if (similarity >= DUPLICATE_SIMILARITY_THRESHOLD) duplicateGroupForCurrentImage.Add(otherImage); } }
                        if (duplicateGroupForCurrentImage.Count > 1) { ImageFileEntry bestImageInGroup = duplicateGroupForCurrentImage.First(); foreach (var imageInGroup in duplicateGroupForCurrentImage.Skip(1)) if (IsImageBetter(imageInGroup, bestImageInGroup)) bestImageInGroup = imageInGroup; foreach (var imageInGroup in duplicateGroupForCurrentImage) { if (!imageInGroup.FilePath.Equals(bestImageInGroup.FilePath, StringComparison.OrdinalIgnoreCase)) { filesToRemovePaths.Add(imageInGroup.FilePath); SimpleFileLogger.Log($"[RDIM] Oznaczono duplikat: '{imageInGroup.FilePath}' (lepszy: '{bestImageInGroup.FilePath}') w '{characterProfile.CategoryName}'."); } processedInThisProfileForDuplicates.Add(imageInGroup.FilePath); } } else processedInThisProfileForDuplicates.Add(currentImage.FilePath);
                    }
                    token.ThrowIfCancellationRequested();
                    if (filesToRemovePaths.Any()) { SimpleFileLogger.LogHighLevelInfo($"[RDIM] Profil '{characterProfile.CategoryName}': {filesToRemovePaths.Count} duplikatów do usunięcia."); long removedThisProfileCount = 0; foreach (var pathToRemoveLoopVar in filesToRemovePaths) { token.ThrowIfCancellationRequested(); try { if (File.Exists(pathToRemoveLoopVar)) { File.Delete(pathToRemoveLoopVar); SimpleFileLogger.LogHighLevelInfo($"[RDIM] Usunięto: {pathToRemoveLoopVar}"); Interlocked.Increment(ref totalDuplicatesRemovedSystemWide); removedThisProfileCount++; } } catch (Exception ex) { SimpleFileLogger.LogError($"[RDIM] Błąd usuwania '{pathToRemoveLoopVar}'.", ex); } } var keptImageEntries = validImageEntriesList.Where(e => !filesToRemovePaths.Contains(e.FilePath)).ToList(); await _profileService.GenerateProfileAsync(characterProfile.CategoryName, keptImageEntries); lock (_profileChangeLock) { anyProfileDataActuallyChangedDuringThisOperation = true; } SimpleFileLogger.LogHighLevelInfo($"[RDIM] Profil '{characterProfile.CategoryName}' zaktualizowany. Usunięto {removedThisProfileCount}. Pozostało: {keptImageEntries.Count}."); } else if (profileHadMissingFiles) { SimpleFileLogger.Log($"[RDIM] '{characterProfile.CategoryName}' bez duplikatów, ale z brakującymi plikami."); var validEntries = validImageEntriesList.Where(e => File.Exists(e.FilePath)).ToList(); await _profileService.GenerateProfileAsync(characterProfile.CategoryName, validEntries); lock (_profileChangeLock) { anyProfileDataActuallyChangedDuringThisOperation = true; } } else SimpleFileLogger.Log($"[RDIM] Profil '{characterProfile.CategoryName}': Brak duplikatów.");
                }
                token.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref totalDuplicatesRemovedSystemWide) > 0 || anyProfileDataActuallyChangedDuringThisOperation) { StatusMessage = $"Zakończono dla '{modelName}'. Usunięto: {Interlocked.Read(ref totalDuplicatesRemovedSystemWide)}. Odświeżanie..."; _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token); _isRefreshingProfilesPostMove = false; MessageBox.Show($"Usunięto {Interlocked.Read(ref totalDuplicatesRemovedSystemWide)} duplikatów dla '{modelName}'.", "Zakończono", MessageBoxButton.OK, MessageBoxImage.Information); } else { StatusMessage = $"Brak duplikatów dla '{modelName}'."; MessageBox.Show(StatusMessage, "Zakończono", MessageBoxButton.OK, MessageBoxImage.Information); }
            }, "Usuwanie duplikatów (wielowątkowe)");

        private Task ExecuteApplyAllMatchesForModelAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                SimpleFileLogger.LogHighLevelInfo($"[ExecuteApplyAllMatchesForModelAsync] Parameter: {parameter?.GetType().FullName ?? "null"}");
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nieprawidłowy parametr."; MessageBox.Show(StatusMessage, "Błąd", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                string modelName = modelVM.ModelName;
                bool hasRelevantCachedSuggestions = (_lastScannedModelNameForSuggestions == modelName && _lastModelSpecificSuggestions.Any(m => _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelName)) || (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) && _lastModelSpecificSuggestions.Any(m => _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelName));
                if (!hasRelevantCachedSuggestions) { StatusMessage = $"Brak sugestii dla '{modelName}'. Uruchom skanowanie."; MessageBox.Show(StatusMessage, "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
                var movesToApply = _lastModelSpecificSuggestions.Where(m => m.Similarity >= SuggestionSimilarityThreshold && _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelName).ToList();
                if (!movesToApply.Any()) { StatusMessage = $"Brak sugestii (próg {SuggestionSimilarityThreshold:F2}) dla '{modelName}'."; MessageBox.Show(StatusMessage, "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
                if (MessageBox.Show($"Zastosować {movesToApply.Count} dopasowań dla '{modelName}'?\nSpowoduje to przeniesienie/usunięcie plików.", "Potwierdź", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                { StatusMessage = "Anulowano."; SimpleFileLogger.LogHighLevelInfo($"[ApplyAllMatchesForModel] Anulowano dla: {modelName}."); return; }
                StatusMessage = $"Stosowanie {movesToApply.Count} dopasowań dla: {modelName}..."; SimpleFileLogger.LogHighLevelInfo($"[ApplyAllMatchesForModel] Rozpoczęto dla: {modelName}. Ruchy: {movesToApply.Count}. Token: {token.GetHashCode()}");
                bool anyProfileDataActuallyChanged = await InternalHandleApprovedMovesAsync(new List<Models.ProposedMove>(movesToApply), modelVM, null, token);
                token.ThrowIfCancellationRequested();
                if (anyProfileDataActuallyChanged) { StatusMessage = $"Zakończono dla '{modelName}'. Odświeżanie..."; _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token); _isRefreshingProfilesPostMove = false; } else StatusMessage = $"Zakończono dla '{modelName}'. Brak zmian w profilach.";
                RefreshPendingSuggestionCountsFromCache();
                MessageBox.Show($"Zastosowano {movesToApply.Count} dopasowań dla '{modelName}'.", "Zakończono", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "Automatyczne stosowanie dopasowań (wielowątkowe)");

        private class ImageFileEntryPathComparer : IEqualityComparer<ImageFileEntry>
        {
            public bool Equals(ImageFileEntry? x, ImageFileEntry? y) { if (ReferenceEquals(x, y)) return true; if (x is null || y is null) return false; return x.FilePath.Equals(y.FilePath, StringComparison.OrdinalIgnoreCase); }
            public int GetHashCode(ImageFileEntry obj) { return obj.FilePath?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0; }
        }
    }
}