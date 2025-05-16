// Plik: ViewModels/MainWindowViewModel.cs
using CosplayManager.Models;
using CosplayManager.Services;
using CosplayManager.ViewModels.Base;
using CosplayManager.Views; // Upewnij się, że ta przestrzeń nazw jest poprawna dla PreviewChangesWindow i SplitProfileWindow
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
        private string? _lastScannedModelNameForSuggestions;
        private bool _isRefreshingProfilesPostMove = false;

        private const double DUPLICATE_SIMILARITY_THRESHOLD = 0.98; // Próg dla identycznych graficznie zdjęć (do dostosowania)
        // SuggestionSimilarityThreshold jest już polem i jest używane dla dopasowania do kategorii

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
                    CommandManager.InvalidateRequerySuggested(); // Ogólne odświeżenie CanExecute
                }
                if (_selectedProfile == null && oldSelectedProfileName != null &&
                    !_profileService.GetAllProfiles().Any(p => p.CategoryName == oldSelectedProfileName))
                {
                    UpdateEditFieldsFromSelectedProfile(); // Wyczyść pola, jeśli profil został usunięty
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
                    if (!string.IsNullOrEmpty(_lastScannedModelNameForSuggestions))
                    {
                        // Jeśli próg się zmienił, odświeżamy liczniki na podstawie istniejących danych w cache _lastModelSpecificSuggestions
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
        public ICommand SuggestImagesCommand { get; } // Globalne sugestie
        public ICommand SaveAppSettingsCommand { get; }
        public ICommand MatchModelSpecificCommand { get; } // Sugestie dla konkretnej modelki
        public ICommand CheckCharacterSuggestionsCommand { get; } // Sugestie dla konkretnej postaci
        public ICommand RemoveModelTreeCommand { get; }
        public ICommand AnalyzeModelForSplittingCommand { get; }
        public ICommand OpenSplitProfileDialogCommand { get; }
        public ICommand CancelCurrentOperationCommand { get; }
        public ICommand EnsureThumbnailsLoadedCommand { get; }


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
            SuggestImagesCommand = new AsyncRelayCommand(ExecuteSuggestImagesAsync, CanExecuteSuggestImages); // Globalne
            SaveAppSettingsCommand = new AsyncRelayCommand(ExecuteSaveAppSettingsAsync, CanExecuteSaveAppSettings);
            MatchModelSpecificCommand = new AsyncRelayCommand(ExecuteMatchModelSpecificAsync, CanExecuteMatchModelSpecific); // Dla modelki
            CheckCharacterSuggestionsCommand = new AsyncRelayCommand(ExecuteCheckCharacterSuggestionsAsync, CanExecuteCheckCharacterSuggestions); // Dla postaci
            RemoveModelTreeCommand = new AsyncRelayCommand(ExecuteRemoveModelTreeAsync, CanExecuteRemoveModelTree);
            AnalyzeModelForSplittingCommand = new AsyncRelayCommand(ExecuteAnalyzeModelForSplittingAsync, CanExecuteAnalyzeModelForSplitting);
            OpenSplitProfileDialogCommand = new AsyncRelayCommand(ExecuteOpenSplitProfileDialogAsync, CanExecuteOpenSplitProfileDialog);

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
                    // StatusMessage zostanie ustawiony przez samą operację po jej zakończeniu (lub błędu)
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
                // Nie resetuj StatusMessage tutaj, jeśli operacja sama go ustawia
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
                // Jeśli tylko nazwa modelki, to domyślnie może to być kategoria ogólna dla tej modelki
                CurrentProfileNameForEdit = $"{ModelNameInput} - General";
            }
            // Usunięto przypadek, gdzie tylko CharacterNameInput jest podany, bo to niejednoznaczne bez modelki
            else
            {
                CurrentProfileNameForEdit = string.Empty;
            }
        }

        private (string model, string character) ParseCategoryName(string? categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return ("UnknownModel", "UnknownCharacter");
            var parts = categoryName.Split(new[] { " - " }, 2, StringSplitOptions.None); // Podział na max 2 części
            string model = parts.Length > 0 ? parts[0].Trim() : categoryName.Trim();
            string character = parts.Length > 1 ? parts[1].Trim() : "General"; // Jeśli nie ma drugiej części, to "General"

            if (string.IsNullOrWhiteSpace(model)) model = "UnknownModel"; // Na wszelki wypadek
            if (string.IsNullOrWhiteSpace(character)) character = "General"; // Jeśli druga część była pusta
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
            // Dodatkowe specyficzne zamiany, jeśli są potrzebne
            sanitizedName = sanitizedName.Replace(":", "_").Replace("?", "_").Replace("*", "_")
                                       .Replace("\"", "_").Replace("<", "_").Replace(">", "_")
                                       .Replace("|", "_").Replace("/", "_").Replace("\\", "_");
            // Usuń kropki z początku/końca, bo mogą powodować problemy w niektórych systemach
            sanitizedName = sanitizedName.Trim().TrimStart('.').TrimEnd('.');
            if (string.IsNullOrWhiteSpace(sanitizedName)) return "_"; // Jeśli wszystko zostało usunięte
            return sanitizedName;
        }

        private void UpdateEditFieldsFromSelectedProfile()
        {
            if (_selectedProfile != null)
            {
                CurrentProfileNameForEdit = _selectedProfile.CategoryName;
                var (model, characterFullName) = ParseCategoryName(_selectedProfile.CategoryName);
                ModelNameInput = model;
                // Jeśli characterFullName to "General", a pełna nazwa profilu to tylko nazwa modelki,
                // wtedy CharacterNameInput powinno być puste lub "General" zgodnie z preferencją wyświetlania.
                // Dla spójności, jeśli postać to "General", wyświetlmy "General".
                CharacterNameInput = characterFullName;

                var newImageFiles = new ObservableCollection<ImageFileEntry>();
                if (_selectedProfile.SourceImagePaths != null)
                {
                    // Asynchroniczne ładowanie metadanych dla plików w profilu może być potrzebne,
                    // jeśli ImageFileEntry nie przechowuje ich trwale.
                    // Na razie zakładamy, że `SourceImagePaths` to tylko ścieżki.
                    // W `ExecuteGenerateProfileCommand` dbamy o to, by `ImageFiles` miały metadane.
                    foreach (var path in _selectedProfile.SourceImagePaths)
                    {
                        if (File.Exists(path))
                        {
                            // Tworzymy prosty ImageFileEntry tylko ze ścieżką i nazwą.
                            // Pełne metadane zostaną załadowane w razie potrzeby (np. przy generowaniu).
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
            SimpleFileLogger.Log("ClearModelSpecificSuggestionsCache: Czyszczenie cache sugestii dla ostatnio skanowanej modelki.");
            _lastModelSpecificSuggestions.Clear();
            _lastScannedModelNameForSuggestions = null;
            // Po wyczyszczeniu cache'u, odświeżamy liczniki w UI
            RefreshPendingSuggestionCountsFromCache(); // Ta metoda powinna obsłużyć sytuację, gdy _lastScannedModelNameForSuggestions jest null
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
                    // Daj chwilę na zakończenie anulowanej operacji
                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2), _activeLongOperationCts.Token), Task.Run(() => { /* No-op task to await on */}));
                }
                catch (OperationCanceledException) { /* Oczekiwane */ }
                _activeLongOperationCts.Dispose();
                _activeLongOperationCts = null;
            }
            await _settingsService.SaveSettingsAsync(GetCurrentSettings());
            SimpleFileLogger.Log("ViewModel: OnAppClosingAsync - Ustawienia zapisane.");
            // Zapis profili i cache embeddingów powinien być teraz obsługiwany przez MainWindow.xaml.cs
            // lub przez wywołanie SaveProfilesCommand, jeśli jest taka potrzeba.
            // EmbeddingCacheService.SaveCacheToFile() jest wywoływane w MainWindow.Closing.
        }

        // CanExecute metody
        private bool CanExecuteLoadProfiles(object? arg) => !IsBusy;
        private bool CanExecuteSaveAllProfiles(object? arg) => !IsBusy && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles);
        private bool CanExecuteAutoCreateProfiles(object? arg) => !IsBusy && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath);
        private bool CanExecuteGenerateProfile(object? parameter = null) => !IsBusy && !string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) && !string.IsNullOrWhiteSpace(ModelNameInput) && !string.IsNullOrWhiteSpace(CharacterNameInput) && ImageFiles.Any();
        private bool CanExecuteSuggestImages(object? parameter = null) => !IsBusy && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles) && !string.IsNullOrWhiteSpace(SourceFolderNamesInput);
        private bool CanExecuteRemoveProfile(object? parameter) => !IsBusy && (parameter is CategoryProfile || SelectedProfile != null); // Poprawione dla parametru
        private bool CanExecuteCheckCharacterSuggestions(object? parameter) =>
            !IsBusy && (parameter is CategoryProfile profile ? profile : SelectedProfile) != null && // Użyj parametru jeśli jest, inaczej SelectedProfile
            !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) &&
            !string.IsNullOrWhiteSpace(SourceFolderNamesInput) && (parameter is CategoryProfile p ? p.CentroidEmbedding : SelectedProfile?.CentroidEmbedding) != null;
        private bool CanExecuteMatchModelSpecific(object? parameter)
        {
            if (IsBusy) return false;
            if (!(parameter is ModelDisplayViewModel modelVM)) return false; // Musi być ModelDisplayViewModel
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


        private Task ExecuteLoadProfilesAsync(object? parameter = null) =>
            RunLongOperation(InternalExecuteLoadProfilesAsync, "Ładowanie profili");

        private async Task InternalExecuteLoadProfilesAsync(CancellationToken token)
        {
            SimpleFileLogger.Log($"InternalExecuteLoadProfilesAsync. RefreshFlag: {_isRefreshingProfilesPostMove}. Token: {token.GetHashCode()}");
            if (!_isRefreshingProfilesPostMove) ClearModelSpecificSuggestionsCache();
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
                if (!string.IsNullOrEmpty(prevSelectedName)) SelectedProfile = flatProfiles?.FirstOrDefault(p => p.CategoryName.Equals(prevSelectedName, StringComparison.OrdinalIgnoreCase));
                else if (SelectedProfile != null && !(flatProfiles?.Any(p => p.CategoryName == SelectedProfile.CategoryName) ?? false)) SelectedProfile = null;
                OnPropertyChanged(nameof(AnyProfilesLoaded));
                if (_isRefreshingProfilesPostMove || !string.IsNullOrWhiteSpace(_lastScannedModelNameForSuggestions))
                {
                    RefreshPendingSuggestionCountsFromCache(); // Odśwież liczniki, jeśli była to operacja po ruchu lub mamy dane dla modelu
                }
            });
        }

        private Task ExecuteGenerateProfileAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) ||
                    string.IsNullOrWhiteSpace(ModelNameInput) ||
                    string.IsNullOrWhiteSpace(CharacterNameInput))
                {
                    StatusMessage = "Błąd: Nazwa modelki i postaci oraz pełna nazwa profilu muszą być zdefiniowane.";
                    MessageBox.Show(StatusMessage, "Błąd danych profilu", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                string catName = CurrentProfileNameForEdit; // Powinna być już poprawnie sformatowana przez UpdateCurrentProfileNameForEdit
                SimpleFileLogger.Log($"Generowanie profilu '{catName}' ({ImageFiles.Count} obr.). Token: {token.GetHashCode()}");

                List<ImageFileEntry> entriesToProcess = new List<ImageFileEntry>();
                foreach (var file in ImageFiles) // ImageFiles to ObservableCollection<ImageFileEntry> z UI
                {
                    token.ThrowIfCancellationRequested();
                    // Upewniamy się, że każdy ImageFileEntry ma załadowane metadane (FileLastModifiedUtc, FileSize)
                    // To jest kluczowe dla poprawnego działania cache'u embeddingów.
                    if (file.FileSize == 0 || file.FileLastModifiedUtc == DateTime.MinValue) // Prosty warunek, że metadane mogły nie być załadowane
                    {
                        var updatedEntry = await _imageMetadataService.ExtractMetadataAsync(file.FilePath);
                        if (updatedEntry != null)
                        {
                            // Aktualizujemy oryginalny obiekt w kolekcji, jeśli to możliwe, lub dodajemy nowy
                            // Dla uproszczenia, tworzymy nową listę przetworzonych wpisów
                            entriesToProcess.Add(updatedEntry);
                        }
                        else
                        {
                            SimpleFileLogger.LogWarning($"ExecuteGenerateProfileAsync: Nie udało się załadować metadanych dla {file.FilePath} (z listy ImageFiles), pomijam.");
                        }
                    }
                    else
                    {
                        entriesToProcess.Add(file); // Metadane już są
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
                token.ThrowIfCancellationRequested();
                StatusMessage = $"Profil '{catName}' wygenerowany/zaktualizowany.";
                await InternalExecuteLoadProfilesAsync(token); // Odśwież listę profili w UI
                // Spróbuj ponownie wybrać profil po odświeżeniu
                SelectedProfile = _profileService.GetProfile(catName);
            }, "Generowanie profilu");

        private Task ExecuteSaveAllProfilesAsync(object? parameter = null) =>
           RunLongOperation(async token =>
           {
               SimpleFileLogger.Log($"Zapis wszystkich profili. Token: {token.GetHashCode()}");
               await _profileService.SaveAllProfilesAsync(); // Ta metoda zapisuje też embedding cache
               token.ThrowIfCancellationRequested();
               StatusMessage = "Wszystkie profile i cache embeddingów zapisane.";
               MessageBox.Show(StatusMessage, "Zapisano", MessageBoxButton.OK, MessageBoxImage.Information);
           }, "Zapisywanie wszystkich profili");

        private Task ExecuteRemoveProfileAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
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
                        if (SelectedProfile?.CategoryName == name) SelectedProfile = null; // Odznacz, jeśli usunięto zaznaczony
                        await InternalExecuteLoadProfilesAsync(token); // Odśwież listę
                    }
                    else StatusMessage = $"Nie udało się usunąć profilu '{name}'.";
                }
            }, "Usuwanie profilu");

        private async void ExecuteAddFilesToProfile(object? parameter = null) // Zmieniono na async void dla await
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "Obrazy|*.jpg;*.jpeg;*.png;*.webp|Wszystkie pliki|*.*", Title = "Wybierz obrazy do dodania", Multiselect = true };
            if (openFileDialog.ShowDialog() == true)
            {
                IsBusy = true; // Ustaw IsBusy na czas przetwarzania
                StatusMessage = "Dodawanie plików i ładowanie metadanych...";
                int addedCount = 0;
                try
                {
                    foreach (string filePath in openFileDialog.FileNames)
                    {
                        if (!ImageFiles.Any(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            // Od razu załaduj metadane, aby ImageFileEntry był kompletny
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
            SelectedProfile = null; // Odznacza aktualnie wybrany profil
            // CurrentProfileNameForEdit jest aktualizowane przez ModelNameInput i CharacterNameInput
            ModelNameInput = string.Empty;
            CharacterNameInput = string.Empty;
            ImageFiles.Clear(); // Wyczyść listę plików
            StatusMessage = "Gotowy do utworzenia nowego profilu. Wprowadź nazwę modelki i postaci.";
        }
        private void ExecuteSelectLibraryPath(object? parameter = null)
        {
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
        }

        private Task ExecuteAutoCreateProfilesAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                SimpleFileLogger.Log($"AutoCreateProfiles: Rozpoczęto skanowanie folderu biblioteki: {LibraryRootPath}. Token: {token.GetHashCode()}");
                var mixedFoldersToIgnore = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                token.ThrowIfCancellationRequested();
                int totalProfilesCreatedOrUpdated = 0;
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
                    string modelName = Path.GetFileName(modelDir);
                    if (string.IsNullOrWhiteSpace(modelName) || mixedFoldersToIgnore.Contains(modelName)) // Ignoruj foldery "Mix" na poziomie modelek
                    {
                        SimpleFileLogger.Log($"AutoCreateProfiles: Pomijanie folderu na poziomie modelek: '{modelName}' (może być folderem Mix).");
                        continue;
                    }
                    try
                    {
                        // Przetwarzanie folderów postaci bezpośrednio w folderze modelki
                        totalProfilesCreatedOrUpdated += await InternalProcessDirectoryForProfileCreationAsync(modelDir, modelName, new List<string>(), mixedFoldersToIgnore, token, isTopLevelCharacterDirectory: true);
                    }
                    catch (OperationCanceledException) { throw; } // Przekaż dalej
                    catch (Exception ex) { SimpleFileLogger.LogError($"AutoCreateProfiles: Błąd podczas iteracji podfolderów dla modelki '{modelName}' w '{modelDir}'", ex); }
                }
                token.ThrowIfCancellationRequested();
                StatusMessage = $"Automatyczne tworzenie profili zakończone. Utworzono/zaktualizowano: {totalProfilesCreatedOrUpdated} profili.";
                await InternalExecuteLoadProfilesAsync(token); // Odśwież widok profili
                MessageBox.Show(StatusMessage, "Skanowanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "Automatyczne tworzenie profili");

        private async Task<int> InternalProcessDirectoryForProfileCreationAsync(string currentPath, string modelName, List<string> parentCharacterParts, HashSet<string> mixedFoldersToIgnore, CancellationToken token, bool isTopLevelCharacterDirectory = false)
        {
            token.ThrowIfCancellationRequested();
            int processedCountThisLevel = 0;
            string currentDirectoryName = Path.GetFileName(currentPath);

            List<string> currentFullCharacterParts;
            if (isTopLevelCharacterDirectory) // Jeśli to folder postaci bezpośrednio w folderze modelki
            {
                currentFullCharacterParts = new List<string> { currentDirectoryName };
            }
            else // Jeśli to podfolder w strukturze postaci
            {
                currentFullCharacterParts = new List<string>(parentCharacterParts) { currentDirectoryName };
            }

            string characterFullName = string.Join(" - ", currentFullCharacterParts);
            string categoryName = $"{modelName} - {characterFullName}";

            SimpleFileLogger.Log($"InternalProcessDir: Przetwarzanie folderu '{currentPath}' dla potencjalnego profilu '{categoryName}'.");

            List<string> imagePathsInThisExactDirectory = new List<string>();
            try
            {
                imagePathsInThisExactDirectory = Directory.GetFiles(currentPath, "*.*", SearchOption.TopDirectoryOnly)
                                           .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f)))
                                           .ToList();
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogWarning($"InternalProcessDir: Błąd odczytu plików z '{currentPath}': {ex.Message}");
            }
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
                    processedCountThisLevel++;
                    SimpleFileLogger.Log($"InternalProcessDir: Wygenerowano/zaktualizowano profil '{categoryName}' z {entriesForProfile.Count} obrazami z folderu '{currentPath}'.");
                }
            }
            // Jeśli folder jest pusty, ale profil dla niego istnieje (np. z poprzedniego skanowania), zaktualizuj go pustą listą plików, co go usunie/wyczyści.
            else if (_profileService.GetProfile(categoryName) != null)
            {
                await _profileService.GenerateProfileAsync(categoryName, new List<ImageFileEntry>());
                SimpleFileLogger.Log($"InternalProcessDir: Profil '{categoryName}' istniał, ale folder '{currentPath}' jest pusty. Profil wyczyszczony.");
                // Nie zwiększamy processedCountThisLevel, bo to raczej czyszczenie niż tworzenie/aktualizacja z danymi
            }
            token.ThrowIfCancellationRequested();

            // Rekurencyjne przetwarzanie podfolderów, które nie są folderami "Mix"
            try
            {
                foreach (var subDirectoryPath in Directory.GetDirectories(currentPath))
                {
                    token.ThrowIfCancellationRequested();
                    string subDirectoryName = Path.GetFileName(subDirectoryPath);
                    if (mixedFoldersToIgnore.Contains(subDirectoryName))
                    {
                        SimpleFileLogger.Log($"InternalProcessDir: Pomijanie podfolderu '{subDirectoryPath}' (jest na liście ignorowanych).");
                        continue; // Ignoruj foldery "Mix" itp.
                    }
                    // Dla podfolderów, przekazujemy aktualną ścieżkę części nazwy postaci
                    processedCountThisLevel += await InternalProcessDirectoryForProfileCreationAsync(subDirectoryPath, modelName, currentFullCharacterParts, mixedFoldersToIgnore, token, isTopLevelCharacterDirectory: false);
                }
            }
            catch (OperationCanceledException) { throw; } // Przekaż dalej
            catch (Exception ex) { SimpleFileLogger.LogError($"InternalProcessDir: Błąd przetwarzania podfolderów dla '{currentPath}'", ex); }

            return processedCountThisLevel;
        }

        private async Task<(Models.ProposedMove? proposedMove, bool wasActionAutoHandled)> ProcessDuplicateOrSuggestNewAsync(
            ImageFileEntry sourceImageEntry,
            CategoryProfile targetProfile,
            double similarityToCentroid,
            string modelDirectoryPath,
            float[] sourceImageEmbedding,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

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
                filesInTargetDir = new List<string>(); // Kontynuuj z pustą listą, aby uniknąć przerwania całej operacji
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

                float[]? existingTargetEmbedding = await _profileService.GetImageEmbeddingAsync(existingTargetEntry);
                if (existingTargetEmbedding == null) continue;

                double similarityBetweenSourceAndExisting = Utils.MathUtils.CalculateCosineSimilarity(sourceImageEmbedding, existingTargetEmbedding);

                if (similarityBetweenSourceAndExisting >= DUPLICATE_SIMILARITY_THRESHOLD)
                {
                    bool sourceIsBetter = IsImageBetter(sourceImageEntry, existingTargetEntry);

                    if (sourceIsBetter)
                    {
                        SimpleFileLogger.Log($"[AutoReplace] Znaleziono lepszą wersję. Źródło: '{sourceImageEntry.FilePath}' ({sourceImageEntry.Width}x{sourceImageEntry.Height}, {sourceImageEntry.FileSize}B). Cel: '{existingTargetEntry.FilePath}' ({existingTargetEntry.Width}x{existingTargetEntry.Height}, {existingTargetEntry.FileSize}B).");
                        try
                        {
                            File.Copy(sourceImageEntry.FilePath, existingTargetEntry.FilePath, true);
                            SimpleFileLogger.Log($"[AutoReplace] Nadpisano '{existingTargetEntry.FilePath}' plikiem '{sourceImageEntry.FilePath}'.");

                            string oldSourcePath = sourceImageEntry.FilePath;
                            File.Delete(sourceImageEntry.FilePath);
                            SimpleFileLogger.Log($"[AutoReplace] Usunięto oryginalny plik źródłowy '{oldSourcePath}'.");

                            await HandleFileMovedOrDeletedUpdateProfilesAsync(oldSourcePath, existingTargetEntry.FilePath, targetProfile.CategoryName, token);
                        }
                        catch (Exception ex)
                        {
                            SimpleFileLogger.LogError($"[AutoReplace] Błąd podczas automatycznego zastępowania lepszym zdjęciem: {sourceImageEntry.FilePath} -> {existingTargetEntry.FilePath}", ex);
                        }
                        return (null, true);
                    }
                    else
                    {
                        SimpleFileLogger.Log($"[AutoDeleteSource] Istniejąca wersja w '{targetCharacterFolderPath}' jest lepsza/równa od '{sourceImageEntry.FilePath}'. Usuwanie pliku źródłowego.");
                        try
                        {
                            string oldSourcePath = sourceImageEntry.FilePath;
                            File.Delete(sourceImageEntry.FilePath);
                            SimpleFileLogger.Log($"[AutoDeleteSource] Usunięto plik źródłowy '{oldSourcePath}'.");
                            await HandleFileMovedOrDeletedUpdateProfilesAsync(oldSourcePath, null, null, token);
                        }
                        catch (Exception ex)
                        {
                            SimpleFileLogger.LogError($"[AutoDeleteSource] Błąd podczas automatycznego usuwania gorszego/równego zdjęcia ze źródła: {sourceImageEntry.FilePath}", ex);
                        }
                        return (null, true);
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
                    SimpleFileLogger.Log($"[Suggest] Plik '{sourceImageEntry.FilePath}' jest już w docelowej lokalizacji '{proposedPathStandard}' i nie jest duplikatem o innej jakości. Brak sugestii.");
                    return (null, false);
                }
                else
                {
                    actionStandard = ProposedMoveActionType.CopyNew;
                }

                var move = new Models.ProposedMove(sourceImageEntry, displayTargetStandard, proposedPathStandard, similarityToCentroid, targetProfile.CategoryName, actionStandard);
                SimpleFileLogger.Log($"[Suggest] Utworzono sugestię: {actionStandard} dla '{sourceImageEntry.FileName}' do '{targetProfile.CategoryName}', SimToCentroid: {similarityToCentroid:F4}");
                return (move, false);
            }

            SimpleFileLogger.Log($"[Suggest] Plik '{sourceImageEntry.FileName}' (SimToCentroid: {similarityToCentroid:F4}) nie pasuje wystarczająco ({SuggestionSimilarityThreshold:F2}) do profilu '{targetProfile.CategoryName}' i nie jest duplikatem graficznym.");
            return (null, false);
        }


        private async Task HandleFileMovedOrDeletedUpdateProfilesAsync(string oldPath, string? newPathIfMoved, string? targetCategoryNameIfMoved, CancellationToken token)
        {
            SimpleFileLogger.Log($"[ProfileUpdate] Rozpoczęto aktualizację profili po operacji na pliku: OldPath='{oldPath}', NewPath='{newPathIfMoved}', TargetCat='{targetCategoryNameIfMoved}'");
            var affectedProfileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyProfileStructureChanged = false;

            // Pobierz aktualną listę profili z serwisu za każdym razem, aby pracować na najnowszych danych
            var allProfiles = _profileService.GetAllProfiles().ToList();

            foreach (var profile in allProfiles) // Iteruj po kopii, jeśli modyfikujesz oryginalną listę w _profileService, lub po prostu po danych z GetAllProfiles
            {
                token.ThrowIfCancellationRequested();
                bool currentProfileModifiedByThisFileOperation = false;
                if (profile.SourceImagePaths != null && profile.SourceImagePaths.Any(p => p.Equals(oldPath, StringComparison.OrdinalIgnoreCase)))
                {
                    int removedCount = profile.SourceImagePaths.RemoveAll(p => p.Equals(oldPath, StringComparison.OrdinalIgnoreCase));
                    if (removedCount > 0)
                    {
                        SimpleFileLogger.Log($"[ProfileUpdate] Usunięto '{oldPath}' ({removedCount}x) z SourceImagePaths profilu '{profile.CategoryName}'.");
                        currentProfileModifiedByThisFileOperation = true;
                    }
                }

                if (!string.IsNullOrWhiteSpace(newPathIfMoved) &&
                    !string.IsNullOrWhiteSpace(targetCategoryNameIfMoved) &&
                    profile.CategoryName.Equals(targetCategoryNameIfMoved, StringComparison.OrdinalIgnoreCase))
                {
                    if (profile.SourceImagePaths == null) profile.SourceImagePaths = new List<string>();

                    if (!profile.SourceImagePaths.Contains(newPathIfMoved, StringComparison.OrdinalIgnoreCase))
                    {
                        profile.SourceImagePaths.Add(newPathIfMoved);
                        SimpleFileLogger.Log($"[ProfileUpdate] Dodano '{newPathIfMoved}' do SourceImagePaths profilu '{profile.CategoryName}'.");
                        currentProfileModifiedByThisFileOperation = true;
                    }
                }

                if (currentProfileModifiedByThisFileOperation)
                {
                    affectedProfileNames.Add(profile.CategoryName);
                    anyProfileStructureChanged = true; // Ustaw ogólną flagę
                }
            }
            token.ThrowIfCancellationRequested();

            if (anyProfileStructureChanged)
            {
                SimpleFileLogger.Log($"[ProfileUpdate] Zidentyfikowano {affectedProfileNames.Count} profili do potencjalnego przeliczenia: {string.Join(", ", affectedProfileNames)}");

                // Zamiast wywoływać InternalRefreshProfilesAsync, które ładuje wszystko od nowa,
                // spróbujmy zaktualizować tylko te dotknięte profile.
                // To wymaga, aby ProfileService miał metodę do aktualizacji pojedynczego profilu na podstawie jego SourceImagePaths
                // lub abyśmy tutaj zebrali ImageFileEntry dla każdego affectedProfile i wywołali GenerateProfileAsync.

                List<ImageFileEntry> entriesForAffectedProfile;
                foreach (var affectedName in affectedProfileNames)
                {
                    token.ThrowIfCancellationRequested();
                    var affectedProfile = _profileService.GetProfile(affectedName); // Pobierz zaktualizowany obiekt profilu
                    if (affectedProfile == null || affectedProfile.SourceImagePaths == null) continue;

                    entriesForAffectedProfile = new List<ImageFileEntry>();
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
                            SimpleFileLogger.LogWarning($"[ProfileUpdate] Ścieżka '{path}' w profilu '{affectedName}' nie istnieje na dysku podczas przeliczania centroidu.");
                        }
                    }
                    // Jeśli profil po usunięciu/dodaniu ścieżek stał się pusty, GenerateProfileAsync go wyczyści/usunie.
                    SimpleFileLogger.Log($"[ProfileUpdate] Przeliczanie profilu '{affectedName}' z {entriesForAffectedProfile.Count} obrazami.");
                    await _profileService.GenerateProfileAsync(affectedName, entriesForAffectedProfile);
                }

                // Po zaktualizowaniu konkretnych profili, musimy odświeżyć całą hierarchiczną listę w UI,
                // aby odzwierciedlić zmiany (np. w liczbie obrazów, potencjalnie centroidach).
                // _isRefreshingProfilesPostMove jest już ustawiane przez metody wywołujące, jeśli trzeba.
                // Tutaj upewniamy się, że po tych specyficznych aktualizacjach UI jest odświeżone.
                // Ustawienie flagi _isRefreshingProfilesPostMove i wywołanie InternalExecuteLoadProfilesAsync
                // jest bezpiecznym sposobem na odświeżenie UI.
                if (affectedProfileNames.Any())
                {
                    _isRefreshingProfilesPostMove = true; // Sygnalizuj, że to odświeżenie po zmianach
                    await InternalExecuteLoadProfilesAsync(token);
                    _isRefreshingProfilesPostMove = false;
                }

                SimpleFileLogger.Log($"[ProfileUpdate] Zakończono aktualizację {affectedProfileNames.Count} profili.");
            }
            else
            {
                SimpleFileLogger.Log($"[ProfileUpdate] Nie znaleziono profili, które wymagałyby aktualizacji dla ścieżki '{oldPath}'.");
            }
        }

        private Task ExecuteMatchModelSpecificAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nie wybrano modelki z listy."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                SimpleFileLogger.Log($"MatchModelSpecific dla '{modelVM.ModelName}'. Token: {token.GetHashCode()}");
                token.ThrowIfCancellationRequested();
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe (np. 'Mix') w ustawieniach."; MessageBox.Show(StatusMessage, "Brak Folderów Źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();

                var movesForSuggestionWindow = new List<Models.ProposedMove>();
                string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName); // Ścieżka do folderu modelki
                if (!Directory.Exists(modelPath))
                {
                    StatusMessage = $"Błąd: Folder dla modelki '{modelVM.ModelName}' nie istnieje: {modelPath}";
                    MessageBox.Show(StatusMessage, "Błąd Folderu Modelki", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                await Application.Current.Dispatcher.InvokeAsync(() => { modelVM.PendingSuggestionsCount = 0; foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; });
                token.ThrowIfCancellationRequested();
                int filesFoundInMix = 0, filesWithEmbeddings = 0, autoActionsCount = 0, suggestionsForWindowCount = 0;

                foreach (var mixFolderName in mixedFolders)
                {
                    token.ThrowIfCancellationRequested();
                    string currentMixPath = Path.Combine(modelPath, mixFolderName); // Pełna ścieżka do folderu Mix/Unsorted itp. wewnątrz folderu modelki
                    if (Directory.Exists(currentMixPath))
                    {
                        var imagePathsInMix = await _fileScannerService.ScanDirectoryAsync(currentMixPath); // Skanuje rekurencyjnie
                        filesFoundInMix += imagePathsInMix.Count;
                        SimpleFileLogger.Log($"MatchModelSpecific: W folderze '{currentMixPath}' znaleziono {imagePathsInMix.Count} obrazów.");

                        foreach (var imgPathFromMix in imagePathsInMix)
                        {
                            token.ThrowIfCancellationRequested();
                            var sourceEntry = await _imageMetadataService.ExtractMetadataAsync(imgPathFromMix);
                            if (sourceEntry == null)
                            {
                                SimpleFileLogger.LogWarning($"MatchModelSpecific: Nie udało się załadować metadanych dla: {imgPathFromMix}, pomijam.");
                                continue;
                            }

                            var sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceEntry);
                            if (sourceEmbedding == null)
                            {
                                SimpleFileLogger.LogWarning($"MatchModelSpecific: Nie udało się załadować embeddingu dla: {sourceEntry.FilePath}, pomijam.");
                                continue;
                            }
                            filesWithEmbeddings++;
                            token.ThrowIfCancellationRequested();

                            var bestSuggestionForThisSourceImage = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName);

                            if (bestSuggestionForThisSourceImage != null)
                            {
                                CategoryProfile targetProfile = bestSuggestionForThisSourceImage.Item1;
                                double similarityToCentroid = bestSuggestionForThisSourceImage.Item2;

                                var (proposedMove, wasActionAutoHandled) = await ProcessDuplicateOrSuggestNewAsync(
                                    sourceEntry,
                                    targetProfile,
                                    similarityToCentroid,
                                    modelPath,
                                    sourceEmbedding,
                                    token);

                                if (wasActionAutoHandled)
                                {
                                    autoActionsCount++;
                                }
                                else if (proposedMove != null)
                                {
                                    movesForSuggestionWindow.Add(proposedMove);
                                    suggestionsForWindowCount++;
                                }
                            }
                            else
                            {
                                SimpleFileLogger.Log($"MatchModelSpecific: Brak sugestii kategorii dla '{sourceEntry.FilePath}' (model: {modelVM.ModelName}).");
                            }
                        }
                    }
                    else
                    {
                        SimpleFileLogger.LogWarning($"MatchModelSpecific: Folder źródłowy '{currentMixPath}' nie istnieje.");
                    }
                }
                token.ThrowIfCancellationRequested();
                SimpleFileLogger.Log($"MatchModelSpecific dla '{modelVM.ModelName}': Podsumowanie - Znaleziono w Mix: {filesFoundInMix}, z embeddingami: {filesWithEmbeddings}. Akcje automatyczne: {autoActionsCount}. Sugestie do okna: {suggestionsForWindowCount}.");

                _lastModelSpecificSuggestions = new List<Models.ProposedMove>(movesForSuggestionWindow);
                _lastScannedModelNameForSuggestions = modelVM.ModelName;
                RefreshPendingSuggestionCountsFromCache();

                StatusMessage = $"Dla '{modelVM.ModelName}': {autoActionsCount} akcji auto., {modelVM.PendingSuggestionsCount} sugestii do przejrzenia.";

                if (movesForSuggestionWindow.Any())
                {
                    bool? dialogOutcome = false;
                    List<Models.ProposedMove> approvedMoves = new List<Models.ProposedMove>();
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var previewVM = new PreviewChangesViewModel(movesForSuggestionWindow, SuggestionSimilarityThreshold);
                        var previewWindow = new PreviewChangesWindow { DataContext = previewVM, Owner = Application.Current.MainWindow };
                        if (previewWindow is PreviewChangesWindow typedWin) typedWin.SetCloseAction(previewVM);

                        dialogOutcome = previewWindow.ShowDialog();
                        if (dialogOutcome == true)
                        {
                            approvedMoves = previewVM.GetApprovedMoves();
                        }
                    }); // Koniec Dispatcher.InvokeAsync
                    token.ThrowIfCancellationRequested();
                    if (dialogOutcome == true && approvedMoves.Any())
                    {
                        _isRefreshingProfilesPostMove = true; // Ustaw flagę przed obsłużeniem ruchów
                        await InternalHandleApprovedMovesAsync(approvedMoves, modelVM, null, token);
                        _isRefreshingProfilesPostMove = false; // Zresetuj flagę
                        // Po InternalHandleApprovedMovesAsync, które może zmienić profile, odśwież liczniki
                        ClearModelSpecificSuggestionsCache(); // Wyczyść cache sugestii, bo pliki zostały przeniesione
                        RefreshPendingSuggestionCountsFromCache(); // To odświeży UI na podstawie pustego cache'u (czyli wyzeruje)
                    }
                    else if (dialogOutcome == false) // Użytkownik kliknął Anuluj w oknie sugestii
                    {
                        StatusMessage = $"Anulowano zmiany sugestii dla '{modelVM.ModelName}'.";
                        // Cache _lastModelSpecificSuggestions i liczniki pozostają, bo użytkownik może chcieć wrócić
                    }
                } // Koniec if (movesForSuggestionWindow.Any())
                else if (autoActionsCount > 0)
                {
                    MessageBox.Show($"Zakończono automatyczne operacje dla '{modelVM.ModelName}'. Wykonano {autoActionsCount} akcji. Brak dodatkowych sugestii do przejrzenia.", "Operacje Automatyczne Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Brak nowych sugestii lub automatycznych akcji dla '{modelVM.ModelName}'. Sprawdź, czy foldery 'Mix' zawierają obrazy dla tej modelki.", "Brak Zmian", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }, "Dopasowywanie dla modelki");

        private Task ExecuteSuggestImagesAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                ClearModelSpecificSuggestionsCache(); // Globalne sugestie, więc czyścimy poprzedni cache dla konkretnej modelki
                token.ThrowIfCancellationRequested();
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe (np. 'Mix') w ustawieniach."; MessageBox.Show(StatusMessage, "Brak Folderów Źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();

                var movesForSuggestionWindow = new List<Models.ProposedMove>();
                await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var mVM_ui in HierarchicalProfilesList) { mVM_ui.PendingSuggestionsCount = 0; foreach (var cp_ui in mVM_ui.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; } });
                token.ThrowIfCancellationRequested();

                var allModelsCurrentlyInList = HierarchicalProfilesList.ToList(); // Pracuj na kopii listy modeli
                int totalFilesFound = 0, totalFilesWithEmbeddings = 0, totalAutoActions = 0, totalSuggestionsForWindow = 0;

                foreach (var modelVM in allModelsCurrentlyInList)
                {
                    token.ThrowIfCancellationRequested();
                    string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName);
                    if (!Directory.Exists(modelPath) || !modelVM.HasCharacterProfiles)
                    {
                        SimpleFileLogger.Log($"ExecuteSuggestImagesAsync: Pomijanie modelki '{modelVM.ModelName}' - folder nie istnieje lub brak profili postaci.");
                        continue;
                    }
                    SimpleFileLogger.Log($"ExecuteSuggestImagesAsync: Przetwarzanie modelki '{modelVM.ModelName}'.");

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
                                var sourceEntry = await _imageMetadataService.ExtractMetadataAsync(imgPathFromMix);
                                if (sourceEntry == null) continue;

                                var sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceEntry);
                                if (sourceEmbedding == null) continue;
                                totalFilesWithEmbeddings++;
                                token.ThrowIfCancellationRequested();

                                var bestSuggestionForThisSourceImage = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName); // Sugestie tylko w obrębie bieżącej modelki

                                if (bestSuggestionForThisSourceImage != null)
                                {
                                    CategoryProfile targetProfile = bestSuggestionForThisSourceImage.Item1;
                                    double similarityToCentroid = bestSuggestionForThisSourceImage.Item2;

                                    var (proposedMove, wasActionAutoHandled) = await ProcessDuplicateOrSuggestNewAsync(
                                        sourceEntry,
                                        targetProfile,
                                        similarityToCentroid,
                                        modelPath,
                                        sourceEmbedding,
                                        token);

                                    if (wasActionAutoHandled)
                                    {
                                        totalAutoActions++;
                                    }
                                    else if (proposedMove != null)
                                    {
                                        movesForSuggestionWindow.Add(proposedMove);
                                        totalSuggestionsForWindow++;
                                    }
                                }
                            }
                        }
                    }
                } // Koniec pętli po modelkach
                token.ThrowIfCancellationRequested();
                SimpleFileLogger.Log($"ExecuteSuggestImagesAsync: Podsumowanie globalne - Znaleziono: {totalFilesFound}, Z embeddingami: {totalFilesWithEmbeddings}, Akcje auto: {totalAutoActions}, Sugestie do okna: {totalSuggestionsForWindow}.");

                StatusMessage = $"Globalne sugestie: {totalAutoActions} akcji auto., {totalSuggestionsForWindow} sugestii do przejrzenia.";

                if (movesForSuggestionWindow.Any())
                {
                    bool? dialogOutcome = false; List<Models.ProposedMove> approvedMoves = new List<Models.ProposedMove>();
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var previewVM = new PreviewChangesViewModel(movesForSuggestionWindow, SuggestionSimilarityThreshold);
                        var previewWindow = new PreviewChangesWindow { DataContext = previewVM, Owner = Application.Current.MainWindow };
                        if (previewWindow is PreviewChangesWindow typedWin) typedWin.SetCloseAction(previewVM);
                        dialogOutcome = previewWindow.ShowDialog();
                        if (dialogOutcome == true) approvedMoves = previewVM.GetApprovedMoves();
                    });
                    token.ThrowIfCancellationRequested();
                    if (dialogOutcome == true && approvedMoves.Any())
                    {
                        _isRefreshingProfilesPostMove = true;
                        await InternalHandleApprovedMovesAsync(approvedMoves, null, null, token); // null, null bo to globalne
                        _isRefreshingProfilesPostMove = false;
                        // Po InternalHandleApprovedMovesAsync, które może zmienić profile, odśwież wszystko
                        await InternalExecuteLoadProfilesAsync(token); // Pełne przeładowanie profili i odświeżenie UI
                    }
                    else if (dialogOutcome == false) StatusMessage = "Anulowano zmiany dla globalnych sugestii.";
                    // Po globalnych sugestiach zawsze czyścimy cache specyficzny dla modelu, bo nie wiadomo, który model był "ostatni"
                    ClearModelSpecificSuggestionsCache();
                }
                else if (totalAutoActions > 0)
                {
                    MessageBox.Show($"Zakończono automatyczne operacje. Wykonano {totalAutoActions} akcji. Brak dodatkowych sugestii do przejrzenia.", "Operacje Automatyczne Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Brak nowych sugestii lub automatycznych akcji w całym folderze biblioteki.", "Brak Zmian", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }, "Globalne wyszukiwanie sugestii");

        private Task ExecuteCheckCharacterSuggestionsAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                var charProfileForSuggestions = (parameter as CategoryProfile) ?? SelectedProfile;
                if (charProfileForSuggestions == null) { StatusMessage = "Błąd: Wybierz profil postaci z listy."; MessageBox.Show(StatusMessage, "Brak Wyboru Postaci", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                SimpleFileLogger.Log($"CheckCharacterSuggestions dla '{charProfileForSuggestions.CategoryName}'. Token: {token.GetHashCode()}");
                token.ThrowIfCancellationRequested();
                string modelName = _profileService.GetModelNameFromCategory(charProfileForSuggestions.CategoryName);

                List<Models.ProposedMove> suggestionsForThisCharacter;

                // Sprawdź, czy mamy świeże, przefiltrowane sugestie dla całej modelki, które dotyczą tej postaci
                if (_lastScannedModelNameForSuggestions == modelName && _lastModelSpecificSuggestions.Any())
                {
                    suggestionsForThisCharacter = _lastModelSpecificSuggestions
                        .Where(m => m.TargetCategoryProfileName.Equals(charProfileForSuggestions.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                                    m.Similarity >= SuggestionSimilarityThreshold) // Ponowne filtrowanie progiem, na wypadek zmiany
                        .ToList();
                    SimpleFileLogger.Log($"CheckCharacterSuggestions: Użyto cache'owanych sugestii ({_lastModelSpecificSuggestions.Count}) dla modelki '{modelName}'. Przefiltrowano do {suggestionsForThisCharacter.Count} dla postaci '{charProfileForSuggestions.CategoryName}'.");
                }
                else // Jeśli nie ma cache'u dla modelki lub dotyczy innej, przeskanuj foldery Mix dla tej konkretnej postaci
                {
                    SimpleFileLogger.Log($"CheckCharacterSuggestions: Brak cache'u sugestii dla modelki '{modelName}' lub dotyczy innej. Rozpoczynanie skanowania folderów Mix.");
                    suggestionsForThisCharacter = new List<Models.ProposedMove>();
                    var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                    string modelPath = Path.Combine(LibraryRootPath, modelName);
                    token.ThrowIfCancellationRequested();

                    if (Directory.Exists(modelPath) && mixedFolders.Any() && charProfileForSuggestions.CentroidEmbedding != null)
                    {
                        int filesFoundInMix = 0, filesWithEmbeddings = 0, autoActionsCount = 0;
                        foreach (var mixFolderName in mixedFolders)
                        {
                            token.ThrowIfCancellationRequested();
                            string currentMixPath = Path.Combine(modelPath, mixFolderName);
                            if (Directory.Exists(currentMixPath))
                            {
                                var imagePathsInMix = await _fileScannerService.ScanDirectoryAsync(currentMixPath);
                                filesFoundInMix += imagePathsInMix.Count;

                                foreach (var imgPathFromMix in imagePathsInMix)
                                {
                                    token.ThrowIfCancellationRequested();
                                    var sourceEntry = await _imageMetadataService.ExtractMetadataAsync(imgPathFromMix);
                                    if (sourceEntry == null) continue;

                                    var sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceEntry);
                                    if (sourceEmbedding == null) continue;
                                    filesWithEmbeddings++;
                                    token.ThrowIfCancellationRequested();

                                    // Tutaj similarityToCentroid jest kluczowe dla tej konkretnej postaci
                                    double similarityToCurrentCharacterCentroid = Utils.MathUtils.CalculateCosineSimilarity(sourceEmbedding, charProfileForSuggestions.CentroidEmbedding);

                                    // Użyj ProcessDuplicateOrSuggestNewAsync, przekazując charProfileForSuggestions jako targetProfile
                                    var (proposedMove, wasActionAutoHandled) = await ProcessDuplicateOrSuggestNewAsync(
                                        sourceEntry,
                                        charProfileForSuggestions, // Cel to konkretnie ta postać
                                        similarityToCurrentCharacterCentroid, // Użyj podobieństwa do jej centroidu
                                        modelPath,
                                        sourceEmbedding,
                                        token);

                                    if (wasActionAutoHandled)
                                    {
                                        autoActionsCount++;
                                    }
                                    else if (proposedMove != null && proposedMove.TargetCategoryProfileName == charProfileForSuggestions.CategoryName) // Upewnij się, że sugestia jest dla tej postaci
                                    {
                                        suggestionsForThisCharacter.Add(proposedMove);
                                    }
                                }
                            }
                        }
                        SimpleFileLogger.Log($"CheckCharacterSuggestions: Skanowanie Mix dla '{charProfileForSuggestions.CategoryName}' - Znaleziono: {filesFoundInMix}, Z embeddingami: {filesWithEmbeddings}, Akcje auto: {autoActionsCount}, Sugestie: {suggestionsForThisCharacter.Count}.");
                    }
                    StatusMessage = $"Skanowanie dla '{charProfileForSuggestions.CategoryName}': {suggestionsForThisCharacter.Count} sugestii."; token.ThrowIfCancellationRequested();

                    // Zaktualizuj cache _lastModelSpecificSuggestions, jeśli przeskanowaliśmy od nowa
                    // To jest trochę bardziej skomplikowane, bo skanowaliśmy tylko pod kątem jednej postaci.
                    // Na razie nie będziemy nadpisywać _lastModelSpecificSuggestions tutaj, aby nie stracić sugestii dla innych postaci tej modelki.
                    // RefreshPendingSuggestionCountsFromCache() odświeży UI dla tej postaci na podstawie `suggestionsForThisCharacter`.
                }
                token.ThrowIfCancellationRequested();

                if (suggestionsForThisCharacter.Any())
                {
                    bool? outcome = false; List<Models.ProposedMove> approved = new List<Models.ProposedMove>();
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var vm = new PreviewChangesViewModel(suggestionsForThisCharacter, SuggestionSimilarityThreshold);
                        var win = new PreviewChangesWindow { DataContext = vm, Owner = Application.Current.MainWindow };
                        if (win is PreviewChangesWindow typedWin) typedWin.SetCloseAction(vm);
                        outcome = win.ShowDialog();
                        if (outcome == true) approved = vm.GetApprovedMoves();
                    });
                    token.ThrowIfCancellationRequested();
                    if (outcome == true && approved.Any())
                    {
                        _isRefreshingProfilesPostMove = true;
                        // Znajdź ModelDisplayViewModel dla tej postaci
                        var parentModelVM = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName == modelName);
                        await InternalHandleApprovedMovesAsync(approved, parentModelVM, charProfileForSuggestions, token);
                        _isRefreshingProfilesPostMove = false;
                        // Po InternalHandleApprovedMovesAsync odśwież liczniki dla tej postaci/modelki
                        // Możemy wyczyścić _lastModelSpecificSuggestions dla tej modelki, bo stan się zmienił
                        if (_lastScannedModelNameForSuggestions == modelName) _lastModelSpecificSuggestions.RemoveAll(m => approved.Any(ap => ap.SourceImage.FilePath == m.SourceImage.FilePath));
                        RefreshPendingSuggestionCountsFromCache();
                    }
                    else if (outcome == false) StatusMessage = $"Anulowano sugestie dla '{charProfileForSuggestions.CategoryName}'.";
                }
                else
                {
                    MessageBox.Show($"Brak nowych sugestii dla postaci '{charProfileForSuggestions.CategoryName}'.", "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                // Odśwież licznik dla tej konkretnej postaci w UI, nawet jeśli nie było sugestii (mogły być akcje auto)
                var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));
                if (modelToUpdate != null)
                {
                    var charProfileInUI = modelToUpdate.CharacterProfiles.FirstOrDefault(cp => cp.CategoryName == charProfileForSuggestions.CategoryName);
                    if (charProfileInUI != null)
                    {
                        // Jeśli skanowaliśmy od nowa, suggestionsForThisCharacter zawiera aktualne sugestie
                        // Jeśli braliśmy z cache'a, to też
                        charProfileInUI.PendingSuggestionsCount = suggestionsForThisCharacter.Count;
                        modelToUpdate.PendingSuggestionsCount = modelToUpdate.CharacterProfiles.Sum(cp => cp.PendingSuggestionsCount); // Zaktualizuj sumę dla modelki
                    }
                }

            }, "Sprawdzanie sugestii dla postaci");

        private void RefreshPendingSuggestionCountsFromCache() // Odświeża na podstawie _lastModelSpecificSuggestions
        {
            Application.Current.Dispatcher.Invoke(() => {
                // Najpierw wyzeruj wszystkie liczniki
                foreach (var mVM_iter in HierarchicalProfilesList)
                {
                    mVM_iter.PendingSuggestionsCount = 0;
                    foreach (var cp_iter in mVM_iter.CharacterProfiles)
                    {
                        cp_iter.PendingSuggestionsCount = 0;
                    }
                }

                // Jeśli mamy dane dla konkretnej modelki w _lastModelSpecificSuggestions, użyj ich
                if (!string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) && _lastModelSpecificSuggestions.Any())
                {
                    var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(_lastScannedModelNameForSuggestions, StringComparison.OrdinalIgnoreCase));
                    if (modelToUpdate != null)
                    {
                        int totalForModel = 0;
                        foreach (var cp_ui in modelToUpdate.CharacterProfiles)
                        {
                            // Liczymy sugestie z _lastModelSpecificSuggestions, które pasują do tej postaci i progu
                            cp_ui.PendingSuggestionsCount = _lastModelSpecificSuggestions.Count(
                                m => m.TargetCategoryProfileName.Equals(cp_ui.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                                     m.Similarity >= SuggestionSimilarityThreshold); // Ponowne sprawdzenie progu
                            totalForModel += cp_ui.PendingSuggestionsCount;
                        }
                        modelToUpdate.PendingSuggestionsCount = totalForModel;
                        SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache: Zaktualizowano liczniki dla modelki '{_lastScannedModelNameForSuggestions}'. Suma: {totalForModel}.");
                    }
                    else
                    {
                        SimpleFileLogger.LogWarning($"RefreshPendingSuggestionCountsFromCache: Nie znaleziono ModelDisplayViewModel dla '{_lastScannedModelNameForSuggestions}' do aktualizacji liczników.");
                    }
                }
                else
                {
                    SimpleFileLogger.Log("RefreshPendingSuggestionCountsFromCache: Brak danych w _lastModelSpecificSuggestions lub _lastScannedModelNameForSuggestions jest pusty. Wszystkie liczniki wyzerowane.");
                }
            });
        }

        private async Task InternalHandleApprovedMovesAsync(List<Models.ProposedMove> approvedMoves, ModelDisplayViewModel? specificModelVM, CategoryProfile? specificCharacterProfile, CancellationToken token)
        {
            int successfulMoves = 0, copyErrors = 0, deleteErrors = 0, skippedQuality = 0, skippedOther = 0;
            var affectedProfileNamesForRefresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processedSourcePaths = new List<string>(); // Ścieżki plików źródłowych, które zostały przetworzone (przeniesione/usunięte)

            foreach (var move in approvedMoves)
            {
                token.ThrowIfCancellationRequested();
                string sourcePath = move.SourceImage.FilePath;
                string targetPath = move.ProposedTargetPath;
                var actionType = move.Action;
                bool operationSuccessful = false;
                bool deleteSourceAfterCopy = false;

                try
                {
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    {
                        SimpleFileLogger.LogWarning($"[HandleApproved] Plik źródłowy nie istnieje lub ścieżka jest pusta: '{sourcePath}'. Pomijanie.");
                        skippedOther++;
                        continue;
                    }
                    string targetDirectory = Path.GetDirectoryName(targetPath);
                    if (string.IsNullOrEmpty(targetDirectory))
                    {
                        SimpleFileLogger.LogWarning($"[HandleApproved] Nie można określić folderu docelowego dla: '{targetPath}'. Pomijanie.");
                        skippedOther++;
                        continue;
                    }
                    Directory.CreateDirectory(targetDirectory); // Upewnij się, że folder docelowy istnieje

                    switch (actionType)
                    {
                        case ProposedMoveActionType.CopyNew:
                            if (File.Exists(targetPath)) // Powinno być obsłużone przez ConflictKeepBoth, ale dla bezpieczeństwa
                            {
                                targetPath = GenerateUniqueTargetPath(targetDirectory, Path.GetFileName(sourcePath), "_new_approved");
                                SimpleFileLogger.Log($"[HandleApproved] CopyNew: Plik docelowy '{move.ProposedTargetPath}' już istniał. Zmieniono na '{targetPath}'.");
                            }
                            await Task.Run(() => File.Copy(sourcePath, targetPath, false), token);
                            operationSuccessful = true;
                            deleteSourceAfterCopy = true;
                            SimpleFileLogger.Log($"[HandleApproved] CopyNew: Skopiowano '{sourcePath}' do '{targetPath}'.");
                            break;

                        case ProposedMoveActionType.OverwriteExisting:
                            await Task.Run(() => File.Copy(sourcePath, targetPath, true), token); // true for overwrite
                            operationSuccessful = true;
                            deleteSourceAfterCopy = true;
                            SimpleFileLogger.Log($"[HandleApproved] OverwriteExisting: Nadpisano '{targetPath}' plikiem '{sourcePath}'.");
                            break;

                        case ProposedMoveActionType.KeepExistingDeleteSource:
                            // Plik docelowy pozostaje bez zmian, usuwamy tylko źródłowy.
                            // Upewnijmy się, że plik źródłowy nie jest tym samym plikiem co docelowy (choć logika powinna to wykluczać).
                            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                deleteSourceAfterCopy = true; // Ustawiamy flagę do usunięcia źródła
                                operationSuccessful = true; // Sama operacja "zachowaj istniejący" jest "sukcesem"
                                SimpleFileLogger.Log($"[HandleApproved] KeepExistingDeleteSource: Zachowano istniejący '{targetPath}'. Źródło '{sourcePath}' zostanie usunięte.");
                            }
                            else
                            {
                                SimpleFileLogger.LogWarning($"[HandleApproved] KeepExistingDeleteSource: Plik źródłowy i docelowy to ten sam plik ('{sourcePath}'). Nic do zrobienia.");
                                skippedQuality++; // Lub inny licznik, jeśli to oczekiwane
                            }
                            break;

                        case ProposedMoveActionType.ConflictKeepBoth:
                            // Zgodnie z definicją, jeśli użytkownik zatwierdził "ConflictKeepBoth", oznacza to, że chce skopiować plik źródłowy,
                            // a plik docelowy (o tej samej nazwie) już tam jest i nie jest duplikatem graficznym.
                            // Trzeba wygenerować unikalną nazwę dla kopiowanego pliku.
                            string newTargetPathForConflict = GenerateUniqueTargetPath(targetDirectory, Path.GetFileName(sourcePath), "_conflict_approved");
                            await Task.Run(() => File.Copy(sourcePath, newTargetPathForConflict, false), token);
                            targetPath = newTargetPathForConflict; // Zaktualizuj targetPath na faktycznie użyty
                            operationSuccessful = true;
                            deleteSourceAfterCopy = true;
                            SimpleFileLogger.Log($"[HandleApproved] ConflictKeepBoth: Skopiowano '{sourcePath}' do '{targetPath}' (rozwiązanie konfliktu).");
                            break;
                    }
                    token.ThrowIfCancellationRequested();

                    if (operationSuccessful)
                    {
                        successfulMoves++;
                        processedSourcePaths.Add(sourcePath);
                        affectedProfileNamesForRefresh.Add(move.TargetCategoryProfileName); // Dodaj profil docelowy do odświeżenia

                        if (deleteSourceAfterCopy)
                        {
                            try
                            {
                                await Task.Run(() => File.Delete(sourcePath), token);
                                SimpleFileLogger.Log($"[HandleApproved] Usunięto plik źródłowy: '{sourcePath}'.");
                                // Plik źródłowy został usunięty, więc musimy go usunąć ze wszystkich profili, które go zawierały.
                                // A do profilu docelowego dodać nowy targetPath.
                                await HandleFileMovedOrDeletedUpdateProfilesAsync(sourcePath, targetPath, move.TargetCategoryProfileName, token);

                            }
                            catch (Exception exDelete)
                            {
                                deleteErrors++;
                                SimpleFileLogger.LogError($"[HandleApproved] Błąd podczas usuwania pliku źródłowego '{sourcePath}'.", exDelete);
                            }
                        }
                        else // Jeśli nie usuwamy źródła (np. specjalny przypadek KeepExistingDeleteSource gdzie źródło == cel)
                        {
                            // Jeśli targetPath został zmieniony (np. przy ConflictKeepBoth), a źródło nie jest usuwane,
                            // to stary sourcePath pozostaje w swoich profilach, a nowy targetPath jest dodawany do profilu docelowego.
                            // Wymaga to bardziej złożonej logiki w HandleFileMovedOrDeletedUpdateProfilesAsync lub tutaj.
                            // Na razie zakładamy, że jeśli operacja była udana, to plik trafił do targetPath.
                            await HandleFileMovedOrDeletedUpdateProfilesAsync(null, targetPath, move.TargetCategoryProfileName, token); // Tylko dodanie do nowego
                        }
                    }
                }
                catch (OperationCanceledException) { throw; } // Przekaż dalej
                catch (Exception exCopy)
                {
                    copyErrors++;
                    SimpleFileLogger.LogError($"[HandleApproved] Błąd podczas przetwarzania ruchu dla '{sourcePath}' -> '{targetPath}'. Akcja: {actionType}.", exCopy);
                }
            } // Koniec pętli po approvedMoves
            token.ThrowIfCancellationRequested();

            // Usuń przetworzone pliki z cache'u sugestii dla konkretnej modelki, jeśli dotyczy
            if (specificModelVM != null && !string.IsNullOrWhiteSpace(_lastScannedModelNameForSuggestions) &&
                _lastScannedModelNameForSuggestions.Equals(specificModelVM.ModelName, StringComparison.OrdinalIgnoreCase) &&
                processedSourcePaths.Any())
            {
                _lastModelSpecificSuggestions.RemoveAll(s => processedSourcePaths.Contains(s.SourceImage.FilePath, StringComparer.OrdinalIgnoreCase));
                SimpleFileLogger.Log($"[HandleApproved] Usunięto {processedSourcePaths.Count} przetworzonych sugestii z cache'u dla modelki '{specificModelVM.ModelName}'.");
            }
            else if (specificModelVM == null && processedSourcePaths.Any()) // Globalne sugestie
            {
                // Jeśli to były globalne sugestie, nie mamy _lastModelSpecificSuggestions do czyszczenia w ten sposób.
                // ClearModelSpecificSuggestionsCache() jest wywoływane po globalnych.
            }


            // Odświeżanie profili powinno być już obsłużone przez HandleFileMovedOrDeletedUpdateProfilesAsync.
            // Ale na wszelki wypadek, jeśli jakieś profile zostały zmienione, a nie zostały odświeżone.
            if (affectedProfileNamesForRefresh.Any())
            {
                SimpleFileLogger.Log($"[HandleApproved] Końcowe odświeżanie profili (jeśli potrzebne) po obsłużeniu zatwierdzonych ruchów.");
                // InternalExecuteLoadProfilesAsync jest tutaj bezpieczniejsze, bo odświeży całe UI
                // _isRefreshingProfilesPostMove powinno być ustawione przed tą metodą.
                // await InternalRefreshProfilesAsync(affectedProfileNamesForRefresh.ToList(), token);
            }

            StatusMessage = $"Zakończono zatwierdzone operacje: {successfulMoves} pomyślnie, {skippedQuality} pom. (jakość), {skippedOther} pom. (inne), {copyErrors} bł. kopiowania, {deleteErrors} bł. usuwania.";
            MessageBox.Show(StatusMessage, "Operacja Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task InternalRefreshProfilesAsync(List<string> profileNamesToRefresh, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(LibraryRootPath) || !Directory.Exists(LibraryRootPath))
            {
                SimpleFileLogger.LogWarning("[InternalRefreshProfilesAsync] Ścieżka biblioteki nie jest ustawiona lub nie istnieje. Przerywanie odświeżania.");
                return;
            }
            if (!profileNamesToRefresh.Any())
            {
                SimpleFileLogger.Log("[InternalRefreshProfilesAsync] Brak profili na liście do odświeżenia.");
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = "Odświeżanie zmodyfikowanych profili...");
            SimpleFileLogger.Log($"[InternalRefreshProfilesAsync] Rozpoczęto odświeżanie {profileNamesToRefresh.Count} profili: {string.Join(", ", profileNamesToRefresh)}");
            bool anyProfileActuallyChanged = false;
            token.ThrowIfCancellationRequested();

            foreach (var profileName in profileNamesToRefresh)
            {
                token.ThrowIfCancellationRequested();
                var (modelName, characterNamePart) = ParseCategoryName(profileName);
                // Ścieżka do folderu, gdzie powinny być pliki dla tego profilu
                string characterFolderPath = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(characterNamePart));

                var entriesForProfile = new List<ImageFileEntry>();
                if (Directory.Exists(characterFolderPath))
                {
                    List<string> filesInCharacterFolder;
                    try
                    {
                        filesInCharacterFolder = Directory.GetFiles(characterFolderPath)
                                                    .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f)))
                                                    .ToList();
                    }
                    catch (Exception ex)
                    {
                        filesInCharacterFolder = new List<string>();
                        SimpleFileLogger.LogError($"[InternalRefreshProfilesAsync] Błąd odczytu plików z folderu '{characterFolderPath}' dla profilu '{profileName}'.", ex);
                    }

                    foreach (var filePath in filesInCharacterFolder)
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = await _imageMetadataService.ExtractMetadataAsync(filePath);
                        if (entry != null) entriesForProfile.Add(entry);
                    }
                }
                else
                {
                    SimpleFileLogger.LogWarning($"[InternalRefreshProfilesAsync] Folder '{characterFolderPath}' dla profilu '{profileName}' nie istnieje. Profil zostanie wyczyszczony/usunięty, jeśli istniał.");
                }

                token.ThrowIfCancellationRequested();
                // GenerateProfileAsync obsłuży sytuację, gdy entriesForProfile jest puste (wyczyści profil)
                await _profileService.GenerateProfileAsync(profileName, entriesForProfile);
                anyProfileActuallyChanged = true; // Zakładamy, że jeśli profil był na liście, to jego stan się zmienił lub wymagał weryfikacji
                SimpleFileLogger.Log($"[InternalRefreshProfilesAsync] Profil '{profileName}' został ponownie wygenerowany/zaktualizowany na podstawie zawartości folderu '{characterFolderPath}'.");
            }
            token.ThrowIfCancellationRequested();

            if (anyProfileActuallyChanged || _isRefreshingProfilesPostMove) // _isRefreshingProfilesPostMove jest flagą globalną, która mogła być ustawiona wcześniej
            {
                SimpleFileLogger.Log("[InternalRefreshProfilesAsync] Co najmniej jeden profil został zmodyfikowany lub flaga odświeżania była aktywna. Ładowanie wszystkich profili do UI.");
                await InternalExecuteLoadProfilesAsync(token); // Pełne przeładowanie i odświeżenie UI
            }
            else
            {
                await Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = "Profile są aktualne (nie wymagały odświeżenia).");
                SimpleFileLogger.Log("[InternalRefreshProfilesAsync] Żaden profil nie wymagał faktycznej zmiany po odświeżeniu.");
            }
        }

        private string GenerateUniqueTargetPath(string targetDirectory, string originalFileNameWithExtension, string suffixIfConflict = "_conflict")
        {
            string baseName = Path.GetFileNameWithoutExtension(originalFileNameWithExtension);
            string extension = Path.GetExtension(originalFileNameWithExtension);
            string finalPath = Path.Combine(targetDirectory, originalFileNameWithExtension);
            int counter = 1;

            // Pętla do znajdowania unikalnej nazwy, jeśli plik już istnieje
            while (File.Exists(finalPath))
            {
                string newFileName = $"{baseName}{suffixIfConflict}{counter}{extension}";
                finalPath = Path.Combine(targetDirectory, newFileName);
                counter++;
                if (counter > 9999) // Zabezpieczenie przed nieskończoną pętlą
                {
                    // Jeśli po wielu próbach nadal jest konflikt, dodaj GUID
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
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Wybierz modelkę z listy."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();
                if (MessageBox.Show($"Czy na pewno chcesz usunąć całą modelkę '{modelVM.ModelName}' wraz ze wszystkimi jej profilami postaci?\nTa operacja usunie definicje profili oraz plik JSON tej modelki, ale NIE usunie plików graficznych z dysku.", "Potwierdź usunięcie modelki", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    if (await _profileService.RemoveAllProfilesForModelAsync(modelVM.ModelName))
                    {
                        StatusMessage = $"Modelka '{modelVM.ModelName}' i jej profile zostały usunięte.";
                        if (_lastScannedModelNameForSuggestions == modelVM.ModelName) ClearModelSpecificSuggestionsCache(); // Wyczyść cache sugestii dla tej modelki

                        // Sprawdź, czy aktualnie wybrany profil należy do usuwanej modelki
                        if (SelectedProfile != null && _profileService.GetModelNameFromCategory(SelectedProfile.CategoryName) == modelVM.ModelName)
                        {
                            SelectedProfile = null; // Odznacz profil
                        }
                        await InternalExecuteLoadProfilesAsync(token); // Odśwież listę w UI
                    }
                    else
                    {
                        StatusMessage = $"Nie udało się usunąć modelki '{modelVM.ModelName}'. Sprawdź logi.";
                        // Mimo to odświeżmy, bo mogło dojść do częściowego usunięcia
                        await InternalExecuteLoadProfilesAsync(token);
                    }
                }
            }, "Usuwanie całej modelki");

        private Task ExecuteAnalyzeModelForSplittingAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Wybierz modelkę z listy."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                token.ThrowIfCancellationRequested();
                int profilesMarkedForSplit = 0;

                // Resetuj flagi HasSplitSuggestion dla UI
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.HasSplitSuggestion = false;
                });

                var characterProfilesForModel = modelVM.CharacterProfiles.ToList(); // Pracuj na kopii listy
                foreach (var characterProfile in characterProfilesForModel)
                {
                    token.ThrowIfCancellationRequested();
                    const int minImagesForConsideration = 10; // Minimum obrazów w profilu, aby go analizować
                    const int minImagesToSuggestSplit = 20;  // Minimum obrazów, aby faktycznie zasugerować podział

                    if (characterProfile.SourceImagePaths == null || characterProfile.SourceImagePaths.Count < minImagesForConsideration)
                    {
                        SimpleFileLogger.Log($"AnalyzeModelForSplitting: Profil '{characterProfile.CategoryName}' ma mniej niż {minImagesForConsideration} obrazów ({characterProfile.SourceImagePaths?.Count ?? 0}), pomijanie.");
                        continue;
                    }

                    var embeddingsForProfile = new List<float[]>();
                    var validImageEntriesForProfile = new List<ImageFileEntry>();

                    foreach (string imagePath in characterProfile.SourceImagePaths)
                    {
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(imagePath))
                        {
                            var imageEntry = await _imageMetadataService.ExtractMetadataAsync(imagePath);
                            if (imageEntry != null)
                            {
                                var embedding = await _profileService.GetImageEmbeddingAsync(imageEntry);
                                if (embedding != null)
                                {
                                    embeddingsForProfile.Add(embedding);
                                    validImageEntriesForProfile.Add(imageEntry); // Może być potrzebne później
                                }
                            }
                        }
                    }
                    token.ThrowIfCancellationRequested();

                    if (embeddingsForProfile.Count < minImagesForConsideration)
                    {
                        SimpleFileLogger.Log($"AnalyzeModelForSplitting: Profil '{characterProfile.CategoryName}' po weryfikacji plików ma mniej niż {minImagesForConsideration} embeddingów ({embeddingsForProfile.Count}), pomijanie.");
                        continue;
                    }

                    // TODO: Tutaj powinna być bardziej zaawansowana logika analizy klastrowania embeddingów,
                    // np. K-Means dla K=2 i ocena jakości podziału (np. silhouette score).
                    // Na razie, jako placeholder, sugerujemy podział, jeśli jest wystarczająco dużo obrazów.
                    bool shouldSuggestSplit = embeddingsForProfile.Count >= minImagesToSuggestSplit;

                    // Aktualizuj flagę w UI
                    var uiCharacterProfile = modelVM.CharacterProfiles.FirstOrDefault(p => p.CategoryName == characterProfile.CategoryName);
                    if (uiCharacterProfile != null)
                    {
                        uiCharacterProfile.HasSplitSuggestion = shouldSuggestSplit;
                        if (shouldSuggestSplit) profilesMarkedForSplit++;
                    }
                    SimpleFileLogger.Log($"AnalyzeModelForSplitting: Profil '{characterProfile.CategoryName}', obrazy: {embeddingsForProfile.Count}, sugestia podziału: {shouldSuggestSplit}.");
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

                var imagesInProfile = new List<ImageFileEntry>();
                if (originalCharacterProfile.SourceImagePaths != null)
                {
                    foreach (var path in originalCharacterProfile.SourceImagePaths)
                    {
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(path))
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
                    // Zdejmij flagę, skoro jest pusty
                    var uiProfile = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == originalCharacterProfile.CategoryName);
                    if (uiProfile != null) uiProfile.HasSplitSuggestion = false;
                    return;
                }

                // Prosty podział na pół dla przykładu - w rzeczywistości powinno być oparte o klastrowanie
                var group1Images = imagesInProfile.Take(imagesInProfile.Count / 2).ToList();
                var group2Images = imagesInProfile.Skip(imagesInProfile.Count / 2).ToList();

                string modelName = _profileService.GetModelNameFromCategory(originalCharacterProfile.CategoryName);
                string baseCharacterName = _profileService.GetCharacterNameFromCategory(originalCharacterProfile.CategoryName);
                // Jeśli oryginalna nazwa postaci to "General", użyj nazwy modelki jako bazy dla nowych nazw
                if (baseCharacterName.Equals("General", StringComparison.OrdinalIgnoreCase) && originalCharacterProfile.CategoryName.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                {
                    baseCharacterName = modelName;
                }


                string suggestedName1 = $"{baseCharacterName} - Part 1";
                string suggestedName2 = $"{baseCharacterName} - Part 2";
                bool? dialogResult = false;
                SplitProfileViewModel? splitVM = null; // Aby uzyskać dostęp do nazw po zamknięciu dialogu

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    splitVM = new SplitProfileViewModel(originalCharacterProfile, group1Images, group2Images, suggestedName1, suggestedName2);
                    var splitWindow = new SplitProfileWindow { DataContext = splitVM, Owner = Application.Current.MainWindow };
                    if (splitWindow is SplitProfileWindow typedWin) typedWin.SetCloseAction(splitVM); // Ustawienie akcji zamknięcia
                    dialogResult = splitWindow.ShowDialog();
                });
                token.ThrowIfCancellationRequested();

                if (dialogResult == true && splitVM != null)
                {
                    StatusMessage = $"Podział profilu '{originalCharacterProfile.CategoryName}' został zatwierdzony. Rozpoczynanie operacji...";
                    SimpleFileLogger.Log($"SplitProfile: Zatwierdzono podział dla '{originalCharacterProfile.CategoryName}'. Nowe profile: '{splitVM.NewProfile1Name}' i '{splitVM.NewProfile2Name}'.");

                    // 1. Przygotuj dane dla nowych profili
                    string fullNewProfile1Name = $"{modelName} - {splitVM.NewProfile1Name}";
                    string fullNewProfile2Name = $"{modelName} - {splitVM.NewProfile2Name}";

                    var entriesForProfile1 = splitVM.Group1Images.Select(vm => vm.OriginalImageEntry).ToList();
                    var entriesForProfile2 = splitVM.Group2Images.Select(vm => vm.OriginalImageEntry).ToList();

                    // 2. Utwórz nowe foldery na dysku (jeśli jeszcze nie istnieją)
                    string newProfile1Path = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(splitVM.NewProfile1Name));
                    string newProfile2Path = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(splitVM.NewProfile2Name));
                    Directory.CreateDirectory(newProfile1Path);
                    Directory.CreateDirectory(newProfile2Path);
                    SimpleFileLogger.Log($"SplitProfile: Utworzono foldery: '{newProfile1Path}' i '{newProfile2Path}'.");

                    // 3. Przenieś pliki do nowych folderów i zaktualizuj ścieżki w ImageFileEntry
                    var movedFilesMappingOldToNewPath = new Dictionary<string, string>();

                    foreach (var entry in entriesForProfile1)
                    {
                        token.ThrowIfCancellationRequested();
                        string newFilePath = Path.Combine(newProfile1Path, entry.FileName);
                        try { File.Move(entry.FilePath, newFilePath); movedFilesMappingOldToNewPath[entry.FilePath] = newFilePath; entry.FilePath = newFilePath; }
                        catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd przenoszenia pliku '{entry.FilePath}' do '{newFilePath}'", ex); }
                    }
                    foreach (var entry in entriesForProfile2)
                    {
                        token.ThrowIfCancellationRequested();
                        string newFilePath = Path.Combine(newProfile2Path, entry.FileName);
                        try { File.Move(entry.FilePath, newFilePath); movedFilesMappingOldToNewPath[entry.FilePath] = newFilePath; entry.FilePath = newFilePath; }
                        catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd przenoszenia pliku '{entry.FilePath}' do '{newFilePath}'", ex); }
                    }
                    SimpleFileLogger.Log($"SplitProfile: Przeniesiono pliki do nowych folderów. Zmapowano {movedFilesMappingOldToNewPath.Count} plików.");

                    // 4. Wygeneruj nowe profile
                    await _profileService.GenerateProfileAsync(fullNewProfile1Name, entriesForProfile1);
                    SimpleFileLogger.Log($"SplitProfile: Wygenerowano profil '{fullNewProfile1Name}'.");
                    token.ThrowIfCancellationRequested();
                    await _profileService.GenerateProfileAsync(fullNewProfile2Name, entriesForProfile2);
                    SimpleFileLogger.Log($"SplitProfile: Wygenerowano profil '{fullNewProfile2Name}'.");
                    token.ThrowIfCancellationRequested();

                    // 5. Usuń stary profil
                    await _profileService.RemoveProfileAsync(originalCharacterProfile.CategoryName);
                    SimpleFileLogger.Log($"SplitProfile: Usunięto stary profil '{originalCharacterProfile.CategoryName}'.");

                    // 6. Usuń stary folder, jeśli jest pusty (ostrożnie!)
                    string oldCharacterPath = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(baseCharacterName));
                    try
                    {
                        if (Directory.Exists(oldCharacterPath) && !Directory.EnumerateFileSystemEntries(oldCharacterPath).Any())
                        {
                            Directory.Delete(oldCharacterPath);
                            SimpleFileLogger.Log($"SplitProfile: Usunięto pusty stary folder '{oldCharacterPath}'.");
                        }
                        else if (Directory.Exists(oldCharacterPath))
                        {
                            SimpleFileLogger.LogWarning($"SplitProfile: Stary folder '{oldCharacterPath}' nie został usunięty, ponieważ nie jest pusty.");
                        }
                    }
                    catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd podczas próby usunięcia starego folderu '{oldCharacterPath}'", ex); }


                    StatusMessage = $"Profil '{originalCharacterProfile.CategoryName}' został podzielony na '{fullNewProfile1Name}' i '{fullNewProfile2Name}'.";
                    // Zdejmij flagę sugestii ze starego profilu (który już nie istnieje, ale obiekt mógł pozostać w UI chwilowo)
                    var uiProfile = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == originalCharacterProfile.CategoryName);
                    if (uiProfile != null) uiProfile.HasSplitSuggestion = false;

                    await InternalExecuteLoadProfilesAsync(token); // Odśwież całe UI
                }
                else
                {
                    StatusMessage = $"Podział profilu '{originalCharacterProfile.CategoryName}' został anulowany przez użytkownika.";
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
                        // LoadThumbnailAsync jest już w ImageFileEntry i obsługuje IsLoadingThumbnail
                        tasks.Add(entry.LoadThumbnailAsync());
                        requestedCount++;
                    }
                }
                StatusMessage = $"Rozpoczęto ładowanie {requestedCount} miniaturek...";
                await Task.WhenAll(tasks);
                token.ThrowIfCancellationRequested();
                int loadedCount = imagesToLoadThumbs.Count(img => img.Thumbnail != null);
                StatusMessage = $"Załadowano {loadedCount} z {imagesToLoadThumbs.Count} miniaturek (poproszono o {requestedCount}).";
                SimpleFileLogger.Log(StatusMessage);
            }, "Ładowanie miniaturek");
    }
}