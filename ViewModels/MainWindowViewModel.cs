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

        private const double DUPLICATE_SIMILARITY_THRESHOLD = 0.97;

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
                    if (!string.IsNullOrEmpty(_lastScannedModelNameForSuggestions))
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
                    SimpleFileLogger.Log($"RunLongOperation: Operacja '{statusMessagePrefix}' (token: {token.GetHashCode()}) zakończona pomyślnie.");
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
                    SimpleFileLogger.Log($"RunLongOperation: CTS dla tokenu {token.GetHashCode()} NIE został usunięty, ponieważ aktywny CTS ma token {_activeLongOperationCts.Token.GetHashCode()}.");
                }
                else
                {
                    SimpleFileLogger.Log($"RunLongOperation: CTS dla tokenu {token.GetHashCode()} NIE został usunięty, ponieważ _activeLongOperationCts jest null.");
                }
                SimpleFileLogger.Log($"RunLongOperation: Zakończono (finally) dla '{statusMessagePrefix}' (token: {token.GetHashCode()}).");
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
                CurrentProfileNameForEdit = ModelNameInput;
            }
            else if (!string.IsNullOrWhiteSpace(CharacterNameInput))
            {
                CurrentProfileNameForEdit = CharacterNameInput;
            }
            else
            {
                CurrentProfileNameForEdit = string.Empty;
            }
        }

        private (string model, string character) ParseCategoryName(string? categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return ("UnknownModel", "UnknownCharacter");
            var parts = categoryName.Split(new[] { " - " }, StringSplitOptions.None);
            string model = parts.Length > 0 ? parts[0].Trim() : categoryName.Trim();
            string character = parts.Length > 1 ? string.Join(" - ", parts.Skip(1)).Trim() : "General";

            if (string.IsNullOrWhiteSpace(character) && parts.Length > 1) character = "General";
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
                CharacterNameInput = (characterFullName == "General" && _selectedProfile.CategoryName.Equals(model, StringComparison.OrdinalIgnoreCase)) ? string.Empty : characterFullName;

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
            SimpleFileLogger.Log("ClearModelSpecificSuggestionsCache: Czyszczenie cache.");
            _lastModelSpecificSuggestions.Clear();
            _lastScannedModelNameForSuggestions = null;
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
            if (_activeLongOperationCts != null)
            {
                _activeLongOperationCts.Cancel();
                await Task.Delay(500);
                _activeLongOperationCts.Dispose();
                _activeLongOperationCts = null;
            }
            await _settingsService.SaveSettingsAsync(GetCurrentSettings());
            SimpleFileLogger.Log("ViewModel: OnAppClosingAsync - Ustawienia zapisane.");
        }

        private bool CanExecuteLoadProfiles(object? arg) => !IsBusy;
        private bool CanExecuteSaveAllProfiles(object? arg) => !IsBusy && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles);
        private bool CanExecuteAutoCreateProfiles(object? arg) => !IsBusy && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath);
        private bool CanExecuteGenerateProfile(object? parameter = null) => !IsBusy && !string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) && !CurrentProfileNameForEdit.Equals("Nowa Kategoria", StringComparison.OrdinalIgnoreCase) && ImageFiles.Any();
        private bool CanExecuteSuggestImages(object? parameter = null) => !IsBusy && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles) && !string.IsNullOrWhiteSpace(SourceFolderNamesInput);
        private bool CanExecuteRemoveProfile(object? parameter) => !IsBusy && parameter is CategoryProfile;
        private bool CanExecuteCheckCharacterSuggestions(object? parameter) =>
            !IsBusy && parameter is CategoryProfile profile &&
            !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) &&
            !string.IsNullOrWhiteSpace(SourceFolderNamesInput) && profile.CentroidEmbedding != null;
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
                if (_isRefreshingProfilesPostMove) RefreshPendingSuggestionCountsFromCache();
            });
        }

        private Task ExecuteGenerateProfileAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                token.ThrowIfCancellationRequested();
                string catName = CurrentProfileNameForEdit;
                SimpleFileLogger.Log($"Generowanie profilu '{catName}' ({ImageFiles.Count} obr.). Token: {token.GetHashCode()}");
                await _profileService.GenerateProfileAsync(catName, ImageFiles.ToList());
                token.ThrowIfCancellationRequested();
                StatusMessage = $"Profil '{catName}' wygenerowany/zaktualizowany.";
                await InternalExecuteLoadProfilesAsync(token);
                SelectedProfile = _profileService.GetProfile(catName);
            }, "Generowanie profilu");

        private Task ExecuteSaveAllProfilesAsync(object? parameter = null) =>
           RunLongOperation(async token =>
           {
               SimpleFileLogger.Log($"Zapis wszystkich profili. Token: {token.GetHashCode()}");
               await _profileService.SaveAllProfilesAsync();
               token.ThrowIfCancellationRequested();
               StatusMessage = "Wszystkie profile zapisane.";
               MessageBox.Show(StatusMessage, "Zapisano", MessageBoxButton.OK, MessageBoxImage.Information);
           }, "Zapisywanie wszystkich profili");

        private Task ExecuteRemoveProfileAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                var profileToRemove = (parameter as CategoryProfile) ?? SelectedProfile;
                if (profileToRemove == null) { MessageBox.Show("Wybierz profil do usunięcia.", "Brak wyboru"); return; }
                token.ThrowIfCancellationRequested();
                if (MessageBox.Show($"Usunąć profil '{profileToRemove.CategoryName}'?", "Potwierdź", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    string name = profileToRemove.CategoryName;
                    SimpleFileLogger.Log($"Usuwanie profilu '{name}'. Token: {token.GetHashCode()}");
                    if (await _profileService.RemoveProfileAsync(name))
                    {
                        StatusMessage = $"Profil '{name}' usunięty.";
                        if (SelectedProfile?.CategoryName == name) SelectedProfile = null;
                        await InternalExecuteLoadProfilesAsync(token);
                    }
                    else StatusMessage = $"Nie udało się usunąć profilu '{name}'.";
                }
            }, "Usuwanie profilu");

        private void ExecuteAddFilesToProfile(object? parameter = null)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "Obrazy|*.jpg;*.jpeg;*.png;*.webp|Wszystkie pliki|*.*", Title = "Wybierz obrazy", Multiselect = true };
            if (openFileDialog.ShowDialog() == true)
            {
                bool added = false;
                foreach (string fileName in openFileDialog.FileNames)
                {
                    if (!ImageFiles.Any(f => f.FilePath.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        ImageFiles.Add(new ImageFileEntry { FilePath = fileName, FileName = Path.GetFileName(fileName) });
                        added = true;
                    }
                }
                if (added) StatusMessage = $"Dodano {openFileDialog.FileNames.Length} plików. Załaduj miniaturki, jeśli potrzebne.";
            }
        }

        private void ExecuteClearFilesFromProfile(object? parameter = null) => ImageFiles.Clear();
        private void ExecuteCreateNewProfileSetup(object? parameter = null)
        {
            SelectedProfile = null; CurrentProfileNameForEdit = "Nowa Kategoria"; ModelNameInput = string.Empty; CharacterNameInput = string.Empty;
            StatusMessage = "Gotowy do utworzenia nowego profilu.";
        }
        private void ExecuteSelectLibraryPath(object? parameter = null)
        {
            try
            {
                var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog { Description = "Wybierz główny folder biblioteki", UseDescriptionForTitle = true, ShowNewFolderButton = true };
                if (!string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath)) dialog.SelectedPath = LibraryRootPath;
                if (dialog.ShowDialog(Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive)) == true) LibraryRootPath = dialog.SelectedPath;
            }
            catch (Exception ex) { SimpleFileLogger.LogError("Błąd wyboru folderu biblioteki", ex); MessageBox.Show($"Błąd dialogu folderu: {ex.Message}"); }
        }

        private Task ExecuteAutoCreateProfilesAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                SimpleFileLogger.Log($"AutoCreateProfiles. Skanowanie: {LibraryRootPath}. Token: {token.GetHashCode()}");
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                token.ThrowIfCancellationRequested();
                int totalCreated = 0;
                List<string> modelDirs; try { modelDirs = Directory.GetDirectories(LibraryRootPath).ToList(); } catch (Exception ex) { SimpleFileLogger.LogError($"Błąd pobierania folderów modelek z '{LibraryRootPath}'", ex); StatusMessage = $"Błąd dostępu do biblioteki: {ex.Message}"; return; }
                token.ThrowIfCancellationRequested();

                foreach (var modelDir in modelDirs)
                {
                    token.ThrowIfCancellationRequested();
                    string modelName = Path.GetFileName(modelDir);
                    try
                    {
                        foreach (var subDir in Directory.GetDirectories(modelDir))
                        {
                            token.ThrowIfCancellationRequested();
                            if (mixedFolders.Contains(Path.GetFileName(subDir))) continue;
                            totalCreated += await InternalProcessDirectoryForProfileCreationAsync(subDir, modelName, new List<string>(), mixedFolders, token);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { SimpleFileLogger.LogError($"Błąd iteracji podfolderów dla '{modelName}' w '{modelDir}'", ex); }
                }
                token.ThrowIfCancellationRequested();
                StatusMessage = $"Zakończono. Utworzono/zaktualizowano: {totalCreated} profili.";
                await InternalExecuteLoadProfilesAsync(token);
                MessageBox.Show(StatusMessage, "Skanowanie Zakończone");
            }, "Automatyczne tworzenie profili");

        private async Task<int> InternalProcessDirectoryForProfileCreationAsync(string currentPath, string modelName, List<string> parentParts, HashSet<string> mixedFolders, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int processedCount = 0;
            string dirName = Path.GetFileName(currentPath);
            var currentParts = new List<string>(parentParts) { dirName };
            string categoryName = $"{modelName} - {string.Join(" - ", currentParts)}";
            SimpleFileLogger.Log($"InternalProcessDir: Folder '{currentPath}' dla profilu '{categoryName}'.");
            List<string> imagePaths = new List<string>(); try { imagePaths = Directory.GetFiles(currentPath, "*.*", SearchOption.TopDirectoryOnly).Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f))).ToList(); } catch { }
            token.ThrowIfCancellationRequested();

            if (imagePaths.Any())
            {
                var entries = new List<ImageFileEntry>();
                foreach (var path in imagePaths) { token.ThrowIfCancellationRequested(); var entry = await _imageMetadataService.ExtractMetadataAsync(path); if (entry != null) entries.Add(entry); }
                token.ThrowIfCancellationRequested();
                if (entries.Any()) { await _profileService.GenerateProfileAsync(categoryName, entries); processedCount++; }
            }
            else if (_profileService.GetProfile(categoryName) != null) await _profileService.GenerateProfileAsync(categoryName, new List<ImageFileEntry>());
            token.ThrowIfCancellationRequested();

            try
            {
                foreach (var subPath in Directory.GetDirectories(currentPath))
                {
                    token.ThrowIfCancellationRequested();
                    if (mixedFolders.Contains(Path.GetFileName(subPath))) continue;
                    processedCount += await InternalProcessDirectoryForProfileCreationAsync(subPath, modelName, currentParts, mixedFolders, token);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { SimpleFileLogger.LogError($"Błąd przetwarzania podfolderów dla '{currentPath}'", ex); }
            return processedCount;
        }

        private async Task<Models.ProposedMove?> CreateProposedMoveAsync(ImageFileEntry source, CategoryProfile targetProfile, double similarityCentroid, string modelDir, float[] sourceEmb, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var (_, charFullName) = ParseCategoryName(targetProfile.CategoryName);
            string targetCharFolder = Path.Combine(modelDir, SanitizeFolderName(charFullName));
            Directory.CreateDirectory(targetCharFolder);
            string proposedPath = Path.Combine(targetCharFolder, source.FileName);
            ImageFileEntry? bestMatchTarget = null; double maxSimExisting = 0.0;
            List<string> filesInTarget; try { filesInTarget = Directory.EnumerateFiles(targetCharFolder).Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f))).ToList(); } catch { filesInTarget = new List<string>(); }

            foreach (string existingPath in filesInTarget)
            {
                token.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFullPath(existingPath), Path.GetFullPath(source.FilePath), StringComparison.OrdinalIgnoreCase)) continue;
                var existingMeta = await _imageMetadataService.ExtractMetadataAsync(existingPath);
                float[]? existingEmb = await _profileService.GetImageEmbeddingAsync(existingPath);
                if (existingMeta != null && existingEmb != null)
                {
                    double curSim = Utils.MathUtils.CalculateCosineSimilarity(sourceEmb, existingEmb);
                    if (curSim > maxSimExisting) { maxSimExisting = curSim; bestMatchTarget = existingMeta; }
                }
            }
            token.ThrowIfCancellationRequested();
            ProposedMoveActionType action; string finalPath = proposedPath; ImageFileEntry? displayTarget = null; double displaySim = similarityCentroid;

            if (bestMatchTarget != null && maxSimExisting >= DUPLICATE_SIMILARITY_THRESHOLD)
            {
                displayTarget = bestMatchTarget; displaySim = maxSimExisting; finalPath = bestMatchTarget.FilePath;
                long sRes = (long)source.Width * source.Height, tRes = (long)bestMatchTarget.Width * bestMatchTarget.Height;
                long sSize = new FileInfo(source.FilePath).Length, tSize = new FileInfo(bestMatchTarget.FilePath).Length;
                action = (sRes > tRes || (sRes == tRes && sSize > tSize)) ? ProposedMoveActionType.OverwriteExisting : ProposedMoveActionType.KeepExistingDeleteSource;
            }
            else
            {
                action = (File.Exists(proposedPath) && !string.Equals(Path.GetFullPath(proposedPath), Path.GetFullPath(source.FilePath), StringComparison.OrdinalIgnoreCase))
                       ? ProposedMoveActionType.ConflictKeepBoth : ProposedMoveActionType.CopyNew;
                if (action == ProposedMoveActionType.ConflictKeepBoth) displayTarget = await _imageMetadataService.ExtractMetadataAsync(proposedPath);
            }
            if (action == ProposedMoveActionType.KeepExistingDeleteSource && string.Equals(Path.GetFullPath(source.FilePath), Path.GetFullPath(finalPath), StringComparison.OrdinalIgnoreCase)) return null;
            return new Models.ProposedMove(source, displayTarget, finalPath, displaySim, targetProfile.CategoryName, action);
        }

        private Task ExecuteMatchModelSpecificAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nie wybrano modelki."; return; }
                SimpleFileLogger.Log($"MatchModelSpecific dla '{modelVM.ModelName}'. Token: {token.GetHashCode()}");
                token.ThrowIfCancellationRequested();
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                if (!mixedFolders.Any()) { MessageBox.Show("Zdefiniuj foldery źródłowe (Mix)."); return; }
                token.ThrowIfCancellationRequested();
                var proposedMoves = new List<Models.ProposedMove>(); string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName);
                await Application.Current.Dispatcher.InvokeAsync(() => { modelVM.PendingSuggestionsCount = 0; foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; });
                token.ThrowIfCancellationRequested();
                int found = 0, withEmb = 0, suggestions = 0;

                foreach (var mixName in mixedFolders)
                {
                    token.ThrowIfCancellationRequested();
                    string mixPath = Path.Combine(modelPath, mixName);
                    if (Directory.Exists(mixPath))
                    {
                        var imgPaths = await _fileScannerService.ScanDirectoryAsync(mixPath); found += imgPaths.Count;
                        foreach (var imgP in imgPaths)
                        {
                            token.ThrowIfCancellationRequested();
                            var srcEntry = await _imageMetadataService.ExtractMetadataAsync(imgP); if (srcEntry == null) continue;
                            var srcEmb = await _profileService.GetImageEmbeddingAsync(srcEntry.FilePath); if (srcEmb == null) continue;
                            withEmb++; token.ThrowIfCancellationRequested();
                            var sugTuple = _profileService.SuggestCategory(srcEmb, SuggestionSimilarityThreshold, modelVM.ModelName);
                            if (sugTuple != null)
                            {
                                var move = await CreateProposedMoveAsync(srcEntry, sugTuple.Item1, sugTuple.Item2, modelPath, srcEmb, token);
                                if (move != null) { proposedMoves.Add(move); suggestions++; }
                            }
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                SimpleFileLogger.Log($"MatchModelSpecific: Mix-obrazy: {found}, z embeddingami: {withEmb}, propozycje: {suggestions}.");
                _lastModelSpecificSuggestions = new List<Models.ProposedMove>(proposedMoves); _lastScannedModelNameForSuggestions = modelVM.ModelName;
                RefreshPendingSuggestionCountsFromCache();
                StatusMessage = $"Sugestie dla '{modelVM.ModelName}': {modelVM.PendingSuggestionsCount} dopasowań.";
                if (modelVM.PendingSuggestionsCount > 0) MessageBox.Show(StatusMessage, "Obliczanie Zakończone"); else MessageBox.Show($"Brak sugestii dla '{modelVM.ModelName}'.", "Brak Sugestii");
            }, "Dopasowywanie dla modelki");

        private Task ExecuteSuggestImagesAsync(object? parameter = null) =>
            RunLongOperation(async token =>
            {
                ClearModelSpecificSuggestionsCache(); token.ThrowIfCancellationRequested();
                var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                if (!mixedFolders.Any()) { MessageBox.Show("Zdefiniuj foldery źródłowe (Mix)."); return; }
                token.ThrowIfCancellationRequested();
                var allMoves = new List<Models.ProposedMove>();
                await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var mVM_ui in HierarchicalProfilesList) { mVM_ui.PendingSuggestionsCount = 0; foreach (var cp_ui in mVM_ui.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; } });
                token.ThrowIfCancellationRequested();
                var currentModels = HierarchicalProfilesList.ToList();

                foreach (var modelVM in currentModels)
                {
                    token.ThrowIfCancellationRequested();
                    string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName);
                    if (!Directory.Exists(modelPath) || !modelVM.HasCharacterProfiles) continue;
                    foreach (var mixName in mixedFolders)
                    {
                        token.ThrowIfCancellationRequested();
                        string mixPath = Path.Combine(modelPath, mixName);
                        if (Directory.Exists(mixPath))
                        {
                            foreach (var imgP in await _fileScannerService.ScanDirectoryAsync(mixPath))
                            {
                                token.ThrowIfCancellationRequested();
                                var srcEntry = await _imageMetadataService.ExtractMetadataAsync(imgP); if (srcEntry == null) continue;
                                var srcEmb = await _profileService.GetImageEmbeddingAsync(srcEntry.FilePath); if (srcEmb == null) continue;
                                token.ThrowIfCancellationRequested();
                                var sugTuple = _profileService.SuggestCategory(srcEmb, SuggestionSimilarityThreshold, modelVM.ModelName);
                                if (sugTuple != null) { var move = await CreateProposedMoveAsync(srcEntry, sugTuple.Item1, sugTuple.Item2, modelPath, srcEmb, token); if (move != null) allMoves.Add(move); }
                            }
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                StatusMessage = $"Globalne skanowanie: {allMoves.Count(m => m.Similarity >= SuggestionSimilarityThreshold)} sugestii.";
                var filteredMoves = allMoves.Where(m => m.Similarity >= SuggestionSimilarityThreshold).ToList();

                if (filteredMoves.Any())
                {
                    bool? dialogOutcome = false; List<Models.ProposedMove> approved = new List<Models.ProposedMove>();
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var previewVM = new PreviewChangesViewModel(filteredMoves, SuggestionSimilarityThreshold);
                        var previewWindow = new PreviewChangesWindow { DataContext = previewVM, Owner = Application.Current.MainWindow };
                        // Poprawka CS0103: Użyj 'previewWindow' po rzutowaniu
                        if (previewWindow is PreviewChangesWindow typedWin)
                        {
                            typedWin.SetCloseAction(previewVM);
                        }
                        dialogOutcome = previewWindow.ShowDialog();
                        if (dialogOutcome == true) approved = previewVM.GetApprovedMoves();
                    });
                    token.ThrowIfCancellationRequested();
                    if (dialogOutcome == true && approved.Any()) await InternalHandleApprovedMovesAsync(approved, null, null, token);
                    else StatusMessage = "Anulowano zmiany (Globalne).";
                    ClearModelSpecificSuggestionsCache();
                }
                else MessageBox.Show("Brak pasujących obrazów (Globalne).");
            }, "Globalne wyszukiwanie sugestii");

        private Task ExecuteCheckCharacterSuggestionsAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is CategoryProfile charProfile)) return;
                SimpleFileLogger.Log($"CheckCharSuggestions dla '{charProfile.CategoryName}'. Token: {token.GetHashCode()}");
                token.ThrowIfCancellationRequested();
                string modelName = _profileService.GetModelNameFromCategory(charProfile.CategoryName);
                List<Models.ProposedMove> suggestions;

                if (_lastScannedModelNameForSuggestions == modelName && _lastModelSpecificSuggestions.Any())
                {
                    suggestions = _lastModelSpecificSuggestions.Where(m => m.TargetCategoryProfileName.Equals(charProfile.CategoryName, StringComparison.OrdinalIgnoreCase) && m.Similarity >= SuggestionSimilarityThreshold).ToList();
                }
                else
                {
                    suggestions = new List<Models.ProposedMove>();
                    var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                    string modelPath = Path.Combine(LibraryRootPath, modelName); token.ThrowIfCancellationRequested();
                    if (Directory.Exists(modelPath) && mixedFolders.Any())
                    {
                        foreach (var mixName in mixedFolders)
                        {
                            token.ThrowIfCancellationRequested();
                            string mixP = Path.Combine(modelPath, mixName);
                            if (Directory.Exists(mixP))
                            {
                                foreach (var imgP in await _fileScannerService.ScanDirectoryAsync(mixP))
                                {
                                    token.ThrowIfCancellationRequested();
                                    var srcE = await _imageMetadataService.ExtractMetadataAsync(imgP); if (srcE == null) continue;
                                    var srcEmb = await _profileService.GetImageEmbeddingAsync(srcE.FilePath); if (srcEmb == null || charProfile.CentroidEmbedding == null) continue;
                                    token.ThrowIfCancellationRequested();
                                    double sim = Utils.MathUtils.CalculateCosineSimilarity(srcEmb, charProfile.CentroidEmbedding);
                                    if (sim >= SuggestionSimilarityThreshold)
                                    {
                                        var move = await CreateProposedMoveAsync(srcE, charProfile, sim, modelPath, srcEmb, token);
                                        if (move != null && move.TargetCategoryProfileName == charProfile.CategoryName) suggestions.Add(move);
                                    }
                                }
                            }
                        }
                    }
                    StatusMessage = $"Skanowanie dla '{charProfile.CategoryName}': {suggestions.Count} sugestii."; token.ThrowIfCancellationRequested();
                    if (_lastScannedModelNameForSuggestions != modelName && _lastScannedModelNameForSuggestions != null) _lastModelSpecificSuggestions.Clear();
                    _lastModelSpecificSuggestions.RemoveAll(m => m.TargetCategoryProfileName.Equals(charProfile.CategoryName, StringComparison.OrdinalIgnoreCase));
                    _lastModelSpecificSuggestions.AddRange(suggestions); _lastScannedModelNameForSuggestions = modelName;
                }
                token.ThrowIfCancellationRequested();
                if (suggestions.Any())
                {
                    bool? outcome = false; List<Models.ProposedMove> approved = new List<Models.ProposedMove>();
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var vm = new PreviewChangesViewModel(suggestions, SuggestionSimilarityThreshold);
                        var win = new PreviewChangesWindow { DataContext = vm, Owner = Application.Current.MainWindow };
                        // Poprawka CS0103: Użyj 'win' po rzutowaniu
                        if (win is PreviewChangesWindow typedWin)
                        {
                            typedWin.SetCloseAction(vm);
                        }
                        outcome = win.ShowDialog();
                        if (outcome == true) approved = vm.GetApprovedMoves();
                    });
                    token.ThrowIfCancellationRequested();
                    if (outcome == true && approved.Any()) await InternalHandleApprovedMovesAsync(approved, HierarchicalProfilesList.FirstOrDefault(m => m.ModelName == modelName), charProfile, token);
                    else StatusMessage = $"Anulowano sugestie dla '{charProfile.CategoryName}'.";
                }
                else MessageBox.Show($"Brak sugestii dla '{charProfile.CategoryName}'.");
                RefreshPendingSuggestionCountsFromCache();
            }, "Sprawdzanie sugestii dla postaci");

        private void RefreshPendingSuggestionCountsFromCache()
        {
            Application.Current.Dispatcher.Invoke(() => {
                if (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions))
                {
                    foreach (var mVM in HierarchicalProfilesList) { mVM.PendingSuggestionsCount = 0; foreach (var cp in mVM.CharacterProfiles) cp.PendingSuggestionsCount = 0; }
                    return;
                }
                var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(_lastScannedModelNameForSuggestions, StringComparison.OrdinalIgnoreCase)); if (modelToUpdate == null) return;
                foreach (var otherM in HierarchicalProfilesList.Where(m => m.ModelName != modelToUpdate.ModelName)) { otherM.PendingSuggestionsCount = 0; foreach (var cpOther in otherM.CharacterProfiles) cpOther.PendingSuggestionsCount = 0; }
                int total = 0; foreach (var cp in modelToUpdate.CharacterProfiles) { cp.PendingSuggestionsCount = _lastModelSpecificSuggestions.Count(m => m.TargetCategoryProfileName.Equals(cp.CategoryName, StringComparison.OrdinalIgnoreCase) && m.Similarity >= SuggestionSimilarityThreshold); total += cp.PendingSuggestionsCount; }
                modelToUpdate.PendingSuggestionsCount = total;
            });
        }

        private async Task InternalHandleApprovedMovesAsync(List<Models.ProposedMove> approvedMoves, ModelDisplayViewModel? specificModelVM, CategoryProfile? specificCharacterProfile, CancellationToken token)
        {
            int suc = 0, cpyErr = 0, delErr = 0, skipQ = 0, skipO = 0; var changedNames = new HashSet<string>(); var processedPaths = new List<string>();
            foreach (var move in approvedMoves)
            {
                token.ThrowIfCancellationRequested(); string src = move.SourceImage.FilePath, target = move.ProposedTargetPath; var act = move.Action; bool ok = false, delSrc = false;
                try
                {
                    if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) { skipO++; continue; }
                    string tDir = Path.GetDirectoryName(target); if (string.IsNullOrEmpty(tDir)) { skipO++; continue; }
                    Directory.CreateDirectory(tDir);
                    switch (act)
                    {
                        case ProposedMoveActionType.CopyNew: if (File.Exists(target)) target = GenerateUniqueTargetPath(tDir, Path.GetFileName(src), "_new"); await Task.Run(() => File.Copy(src, target, false), token); ok = true; delSrc = true; break;
                        case ProposedMoveActionType.OverwriteExisting: await Task.Run(() => File.Copy(src, target, true), token); ok = true; delSrc = true; break;
                        case ProposedMoveActionType.KeepExistingDeleteSource: ok = true; delSrc = true; skipQ++; break;
                        case ProposedMoveActionType.ConflictKeepBoth: long sS = new FileInfo(src).Length, tS = File.Exists(target) ? new FileInfo(target).Length : 0; if (sS > tS * 1.1) await Task.Run(() => File.Copy(src, target, true), token); else skipQ++; ok = true; delSrc = true; break;
                    }
                    token.ThrowIfCancellationRequested();
                    if (ok) { suc++; processedPaths.Add(src); if (delSrc) try { await Task.Run(() => File.Delete(src), token); } catch { delErr++; } changedNames.Add(move.TargetCategoryProfileName); }
                }
                catch (OperationCanceledException) { throw; }
                catch { cpyErr++; }
            }
            token.ThrowIfCancellationRequested();
            if (_lastScannedModelNameForSuggestions != null && processedPaths.Any()) _lastModelSpecificSuggestions.RemoveAll(s => processedPaths.Contains(s.SourceImage.FilePath));
            RefreshPendingSuggestionCountsFromCache(); token.ThrowIfCancellationRequested();
            if (changedNames.Any()) { _isRefreshingProfilesPostMove = true; await InternalRefreshProfilesAsync(changedNames.Distinct().ToList(), token); _isRefreshingProfilesPostMove = false; token.ThrowIfCancellationRequested(); RefreshPendingSuggestionCountsFromCache(); }
            StatusMessage = $"Zakończono: {suc} wyk., {skipQ} pom.(Q), {cpyErr} bł.kop., {delErr} bł.us."; MessageBox.Show(StatusMessage, "Operacja zakończona");
        }

        private async Task InternalRefreshProfilesAsync(List<string> profileNames, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(LibraryRootPath) || !Directory.Exists(LibraryRootPath)) return;
            await Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = "Odświeżanie profili..."); bool changed = false; token.ThrowIfCancellationRequested();
            foreach (var pName in profileNames)
            {
                token.ThrowIfCancellationRequested(); var (model, charName) = ParseCategoryName(pName); string cPath = Path.Combine(LibraryRootPath, SanitizeFolderName(model), SanitizeFolderName(charName));
                if (Directory.Exists(cPath))
                {
                    var entries = new List<ImageFileEntry>(); List<string> files; try { files = Directory.GetFiles(cPath).Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f))).ToList(); } catch { files = new List<string>(); }
                    foreach (var p in files) { token.ThrowIfCancellationRequested(); var entry = await _imageMetadataService.ExtractMetadataAsync(p); if (entry != null) entries.Add(entry); }
                    token.ThrowIfCancellationRequested(); await _profileService.GenerateProfileAsync(pName, entries); changed = true;
                }
                else if (_profileService.GetProfile(pName) != null) { await _profileService.GenerateProfileAsync(pName, new List<ImageFileEntry>()); changed = true; }
            }
            token.ThrowIfCancellationRequested(); if (changed) await InternalExecuteLoadProfilesAsync(token); else await Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = "Profile aktualne.");
        }

        private string GenerateUniqueTargetPath(string dir, string origName, string suffix = "_conflict")
        {
            string baseN = Path.GetFileNameWithoutExtension(origName), ext = Path.GetExtension(origName); int i = 1; string finalPath;
            do { string newN = $"{baseN}{suffix}{i++}{ext}"; finalPath = Path.Combine(dir, newN); if (i > 9999) { finalPath = Path.Combine(dir, $"{baseN}_{Guid.NewGuid():N}{ext}"); break; } } while (File.Exists(finalPath));
            return finalPath;
        }

        private Task ExecuteRemoveModelTreeAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is ModelDisplayViewModel modelVM)) return; token.ThrowIfCancellationRequested();
                if (MessageBox.Show($"Usunąć '{modelVM.ModelName}' i profile?", "Potwierdź", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    if (await _profileService.RemoveAllProfilesForModelAsync(modelVM.ModelName))
                    {
                        StatusMessage = $"'{modelVM.ModelName}' usunięta."; if (_lastScannedModelNameForSuggestions == modelVM.ModelName) ClearModelSpecificSuggestionsCache();
                        if (SelectedProfile != null && _profileService.GetModelNameFromCategory(SelectedProfile.CategoryName) == modelVM.ModelName) SelectedProfile = null;
                        await InternalExecuteLoadProfilesAsync(token);
                    }
                    else { StatusMessage = $"Nie udało się usunąć '{modelVM.ModelName}'."; await InternalExecuteLoadProfilesAsync(token); }
                }
            }, "Usuwanie modelki");

        private Task ExecuteAnalyzeModelForSplittingAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is ModelDisplayViewModel modelVM)) return; token.ThrowIfCancellationRequested(); int marked = 0;
                await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.HasSplitSuggestion = false; });
                var charProfs = modelVM.CharacterProfiles.ToList();
                foreach (var cp in charProfs)
                {
                    token.ThrowIfCancellationRequested(); const int minCon = 10, minSig = 20; if (cp.SourceImagePaths == null || cp.SourceImagePaths.Count < minCon) continue;
                    var embs = new List<float[]>(); foreach (string pth in cp.SourceImagePaths) { token.ThrowIfCancellationRequested(); if (File.Exists(pth)) { var e = await _profileService.GetImageEmbeddingAsync(pth); if (e != null) embs.Add(e); } }
                    token.ThrowIfCancellationRequested(); if (embs.Count < minCon) continue; bool split = embs.Count >= minSig;
                    var uiCp = modelVM.CharacterProfiles.FirstOrDefault(p => p.CategoryName == cp.CategoryName); if (uiCp != null) uiCp.HasSplitSuggestion = split; if (split) marked++;
                }
                token.ThrowIfCancellationRequested(); StatusMessage = $"Analiza podziału dla '{modelVM.ModelName}': {marked} oznaczonych.";
                if (marked > 0) MessageBox.Show($"{marked} profili do podziału.", "Analiza Zakończona"); else MessageBox.Show("Brak profili do podziału.", "Analiza Zakończona");
            }, "Analiza profili pod kątem podziału");

        private Task ExecuteOpenSplitProfileDialogAsync(object? parameter) =>
            RunLongOperation(async token =>
            {
                if (!(parameter is CategoryProfile charProfile)) return; token.ThrowIfCancellationRequested();
                var images = new List<ImageFileEntry>();
                if (charProfile.SourceImagePaths != null)
                {
                    foreach (var p in charProfile.SourceImagePaths) { token.ThrowIfCancellationRequested(); if (File.Exists(p)) { var entry = await _imageMetadataService.ExtractMetadataAsync(p); if (entry != null) images.Add(entry); } }
                }
                token.ThrowIfCancellationRequested(); if (!images.Any()) { MessageBox.Show("Profil pusty."); return; }
                var g1 = images.Take(images.Count / 2).ToList(); var g2 = images.Skip(images.Count / 2).ToList();
                string baseN = _profileService.GetCharacterNameFromCategory(charProfile.CategoryName); if (baseN == "General") baseN = _profileService.GetModelNameFromCategory(charProfile.CategoryName);
                string n1 = $"{baseN} - Grp1", n2 = $"{baseN} - Grp2"; bool? res = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var vm = new SplitProfileViewModel(charProfile, g1, g2, n1, n2); var win = new SplitProfileWindow { DataContext = vm, Owner = Application.Current.MainWindow };
                    if (win is SplitProfileWindow typedWin) typedWin.SetCloseAction(vm);
                    res = win.ShowDialog();
                });
                token.ThrowIfCancellationRequested();
                if (res == true) { StatusMessage = $"Podział '{charProfile.CategoryName}' zatwierdzony (TODO)."; var uiP = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == charProfile.CategoryName); if (uiP != null) uiP.HasSplitSuggestion = false; await InternalExecuteLoadProfilesAsync(token); }
                else StatusMessage = $"Podział '{charProfile.CategoryName}' anulowany.";
            }, "Otwieranie okna podziału profilu");  

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
                if (!(parameter is IEnumerable<ImageFileEntry> images)) { StatusMessage = "Brak obrazów do załadowania miniaturek."; return; }
                var imgList = images.ToList(); if (!imgList.Any()) { StatusMessage = "Brak obrazów do załadowania miniaturek."; return; }
                SimpleFileLogger.Log($"EnsureThumbnailsLoaded: Ładowanie dla {imgList.Count} obrazów. Token: {token.GetHashCode()}");
                int count = 0; var tasks = new List<Task>();
                foreach (var entry in imgList)
                {
                    token.ThrowIfCancellationRequested(); if (entry.Thumbnail == null && !entry.IsLoadingThumbnail) { tasks.Add(entry.LoadThumbnailAsync()); count++; }
                }
                StatusMessage = $"Rozpoczęto ładowanie {count} miniaturek..."; await Task.WhenAll(tasks); token.ThrowIfCancellationRequested();
                StatusMessage = $"Załadowano {imgList.Count(img => img.Thumbnail != null)} z {imgList.Count} miniaturek.";
            }, "Ładowanie miniaturek");
    }
}