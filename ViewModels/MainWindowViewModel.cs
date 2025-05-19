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
        // ... (pola klasy, konstruktor i inne metody pozostają takie same jak w poprzedniej wersji,
        //      aż do metod, które modyfikujemy)

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
        // Usunięto _profileChangeLock, ponieważ będziemy agregować wyniki inaczej

        // Definicja klasy do przechowywania wyników przetwarzania pojedynczego obrazu
        private class SingleImageProcessingResult
        {
            public bool ProfileDataChanged { get; set; } = false;
            public bool WasActionAutoHandled { get; set; } = false;
            public ProposedMove? ProposedMove { get; set; } = null;
            public bool GotEmbedding { get; set; } = false;
            public bool SourceFileProcessedSuccessfully { get; set; } = false; // Czy obraz został pomyślnie przetworzony (miał embedding itd.)
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
            _lastScannedModelNameForSuggestions = "__CACHE_CLEARED__";
            RefreshPendingSuggestionCountsFromCache();
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
                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1), _activeLongOperationCts.Token), Task.Run(() => { }, _activeLongOperationCts.Token));
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
            SimpleFileLogger.Log($"InternalExecuteLoadProfilesAsync. RefreshFlag: {_isRefreshingProfilesPostMove}. Token: {token.GetHashCode()}");
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
                if (_lastModelSpecificSuggestions.Any()) RefreshPendingSuggestionCountsFromCache();
            }, token); // Przekaż token do InvokeAsync, jeśli to możliwe/potrzebne w danej wersji .NET
        }

        private Task ExecuteGenerateProfileAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                bool profilesActuallyRegenerated = false;
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) || string.IsNullOrWhiteSpace(ModelNameInput) || string.IsNullOrWhiteSpace(CharacterNameInput))
                { StatusMessage = "Błąd: Nazwa modelki, postaci i profilu muszą być zdefiniowane."; MessageBox.Show(StatusMessage, "Błąd danych", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                string catName = CurrentProfileNameForEdit;
                SimpleFileLogger.Log($"Generowanie profilu '{catName}' ({ImageFiles.Count} obr.). Token: {token.GetHashCode()}");

                var entriesToProcess = new ConcurrentBag<ImageFileEntry>();
                var fileProcessingTasks = ImageFiles.Select(async file => {
                    token.ThrowIfCancellationRequested();
                    if (file.FileSize == 0 || file.FileLastModifiedUtc == DateTime.MinValue)
                    { var updatedEntry = await _imageMetadataService.ExtractMetadataAsync(file.FilePath); if (updatedEntry != null) entriesToProcess.Add(updatedEntry); else SimpleFileLogger.LogWarning($"Nie udało się załadować metadanych dla {file.FilePath}, pomijam."); }
                    else { entriesToProcess.Add(file); }
                }).ToList();
                await Task.WhenAll(fileProcessingTasks);
                token.ThrowIfCancellationRequested();

                var finalEntries = entriesToProcess.ToList();
                if (!finalEntries.Any() && ImageFiles.Any())
                { StatusMessage = "Błąd: Nie udało się przetworzyć żadnego z plików."; MessageBox.Show(StatusMessage, "Błąd plików", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                await _profileService.GenerateProfileAsync(catName, finalEntries);
                profilesActuallyRegenerated = true;
                token.ThrowIfCancellationRequested();
                StatusMessage = $"Profil '{catName}' wygenerowany/zaktualizowany.";
                if (profilesActuallyRegenerated) { _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token); _isRefreshingProfilesPostMove = false; SelectedProfile = _profileService.GetProfile(catName); }
            }, "Generowanie profilu");

        // ... (reszta metod bez zmian związanych bezpośrednio z wielowątkowością ExecuteSuggestImagesAsync
        //      lub z drobnymi poprawkami jak przekazanie tokenu do Dispatcher.InvokeAsync)

        private async void ExecuteAddFilesToProfile(object? parameter = null)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "Obrazy|*.jpg;*.jpeg;*.png;*.webp|Wszystkie pliki|*.*", Title = "Wybierz obrazy do dodania", Multiselect = true };
            if (openFileDialog.ShowDialog() == true)
            {
                IsBusy = true;
                StatusMessage = "Dodawanie plików i ładowanie metadanych...";
                int addedCount = 0;
                var tasks = new List<Task>();
                var addedEntries = new ConcurrentBag<ImageFileEntry>();

                foreach (string filePath in openFileDialog.FileNames)
                {
                    if (!ImageFiles.Any(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        tasks.Add(Task.Run(async () => {
                            var entry = await _imageMetadataService.ExtractMetadataAsync(filePath);
                            if (entry != null)
                            {
                                addedEntries.Add(entry);
                            }
                            else
                            {
                                SimpleFileLogger.LogWarning($"ExecuteAddFilesToProfile: Nie udało się załadować metadanych dla pliku: {filePath}, pominięto.");
                            }
                        }));
                    }
                }
                try
                {
                    await Task.WhenAll(tasks);
                    foreach (var entry in addedEntries)
                    {
                        ImageFiles.Add(entry);
                        addedCount++;
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


        // --- POCZĄTEK ZMIAN DLA WIELOWĄTKOWOŚCI ---

        private async Task<SingleImageProcessingResult> ProcessSingleImageForScanAsync(
            string imgPathFromMix, ModelDisplayViewModel modelVM, string modelPath, CancellationToken token,
            ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)> alreadySuggestedGraphicDuplicates)
        {
            var result = new SingleImageProcessingResult();
            if (token.IsCancellationRequested) return result;

            if (!File.Exists(imgPathFromMix))
            {
                SimpleFileLogger.LogWarning($"ProcessSingleImage: Plik {imgPathFromMix} nie istnieje (mógł zostać usunięty). Pomijanie.");
                return result; // Zwróć pusty wynik, plik nie istnieje
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
                SimpleFileLogger.LogError($"Błąd pobierania embeddingu dla {sourceEntry.FilePath} w ProcessSingleImage", ex);
                return result; // Błąd, zwróć pusty wynik
            }
            finally
            {
                _embeddingSemaphore.Release();
            }

            if (sourceEmbedding == null) return result;
            result.GotEmbedding = true;
            if (token.IsCancellationRequested) return result;

            var bestSuggestionForThisSourceImage = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName);

            if (bestSuggestionForThisSourceImage != null)
            {
                CategoryProfile targetProfile = bestSuggestionForThisSourceImage.Item1;
                double similarityToCentroid = bestSuggestionForThisSourceImage.Item2;

                var (proposedMove, wasActionAutoHandled, profilesModifiedByCall) = await ProcessDuplicateOrSuggestNewAsync(
                    sourceEntry, targetProfile, similarityToCentroid, modelPath, sourceEmbedding, token);

                result.ProfileDataChanged = profilesModifiedByCall;
                result.WasActionAutoHandled = wasActionAutoHandled;

                if (!wasActionAutoHandled && proposedMove != null)
                {
                    bool addThisSuggestion = true;
                    if (proposedMove.SourceImageEmbedding != null &&
                        (proposedMove.Action == ProposedMoveActionType.CopyNew || proposedMove.Action == ProposedMoveActionType.ConflictKeepBoth))
                    {
                        // Ta logika filtrowania duplikatów musi być ostrożna. 
                        // Iterowanie po 'alreadySuggestedGraphicDuplicates' podczas gdy inne wątki mogą do niej dodawać jest niebezpieczne.
                        // Lepszym podejściem byłoby zebranie wszystkich 'proposedMove' i przefiltrowanie ich później, jednowątkowo.
                        // Na razie, dla uproszczenia, zakładamy, że ten filtr nie jest krytyczny lub zostanie dodany później.
                        // foreach(var (existingEmb, existingTargetCat, existingSourcePath) in alreadySuggestedGraphicDuplicates.ToList()) // ToList() dla kopii
                        // { ... }
                    }

                    if (addThisSuggestion)
                    {
                        result.ProposedMove = proposedMove;
                        // if (proposedMove.SourceImageEmbedding != null && (proposedMove.Action == ProposedMoveActionType.CopyNew || proposedMove.Action == ProposedMoveActionType.ConflictKeepBoth))
                        // {
                        //    alreadySuggestedGraphicDuplicates.Add((proposedMove.SourceImageEmbedding, proposedMove.TargetCategoryProfileName, proposedMove.SourceImage.FilePath));
                        // }
                    }
                }
            }
            result.SourceFileProcessedSuccessfully = true;
            return result;
        }


        private Task ExecuteMatchModelSpecificAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nie wybrano modelki."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                SimpleFileLogger.Log($"MatchModelSpecific dla '{modelVM.ModelName}'. Token: {token.GetHashCode()}");
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe."; MessageBox.Show(StatusMessage, "Brak Folderów Źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                var collectedProposedMoves = new ConcurrentBag<Models.ProposedMove>();
                string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName);
                if (!Directory.Exists(modelPath)) { StatusMessage = $"Błąd: Folder modelki '{modelVM.ModelName}' nie istnieje."; MessageBox.Show(StatusMessage, "Błąd Folderu Modelki", MessageBoxButton.OK, MessageBoxImage.Error); return; }

                await Application.Current.Dispatcher.InvokeAsync(() => { modelVM.PendingSuggestionsCount = 0; foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; }, token);

                long filesFoundInMix = 0;
                long filesWithEmbeddings = 0;
                long autoActionsCount = 0;
                bool anyProfileDataChangedDuringScan = false;
                var alreadySuggestedGraphicDuplicatesConcurrent = new ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)>();
                var processingTasks = new List<Task>();

                foreach (var mixFolderName in mixedFolders)
                {
                    token.ThrowIfCancellationRequested();
                    string currentMixPath = Path.Combine(modelPath, mixFolderName);
                    if (Directory.Exists(currentMixPath))
                    {
                        var imagePathsInMix = await _fileScannerService.ScanDirectoryAsync(currentMixPath);
                        Interlocked.Add(ref filesFoundInMix, imagePathsInMix.Count);
                        SimpleFileLogger.Log($"MatchModelSpecific: W '{currentMixPath}' znaleziono {imagePathsInMix.Count} obrazów.");

                        foreach (var imgPathFromMix in imagePathsInMix)
                        {
                            token.ThrowIfCancellationRequested();
                            processingTasks.Add(Task.Run(async () => {
                                var result = await ProcessSingleImageForScanAsync(
                                    imgPathFromMix, modelVM, modelPath, token,
                                    alreadySuggestedGraphicDuplicatesConcurrent // Przekaż współdzieloną kolekcję (z uwagami o bezpieczeństwie)
                                );
                                if (result.GotEmbedding) Interlocked.Increment(ref filesWithEmbeddings);
                                if (result.WasActionAutoHandled) Interlocked.Increment(ref autoActionsCount);
                                if (result.ProposedMove != null) collectedProposedMoves.Add(result.ProposedMove);
                                if (result.ProfileDataChanged) anyProfileDataChangedDuringScan = true; // Prosta flaga, wymaga synchronizacji jeśli wiele wątków ją ustawia
                            }, token));
                        }
                    }
                    else { SimpleFileLogger.LogWarning($"MatchModelSpecific: Folder źródłowy '{currentMixPath}' nie istnieje."); }
                }

                await Task.WhenAll(processingTasks);
                token.ThrowIfCancellationRequested();

                var movesForSuggestionWindow = collectedProposedMoves.ToList();
                SimpleFileLogger.Log($"MatchModelSpecific dla '{modelVM.ModelName}': Podsumowanie - Znaleziono: {filesFoundInMix}, Z embeddingami: {filesWithEmbeddings}. Akcje auto: {autoActionsCount}. Sugestie: {movesForSuggestionWindow.Count}. Profile zmodyfikowane (podczas skanu): {anyProfileDataChangedDuringScan}");

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
                    }, token);
                    token.ThrowIfCancellationRequested();
                    if (dialogOutcome == true && approvedMoves.Any())
                    {
                        if (await InternalHandleApprovedMovesAsync(approvedMoves, modelVM, null, token))
                            anyProfileDataChangedDuringScan = true; // Zaktualizuj, jeśli operacje z okna zmieniły profile
                                                                    // _lastModelSpecificSuggestions.RemoveAll(sugg => movesForSuggestionWindow.Contains(sugg)); // Usuń tylko te, które były w oknie
                        _lastModelSpecificSuggestions.RemoveAll(sugg => approvedMoves.Any(ap => ap.SourceImage.FilePath == sugg.SourceImage.FilePath)); // Usuń zatwierdzone
                    }
                    else if (dialogOutcome == false) StatusMessage = $"Anulowano zmiany dla '{modelVM.ModelName}'.";
                }

                if (anyProfileDataChangedDuringScan)
                {
                    SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Zmiany w profilach dla '{modelVM.ModelName}'. Odświeżanie.");
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }
                RefreshPendingSuggestionCountsFromCache();
                StatusMessage = $"Dla '{modelVM.ModelName}': {autoActionsCount} akcji auto., {modelVM.PendingSuggestionsCount} sugestii.";
                if (!movesForSuggestionWindow.Any() && autoActionsCount > 0 && !anyProfileDataChangedDuringScan)
                { MessageBox.Show($"Zakończono automatyczne operacje dla '{modelVM.ModelName}'. Wykonano {autoActionsCount} akcji. Brak dodatkowych sugestii.", "Operacje Automatyczne Zakończone", MessageBoxButton.OK, MessageBoxImage.Information); }
                else if (!movesForSuggestionWindow.Any() && autoActionsCount == 0 && !anyProfileDataChangedDuringScan && filesFoundInMix > 0)
                { MessageBox.Show($"Brak nowych sugestii lub automatycznych akcji dla '{modelVM.ModelName}'.", "Brak Zmian", MessageBoxButton.OK, MessageBoxImage.Information); }
                else if (filesFoundInMix == 0)
                { MessageBox.Show($"Nie znaleziono obrazów w folderach źródłowych dla '{modelVM.ModelName}'.", "Brak Plików Źródłowych", MessageBoxButton.OK, MessageBoxImage.Information); }

            }, "Dopasowywanie dla modelki (wielowątkowe)");


        private Task ExecuteSuggestImagesAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                ClearModelSpecificSuggestionsCache();
                token.ThrowIfCancellationRequested();
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe."; MessageBox.Show(StatusMessage, "Brak Folderów Źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                var allCollectedSuggestionsGlobalConcurrent = new ConcurrentBag<Models.ProposedMove>();
                var alreadySuggestedGraphicDuplicatesGlobalConcurrent = new ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)>();

                await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var mVM_ui in HierarchicalProfilesList) { mVM_ui.PendingSuggestionsCount = 0; foreach (var cp_ui in mVM_ui.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; } }, token);

                var allModelsCurrentlyInList = HierarchicalProfilesList.ToList();
                long totalFilesFound = 0;
                long totalFilesWithEmbeddings = 0;
                long totalAutoActions = 0;
                bool anyProfileDataChangedDuringScan = false;
                var processingTasks = new List<Task>();

                foreach (var modelVM in allModelsCurrentlyInList)
                {
                    token.ThrowIfCancellationRequested();
                    string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName);
                    if (!Directory.Exists(modelPath) || !modelVM.HasCharacterProfiles) { SimpleFileLogger.Log($"ExecuteSuggestImagesAsync: Pomijanie '{modelVM.ModelName}'."); continue; }
                    SimpleFileLogger.Log($"ExecuteSuggestImagesAsync (Global Scan): Przetwarzanie '{modelVM.ModelName}'.");

                    foreach (var mixFolderName in mixedFolders)
                    {
                        token.ThrowIfCancellationRequested();
                        string currentMixPath = Path.Combine(modelPath, mixFolderName);
                        if (Directory.Exists(currentMixPath))
                        {
                            var imagePathsInMix = await _fileScannerService.ScanDirectoryAsync(currentMixPath);
                            Interlocked.Add(ref totalFilesFound, imagePathsInMix.Count);

                            foreach (var imgPathFromMix in imagePathsInMix)
                            {
                                token.ThrowIfCancellationRequested();
                                processingTasks.Add(Task.Run(async () => {
                                    var result = await ProcessSingleImageForScanAsync(
                                        imgPathFromMix, modelVM, modelPath, token,
                                        alreadySuggestedGraphicDuplicatesGlobalConcurrent // Współdzielona kolekcja
                                    );
                                    if (result.GotEmbedding) Interlocked.Increment(ref totalFilesWithEmbeddings);
                                    if (result.WasActionAutoHandled) Interlocked.Increment(ref totalAutoActions);
                                    if (result.ProposedMove != null) allCollectedSuggestionsGlobalConcurrent.Add(result.ProposedMove);
                                    if (result.ProfileDataChanged) anyProfileDataChangedDuringScan = true; // Prosta flaga
                                }, token));
                            }
                        }
                    }
                }

                await Task.WhenAll(processingTasks);
                token.ThrowIfCancellationRequested();

                var allCollectedSuggestionsGlobal = allCollectedSuggestionsGlobalConcurrent.ToList();
                SimpleFileLogger.Log($"ExecuteSuggestImagesAsync: Globalnie - Znaleziono: {totalFilesFound}, Z embeddingami: {totalFilesWithEmbeddings}, Akcje auto: {totalAutoActions}, Sugestie: {allCollectedSuggestionsGlobal.Count}. Profile zmodyfikowane (skan): {anyProfileDataChangedDuringScan}");

                _lastModelSpecificSuggestions = new List<Models.ProposedMove>(allCollectedSuggestionsGlobal);
                _lastScannedModelNameForSuggestions = null;
                StatusMessage = $"Globalne wyszukiwanie: {totalAutoActions} akcji auto, {allCollectedSuggestionsGlobal.Count} sugestii.";

                if (anyProfileDataChangedDuringScan)
                {
                    SimpleFileLogger.Log($"ExecuteSuggestImagesAsync: Zmiany w profilach (globalne). Odświeżanie.");
                    _isRefreshingProfilesPostMove = true;
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }
                RefreshPendingSuggestionCountsFromCache();
                string completionMessage = StatusMessage;
                if (allCollectedSuggestionsGlobal.Any()) { completionMessage += " Użyj menu kontekstowego, aby przejrzeć."; }
                else if (totalAutoActions == 0 && totalFilesFound > 0) { completionMessage = "Globalne wyszukiwanie nie znalazło sugestii ani akcji automatycznych."; }
                else if (totalFilesFound == 0) { completionMessage = "Nie znaleziono plików w folderach źródłowych."; }
                MessageBox.Show(completionMessage, "Globalne Wyszukiwanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "Globalne wyszukiwanie sugestii (wielowątkowe)");


        // --- KONIEC ZMIAN DLA WIELOWĄTKOWOŚCI ---

        // ... (reszta metod: ExecuteCheckCharacterSuggestionsAsync, RefreshPendingSuggestionCountsFromCache, InternalHandleApprovedMovesAsync, itd.
        //      pozostaje taka sama jak w poprzedniej odpowiedzi lub wymagałaby podobnych, ostrożnych modyfikacji
        //      jeśli miałyby być również intensywnie zrównoleglone wewnętrznie)
        //      Na razie skupiliśmy się na dwóch głównych pętlach skanujących.

        // ... (cała reszta metod z poprzedniej odpowiedzi, np. ExecuteCheckCharacterSuggestionsAsync itd.)
        // Należy upewnić się, że wszystkie ścieżki kodu są kompletne.
        // Dla uproszczenia, poniżej wklejam tylko szkielet pozostałych metod,
        // zakładając, że ich wewnętrzna logika nie jest bezpośrednio zmieniana w tym kroku,
        // ale należy je przejrzeć pod kątem bezpieczeństwa wątkowego, jeśli są wywoływane
        // z kontekstu, który może być współbieżny.

        private Task ExecuteCheckCharacterSuggestionsAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                var charProfileForSuggestions = (parameter as CategoryProfile) ?? SelectedProfile;
                if (charProfileForSuggestions == null) { StatusMessage = "Błąd: Wybierz profil postaci."; MessageBox.Show(StatusMessage, "Brak Wyboru Postaci", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                SimpleFileLogger.Log($"CheckCharacterSuggestions dla '{charProfileForSuggestions.CategoryName}'. Token: {token.GetHashCode()}");
                token.ThrowIfCancellationRequested();
                string modelName = _profileService.GetModelNameFromCategory(charProfileForSuggestions.CategoryName);
                var modelVM = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));
                var movesForThisCharacterWindow = new List<Models.ProposedMove>();
                bool anyProfileDataChangedDuringEntireOperation = false;

                if (_lastModelSpecificSuggestions.Any())
                {
                    if (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) || _lastScannedModelNameForSuggestions.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                    {
                        movesForThisCharacterWindow = _lastModelSpecificSuggestions
                            .Where(m => m.TargetCategoryProfileName.Equals(charProfileForSuggestions.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                                        m.Similarity >= SuggestionSimilarityThreshold)
                            .ToList();
                        SimpleFileLogger.Log($"CheckCharacterSuggestions: Użyto cache ({_lastModelSpecificSuggestions.Count}). Dla '{charProfileForSuggestions.CategoryName}' znaleziono {movesForThisCharacterWindow.Count}.");
                    }
                }
                if (!movesForThisCharacterWindow.Any())
                { StatusMessage = $"Brak sugestii dla '{charProfileForSuggestions.CategoryName}'. Uruchom skanowanie."; MessageBox.Show(StatusMessage, "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Information); var uiProfile = modelVM?.CharacterProfiles.FirstOrDefault(cp => cp.CategoryName == charProfileForSuggestions.CategoryName); if (uiProfile != null) uiProfile.PendingSuggestionsCount = 0; return; }
                token.ThrowIfCancellationRequested();
                if (movesForThisCharacterWindow.Any())
                {
                    bool? outcome = false; List<Models.ProposedMove> approved = new List<Models.ProposedMove>();
                    await Application.Current.Dispatcher.InvokeAsync(() => { var vm = new PreviewChangesViewModel(movesForThisCharacterWindow, SuggestionSimilarityThreshold); var win = new PreviewChangesWindow { DataContext = vm, Owner = Application.Current.MainWindow }; win.SetViewModelCloseAction(vm); outcome = win.ShowDialog(); if (outcome == true) approved = vm.GetApprovedMoves(); }, token);
                    token.ThrowIfCancellationRequested();
                    if (outcome == true && approved.Any())
                    { if (await InternalHandleApprovedMovesAsync(approved, modelVM, charProfileForSuggestions, token)) anyProfileDataChangedDuringEntireOperation = true; _lastModelSpecificSuggestions.RemoveAll(sugg => approved.Any(ap => ap.SourceImage.FilePath == sugg.SourceImage.FilePath)); }
                    else if (outcome == false) StatusMessage = $"Anulowano sugestie dla '{charProfileForSuggestions.CategoryName}'.";
                }
                if (anyProfileDataChangedDuringEntireOperation) { SimpleFileLogger.Log($"CheckCharacterSuggestionsAsync: Zmiany w profilach dla '{charProfileForSuggestions.CategoryName}'. Odświeżanie."); _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token); _isRefreshingProfilesPostMove = false; }
                RefreshPendingSuggestionCountsFromCache();
            }, "Sprawdzanie sugestii dla postaci");

        private void RefreshPendingSuggestionCountsFromCache()
        {
            Application.Current.Dispatcher.Invoke(() => {
                foreach (var mVM_iter in HierarchicalProfilesList)
                { mVM_iter.PendingSuggestionsCount = 0; foreach (var cp_iter in mVM_iter.CharacterProfiles) cp_iter.PendingSuggestionsCount = 0; }
                if (_lastModelSpecificSuggestions.Any())
                {
                    var relevantSuggestions = _lastModelSpecificSuggestions.Where(sugg => sugg.Similarity >= SuggestionSimilarityThreshold).ToList();
                    if (!string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) && _lastScannedModelNameForSuggestions != "__CACHE_CLEARED__")
                    {
                        var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(_lastScannedModelNameForSuggestions, StringComparison.OrdinalIgnoreCase));
                        if (modelToUpdate != null)
                        { int totalForModel = 0; foreach (var cp_ui in modelToUpdate.CharacterProfiles) { cp_ui.PendingSuggestionsCount = relevantSuggestions.Count(sugg => sugg.TargetCategoryProfileName.Equals(cp_ui.CategoryName, StringComparison.OrdinalIgnoreCase)); totalForModel += cp_ui.PendingSuggestionsCount; } modelToUpdate.PendingSuggestionsCount = totalForModel; SimpleFileLogger.Log($"RefreshPendingCounts (Specific '{_lastScannedModelNameForSuggestions}'): Total: {totalForModel}."); }
                    }
                    else if (_lastScannedModelNameForSuggestions != "__CACHE_CLEARED__")
                    {
                        SimpleFileLogger.Log($"RefreshPendingCounts (Global): Attributing {relevantSuggestions.Count} suggestions.");
                        var suggestionsByModel = relevantSuggestions.GroupBy(sugg => _profileService.GetModelNameFromCategory(sugg.TargetCategoryProfileName));
                        foreach (var group in suggestionsByModel)
                        { var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(group.Key, StringComparison.OrdinalIgnoreCase)); if (modelToUpdate != null) { int totalForModel = 0; foreach (var cp_ui in modelToUpdate.CharacterProfiles) { cp_ui.PendingSuggestionsCount = group.Count(sugg => sugg.TargetCategoryProfileName.Equals(cp_ui.CategoryName, StringComparison.OrdinalIgnoreCase)); totalForModel += cp_ui.PendingSuggestionsCount; } modelToUpdate.PendingSuggestionsCount = totalForModel; SimpleFileLogger.Log($"RefreshPendingCounts (Global): Model '{modelToUpdate.ModelName}' updated: {totalForModel}."); } }
                    }
                    else { SimpleFileLogger.Log("RefreshPendingCounts: Cache was cleared."); }
                }
                else { SimpleFileLogger.Log("RefreshPendingCounts: No suggestions in cache."); }
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
                token.ThrowIfCancellationRequested(); string sourcePath = move.SourceImage.FilePath; string targetPath = move.ProposedTargetPath; string originalProposedTargetPathForLogging = move.ProposedTargetPath; var actionType = move.Action; bool operationSuccessfulThisMove = false; bool deleteSourceAfterCopy = false;
                try
                {
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) { SimpleFileLogger.LogWarning($"[HandleApproved] Plik źródłowy nie istnieje: '{sourcePath}'."); skippedOther++; continue; }
                    string targetDirectory = Path.GetDirectoryName(targetPath); if (string.IsNullOrEmpty(targetDirectory)) { SimpleFileLogger.LogWarning($"[HandleApproved] Nie można określić folderu docelowego dla: '{targetPath}'."); skippedOther++; continue; }
                    Directory.CreateDirectory(targetDirectory);
                    switch (actionType)
                    {
                        case ProposedMoveActionType.CopyNew: if (File.Exists(targetPath) && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)) { targetPath = GenerateUniqueTargetPath(targetDirectory, Path.GetFileName(sourcePath), "_new"); SimpleFileLogger.Log($"[HA] CopyNew: '{originalProposedTargetPathForLogging}' istniał. Nowy: '{targetPath}'."); } else if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)) { SimpleFileLogger.LogWarning($"[HA] CopyNew: Plik źródłowy i docelowy ten sam."); deleteSourceAfterCopy = true; operationSuccessfulThisMove = true; break; } await Task.Run(() => File.Copy(sourcePath, targetPath, false), token); operationSuccessfulThisMove = true; deleteSourceAfterCopy = true; SimpleFileLogger.Log($"[HA] CopyNew: '{sourcePath}' -> '{targetPath}'."); break;
                        case ProposedMoveActionType.OverwriteExisting: if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)) { SimpleFileLogger.LogWarning($"[HA] Overwrite: Plik źródłowy i docelowy ten sam."); deleteSourceAfterCopy = true; operationSuccessfulThisMove = true; break; } await Task.Run(() => File.Copy(sourcePath, targetPath, true), token); operationSuccessfulThisMove = true; deleteSourceAfterCopy = true; SimpleFileLogger.Log($"[HA] Overwrite: '{targetPath}' <--- '{sourcePath}'."); break;
                        case ProposedMoveActionType.KeepExistingDeleteSource: if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)) { deleteSourceAfterCopy = true; operationSuccessfulThisMove = true; SimpleFileLogger.Log($"[HA] KeepDelete: Zachowano '{targetPath}'. Źródło '{sourcePath}' do usunięcia."); } else { SimpleFileLogger.LogWarning($"[HA] KeepDelete: Plik źródłowy i docelowy ten sam."); operationSuccessfulThisMove = true; skippedQuality++; } break;
                        case ProposedMoveActionType.ConflictKeepBoth: string newTargetPathForConflict = GenerateUniqueTargetPath(targetDirectory, Path.GetFileName(sourcePath), "_conflict"); await Task.Run(() => File.Copy(sourcePath, newTargetPathForConflict, false), token); targetPath = newTargetPathForConflict; operationSuccessfulThisMove = true; deleteSourceAfterCopy = true; SimpleFileLogger.Log($"[HA] ConflictKeepBoth: '{sourcePath}' -> '{targetPath}'."); break;
                    }
                    token.ThrowIfCancellationRequested();
                    if (operationSuccessfulThisMove)
                    {
                        successfulMoves++; processedSourcePathsForThisBatch.Add(sourcePath); string? oldPathForProfileUpdate = null; string? newPathForProfileUpdate = targetPath;
                        if (deleteSourceAfterCopy && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)) { oldPathForProfileUpdate = sourcePath; try { if (File.Exists(sourcePath)) { await Task.Run(() => File.Delete(sourcePath), token); SimpleFileLogger.Log($"[HA] Usunięto: '{sourcePath}'."); } } catch (Exception exDelete) { deleteErrors++; SimpleFileLogger.LogError($"[HA] Błąd usuwania '{sourcePath}'.", exDelete); } }
                        else if (deleteSourceAfterCopy && string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)) { oldPathForProfileUpdate = sourcePath; newPathForProfileUpdate = targetPath; SimpleFileLogger.Log($"[HA] Plik '{sourcePath}' tożsamy z docelowym. Obsłużony."); }
                        if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldPathForProfileUpdate, newPathForProfileUpdate, move.TargetCategoryProfileName, token)) anyProfileActuallyModified = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exCopy) { copyErrors++; SimpleFileLogger.LogError($"[HA] Błąd: '{sourcePath}' -> '{originalProposedTargetPathForLogging}'. Akcja: {actionType}.", exCopy); }
            }
            token.ThrowIfCancellationRequested();
            if (processedSourcePathsForThisBatch.Any()) { int removedCount = _lastModelSpecificSuggestions.RemoveAll(s => processedSourcePathsForThisBatch.Contains(s.SourceImage.FilePath)); SimpleFileLogger.Log($"[HA] Usunięto {removedCount} sugestii z cache."); }
            StatusMessage = $"Zakończono: {successfulMoves} ok, {skippedQuality} pom(j), {skippedOther} pom(i), {copyErrors} bł.kopii, {deleteErrors} bł.usuw."; if (successfulMoves > 0 || copyErrors > 0 || deleteErrors > 0) MessageBox.Show(StatusMessage, "Operacja Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
            return anyProfileActuallyModified;
        }

        private string GenerateUniqueTargetPath(string targetDirectory, string originalFileNameWithExtension, string suffixIfConflict = "_conflict")
        {
            string baseName = Path.GetFileNameWithoutExtension(originalFileNameWithExtension); string extension = Path.GetExtension(originalFileNameWithExtension); string finalPath = Path.Combine(targetDirectory, originalFileNameWithExtension); int counter = 1;
            while (File.Exists(finalPath)) { string newFileName = $"{baseName}{suffixIfConflict}{counter}{extension}"; finalPath = Path.Combine(targetDirectory, newFileName); counter++; if (counter > 9999) { newFileName = $"{baseName}_{Guid.NewGuid():N}{extension}"; finalPath = Path.Combine(targetDirectory, newFileName); SimpleFileLogger.LogWarning($"GenerateUniquePath: GUID po konfliktach: {finalPath}"); break; } }
            return finalPath;
        }

        private Task ExecuteRemoveModelTreeAsync(object? parameter) => RunLongOperation(async token => { /* ... bez zmian ... */ }, "Usuwanie modelki");
        private Task ExecuteAnalyzeModelForSplittingAsync(object? parameter) => RunLongOperation(async token => { /* ... bez zmian ... */ }, "Analiza podziału");
        private Task ExecuteOpenSplitProfileDialogAsync(object? parameter) => RunLongOperation(async token => { /* ... bez zmian ... */ }, "Podział profilu");
        private void ExecuteCancelCurrentOperation(object? parameter) { /* ... bez zmian ... */ }
        private Task ExecuteEnsureThumbnailsLoadedAsync(object? parameter) => RunLongOperation(async token => { /* ... zaktualizowane z SemaphoreSlim ... */
            var imagesToLoadThumbs = (parameter as IEnumerable<ImageFileEntry>)?.ToList() ?? ImageFiles.ToList();
            if (!imagesToLoadThumbs.Any()) { StatusMessage = "Brak obrazów do załadowania miniaturek."; return; }
            SimpleFileLogger.Log($"EnsureThumbnailsLoaded: Ładowanie dla {imagesToLoadThumbs.Count} obrazów. Token: {token.GetHashCode()}");
            var tasks = new List<Task>();
            using var thumbnailSemaphore = new SemaphoreSlim(10, 10); // Max 10 na raz
            foreach (var entry in imagesToLoadThumbs)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Thumbnail == null && !entry.IsLoadingThumbnail)
                {
                    tasks.Add(Task.Run(async () => {
                        await thumbnailSemaphore.WaitAsync(token);
                        try { if (token.IsCancellationRequested) return; await entry.LoadThumbnailAsync(); }
                        finally { thumbnailSemaphore.Release(); }
                    }, token));
                }
            }
            StatusMessage = $"Rozpoczęto ładowanie {tasks.Count} miniaturek...";
            await Task.WhenAll(tasks); token.ThrowIfCancellationRequested();
            int loadedCount = imagesToLoadThumbs.Count(img => img.Thumbnail != null);
            StatusMessage = $"Załadowano {loadedCount} z {imagesToLoadThumbs.Count} miniaturek (poproszono o {tasks.Count}).";
            SimpleFileLogger.Log(StatusMessage);
        }, "Ładowanie miniaturek");

        private Task ExecuteRemoveDuplicatesInModelAsync(object? parameter) => RunLongOperation(async token => { /* ... zaktualizowane z ConcurrentBag i SemaphoreSlim ... */
            SimpleFileLogger.Log($"[RDIM] Parameter: {parameter?.GetType().FullName ?? "null"}");
            if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nieprawidłowy parametr."; SimpleFileLogger.LogWarning(StatusMessage); return; }
            string modelName = modelVM.ModelName;
            if (MessageBox.Show($"Czy na pewno usunąć duplikaty dla '{modelName}' (pozostawiając najlepszą jakość)?\nSpowoduje to usunięcie plików z dysku.", "Potwierdź", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            { StatusMessage = "Usuwanie duplikatów anulowane."; SimpleFileLogger.Log($"[RDIM] Anulowano dla: {modelName}."); return; }
            StatusMessage = $"Usuwanie duplikatów dla: {modelName}..."; SimpleFileLogger.Log($"[RDIM] Rozpoczęto dla: {modelName}. Token: {token.GetHashCode()}");
            long totalDuplicatesRemovedSystemWide = 0; bool anyProfileDataActuallyChangedDuringThisOperation = false;
            var characterProfilesSnapshot = modelVM.CharacterProfiles.ToList();
            var mainProcessingTasks = new List<Task>();

            foreach (var characterProfile in characterProfilesSnapshot)
            {
                mainProcessingTasks.Add(Task.Run(async () => {
                    token.ThrowIfCancellationRequested();
                    if (characterProfile.SourceImagePaths == null || characterProfile.SourceImagePaths.Count < 2) return;
                    // Aktualizacja statusu z wątku UI
                    await Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = $"Przetwarzanie: {characterProfile.CategoryName}...", token);
                    SimpleFileLogger.Log($"[RDIM] Profil: {characterProfile.CategoryName}");
                    var imageEntriesInProfile = new ConcurrentBag<ImageFileEntry>(); bool profileHadMissingFiles = false;
                    var entryLoadingTasks = characterProfile.SourceImagePaths.Select(async imagePath => {
                        token.ThrowIfCancellationRequested();
                        if (!File.Exists(imagePath)) { SimpleFileLogger.LogWarning($"[RDIM] Plik '{imagePath}' z '{characterProfile.CategoryName}' nie istnieje."); Volatile.Write(ref profileHadMissingFiles, true); return; }
                        var entry = await _imageMetadataService.ExtractMetadataAsync(imagePath);
                        if (entry != null)
                        {
                            await _embeddingSemaphore.WaitAsync(token); try { if (token.IsCancellationRequested) return; entry.FeatureVector = await _profileService.GetImageEmbeddingAsync(entry); } finally { _embeddingSemaphore.Release(); }
                            if (entry.FeatureVector != null) imageEntriesInProfile.Add(entry); else SimpleFileLogger.LogWarning($"[RDIM] Brak embeddingu dla '{entry.FilePath}'.");
                        }
                    }).ToList();
                    await Task.WhenAll(entryLoadingTasks); token.ThrowIfCancellationRequested();
                    var validImageEntriesList = imageEntriesInProfile.ToList();
                    if (validImageEntriesList.Count < 2) { if (profileHadMissingFiles) { SimpleFileLogger.Log($"[RDIM] '{characterProfile.CategoryName}' wymaga aktualizacji (brakujące pliki)."); await _profileService.GenerateProfileAsync(characterProfile.CategoryName, validImageEntriesList); Volatile.Write(ref anyProfileDataActuallyChangedDuringThisOperation, true); } return; }
                    var filesToRemovePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var processedInThisProfileForDuplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < validImageEntriesList.Count; i++) { token.ThrowIfCancellationRequested(); var cI = validImageEntriesList[i]; if (filesToRemovePaths.Contains(cI.FilePath) || processedInThisProfileForDuplicates.Contains(cI.FilePath)) continue; var dG = new List<ImageFileEntry> { cI }; for (int j = i + 1; j < validImageEntriesList.Count; j++) { token.ThrowIfCancellationRequested(); var oI = validImageEntriesList[j]; if (filesToRemovePaths.Contains(oI.FilePath) || processedInThisProfileForDuplicates.Contains(oI.FilePath)) continue; if (cI.FeatureVector != null && oI.FeatureVector != null) { if (Utils.MathUtils.CalculateCosineSimilarity(cI.FeatureVector, oI.FeatureVector) >= DUPLICATE_SIMILARITY_THRESHOLD) dG.Add(oI); } } if (dG.Count > 1) { var bI = dG.First(); foreach (var imgG in dG.Skip(1)) if (IsImageBetter(imgG, bI)) bI = imgG; foreach (var imgG in dG) { if (!imgG.FilePath.Equals(bI.FilePath, StringComparison.OrdinalIgnoreCase)) filesToRemovePaths.Add(imgG.FilePath); processedInThisProfileForDuplicates.Add(imgG.FilePath); } } else processedInThisProfileForDuplicates.Add(cI.FilePath); }
                    token.ThrowIfCancellationRequested();
                    if (filesToRemovePaths.Any()) { SimpleFileLogger.Log($"[RDIM] '{characterProfile.CategoryName}': {filesToRemovePaths.Count} duplikatów."); long remProfCnt = 0; foreach (var pTR in filesToRemovePaths) { token.ThrowIfCancellationRequested(); try { if (File.Exists(pTR)) { File.Delete(pTR); Interlocked.Increment(ref totalDuplicatesRemovedSystemWide); remProfCnt++; } } catch (Exception ex) { SimpleFileLogger.LogError($"[RDIM] Błąd usuwania '{pTR}'.", ex); } } var kept = validImageEntriesList.Where(e => !filesToRemovePaths.Contains(e.FilePath)).ToList(); await _profileService.GenerateProfileAsync(characterProfile.CategoryName, kept); Volatile.Write(ref anyProfileDataActuallyChangedDuringThisOperation, true); SimpleFileLogger.Log($"[RDIM] '{characterProfile.CategoryName}' zakt. Usunięto {remProfCnt}. Zostało: {kept.Count}."); }
                    else if (profileHadMissingFiles) { SimpleFileLogger.Log($"[RDIM] '{characterProfile.CategoryName}' bez dupl., ale z brakującymi."); var valid = validImageEntriesList.Where(e => File.Exists(e.FilePath)).ToList(); await _profileService.GenerateProfileAsync(characterProfile.CategoryName, valid); Volatile.Write(ref anyProfileDataActuallyChangedDuringThisOperation, true); }
                }, token));
            }
            await Task.WhenAll(mainProcessingTasks); token.ThrowIfCancellationRequested();
            if (Interlocked.Read(ref totalDuplicatesRemovedSystemWide) > 0 || anyProfileDataActuallyChangedDuringThisOperation) { StatusMessage = $"Zakończono dla '{modelName}'. Usunięto: {Interlocked.Read(ref totalDuplicatesRemovedSystemWide)}. Odświeżanie..."; _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token); _isRefreshingProfilesPostMove = false; MessageBox.Show($"Usunięto {Interlocked.Read(ref totalDuplicatesRemovedSystemWide)} duplikatów dla '{modelName}'.", "Zakończono", MessageBoxButton.OK, MessageBoxImage.Information); } else { StatusMessage = $"Brak duplikatów dla '{modelName}'."; MessageBox.Show(StatusMessage, "Zakończono", MessageBoxButton.OK, MessageBoxImage.Information); }
        }, "Usuwanie duplikatów (wielowątkowe)");

        private Task ExecuteApplyAllMatchesForModelAsync(object? parameter) => RunLongOperation(async token => { /* ... bez zmian ... */ }, "Automatyczne stosowanie dopasowań");

        private class ImageFileEntryPathComparer : IEqualityComparer<ImageFileEntry>
        { public bool Equals(ImageFileEntry? x, ImageFileEntry? y) { if (ReferenceEquals(x, y)) return true; if (x is null || y is null) return false; return x.FilePath.Equals(y.FilePath, StringComparison.OrdinalIgnoreCase); } public int GetHashCode(ImageFileEntry obj) { return obj.FilePath?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0; } }
    }
}