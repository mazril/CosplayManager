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
                    (RemoveProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CheckCharacterSuggestionsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (OpenSplitProfileDialogCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
                    (GenerateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
                    (GenerateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (ClearFilesFromProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
                    (AutoCreateProfilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (SuggestImagesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (MatchModelSpecificCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CheckCharacterSuggestionsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (AnalyzeModelForSplittingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
                    (AutoCreateProfilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (SuggestImagesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (MatchModelSpecificCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CheckCharacterSuggestionsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (AnalyzeModelForSplittingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

        private bool _isBusy; // Dla paska postępu w StatusBar
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
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

            LoadProfilesCommand = new AsyncRelayCommand(ExecuteLoadProfilesAsync);
            GenerateProfileCommand = new AsyncRelayCommand(ExecuteGenerateProfileAsync, CanExecuteGenerateProfile);
            SaveProfilesCommand = new AsyncRelayCommand(ExecuteSaveAllProfilesAsync, CanExecuteSaveAllProfiles);
            RemoveProfileCommand = new AsyncRelayCommand(ExecuteRemoveProfileAsync, CanExecuteRemoveProfile);
            AddFilesToProfileCommand = new RelayCommand(ExecuteAddFilesToProfile);
            ClearFilesFromProfileCommand = new RelayCommand(ExecuteClearFilesFromProfile, _ => ImageFiles.Any());
            CreateNewProfileSetupCommand = new RelayCommand(ExecuteCreateNewProfileSetup);
            SelectLibraryPathCommand = new RelayCommand(ExecuteSelectLibraryPath);
            AutoCreateProfilesCommand = new AsyncRelayCommand(ExecuteAutoCreateProfilesAsync, CanExecuteAutoCreateProfiles);
            SuggestImagesCommand = new AsyncRelayCommand(ExecuteSuggestImagesAsync, CanExecuteSuggestImages);
            SaveAppSettingsCommand = new AsyncRelayCommand(ExecuteSaveAppSettingsAsync);
            MatchModelSpecificCommand = new AsyncRelayCommand(ExecuteMatchModelSpecificAsync, CanExecuteMatchModelSpecific);
            CheckCharacterSuggestionsCommand = new AsyncRelayCommand(ExecuteCheckCharacterSuggestionsAsync, CanExecuteCheckCharacterSuggestions);
            RemoveModelTreeCommand = new AsyncRelayCommand(ExecuteRemoveModelTreeAsync, CanExecuteRemoveModelTree);
            AnalyzeModelForSplittingCommand = new AsyncRelayCommand(ExecuteAnalyzeModelForSplittingAsync, CanExecuteAnalyzeModelForSplitting);
            OpenSplitProfileDialogCommand = new AsyncRelayCommand(ExecuteOpenSplitProfileDialogAsync, CanExecuteOpenSplitProfileDialog);
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

            if (string.IsNullOrWhiteSpace(character) && parts.Length > 1)
            {
                character = "General";
            }
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

        private async void UpdateEditFieldsFromSelectedProfile()
        {
            if (_selectedProfile != null)
            {
                CurrentProfileNameForEdit = _selectedProfile.CategoryName;
                var (model, characterFullName) = ParseCategoryName(_selectedProfile.CategoryName);
                ModelNameInput = model;

                if (characterFullName == "General" && _selectedProfile.CategoryName.Equals(model, StringComparison.OrdinalIgnoreCase))
                {
                    CharacterNameInput = string.Empty;
                }
                else
                {
                    CharacterNameInput = characterFullName;
                }

                var newImageFiles = new ObservableCollection<ImageFileEntry>();
                if (_selectedProfile.SourceImagePaths != null)
                {
                    foreach (var path in _selectedProfile.SourceImagePaths)
                    {
                        if (File.Exists(path))
                        {
                            var entry = new ImageFileEntry { FilePath = path, FileName = Path.GetFileName(path) };
                            newImageFiles.Add(entry);
                            _ = entry.LoadThumbnailAsync(); // Uruchom ładowanie w tle
                        }
                        else
                        {
                            SimpleFileLogger.LogWarning($"OSTRZEŻENIE (UpdateEditFields): Ścieżka obrazu '{path}' dla profilu '{_selectedProfile.CategoryName}' nie istnieje.");
                        }
                    }
                    ImageFiles = newImageFiles;
                }
                else
                {
                    ImageFiles = new ObservableCollection<ImageFileEntry>();
                }
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
            SimpleFileLogger.Log("ClearModelSpecificSuggestionsCache: Czyszczenie cache sugestii dla modelu.");
            _lastModelSpecificSuggestions.Clear();
            _lastScannedModelNameForSuggestions = null;
            RefreshPendingSuggestionCountsFromCache();
        }


        private UserSettings GetCurrentSettings()
        {
            return new UserSettings
            {
                LibraryRootPath = this.LibraryRootPath,
                SourceFolderNamesInput = this.SourceFolderNamesInput,
                SuggestionSimilarityThreshold = this.SuggestionSimilarityThreshold
            };
        }

        private void ApplySettings(UserSettings settings)
        {
            if (settings != null)
            {
                LibraryRootPath = settings.LibraryRootPath;
                SourceFolderNamesInput = settings.SourceFolderNamesInput;
                SuggestionSimilarityThreshold = settings.SuggestionSimilarityThreshold;
                SimpleFileLogger.Log("Zastosowano wczytane ustawienia aplikacji.");
            }
        }

        private async Task ExecuteSaveAppSettingsAsync(object? parameter = null)
        {
            StatusMessage = "Zapisywanie ustawień aplikacji...";
            UserSettings currentSettings = GetCurrentSettings();
            await _settingsService.SaveSettingsAsync(currentSettings);
            StatusMessage = "Ustawienia aplikacji zapisane.";
            SimpleFileLogger.Log("Ustawienia aplikacji zostały zapisane (na żądanie).");
        }

        public async Task InitializeAsync()
        {
            StatusMessage = "Inicjalizacja...";
            SimpleFileLogger.Log("ViewModel: Rozpoczęto InitializeAsync.");
            IsBusy = true;
            try
            {
                UserSettings? loadedSettings = await _settingsService.LoadSettingsAsync();
                if (loadedSettings != null)
                {
                    ApplySettings(loadedSettings);
                }
                else
                {
                    SimpleFileLogger.Log("InitializeAsync: Nie wczytano ustawień, używam domyślnych wartości z ViewModelu.");
                }
                await ExecuteLoadProfilesAsync();

                if (string.IsNullOrEmpty(LibraryRootPath))
                {
                    StatusMessage = "Gotowy. Proszę wybrać główny folder biblioteki.";
                }
                else if (!Directory.Exists(LibraryRootPath))
                {
                    StatusMessage = $"Uwaga: Folder biblioteki '{LibraryRootPath}' nie istnieje. Proszę wybrać poprawny.";
                    SimpleFileLogger.LogWarning($"OSTRZEŻENIE: Wczytany LibraryRootPath ('{LibraryRootPath}') nie istnieje.");
                }
                else
                {
                    StatusMessage = "Gotowy.";
                }
            }
            finally
            {
                IsBusy = false;
                SimpleFileLogger.Log("ViewModel: Zakończono InitializeAsync.");
            }
        }
        public async Task OnAppClosingAsync()
        {
            SimpleFileLogger.Log("ViewModel: OnAppClosingAsync - Zapisywanie ustawień aplikacji...");
            UserSettings currentSettings = GetCurrentSettings();
            await _settingsService.SaveSettingsAsync(currentSettings);
            SimpleFileLogger.Log("ViewModel: OnAppClosingAsync - Ustawienia aplikacji zapisane.");
        }

        private bool CanExecuteSaveAllProfiles(object? arg) => HierarchicalProfilesList.Any(m => m.HasCharacterProfiles);
        private bool CanExecuteAutoCreateProfiles(object? arg) => !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath);
        private bool CanExecuteGenerateProfile(object? parameter = null) => !string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) && !CurrentProfileNameForEdit.Equals("Nowa Kategoria", StringComparison.OrdinalIgnoreCase) && ImageFiles.Any();
        private bool CanExecuteSuggestImages(object? parameter = null) => !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles) && !string.IsNullOrWhiteSpace(SourceFolderNamesInput);

        private bool CanExecuteRemoveProfile(object? parameter)
        {
            return parameter is CategoryProfile;
        }

        private bool CanExecuteCheckCharacterSuggestions(object? parameter)
        {
            return parameter is CategoryProfile profile &&
                   !string.IsNullOrWhiteSpace(LibraryRootPath) &&
                   Directory.Exists(LibraryRootPath) &&
                   !string.IsNullOrWhiteSpace(SourceFolderNamesInput) &&
                   profile.CentroidEmbedding != null;
        }

        private bool CanExecuteMatchModelSpecific(object? parameter)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) return false;
            if (string.IsNullOrWhiteSpace(LibraryRootPath) || !Directory.Exists(LibraryRootPath)) return false;
            if (!modelVM.HasCharacterProfiles) return false;
            if (string.IsNullOrWhiteSpace(SourceFolderNamesInput)) return false;
            return true;
        }
        private bool CanExecuteRemoveModelTree(object? parameter) => parameter is ModelDisplayViewModel;


        private async Task ExecuteLoadProfilesAsync(object? parameter = null)
        {
            StatusMessage = "Ładowanie profili...";
            SimpleFileLogger.Log($"ViewModel: Rozpoczęto ładowanie profili (ExecuteLoadProfilesAsync). Flaga _isRefreshingProfilesPostMove: {_isRefreshingProfilesPostMove}");
            IsBusy = true;
            try
            {
                if (!_isRefreshingProfilesPostMove)
                {
                    ClearModelSpecificSuggestionsCache();
                }
                else
                {
                    SimpleFileLogger.Log("ExecuteLoadProfilesAsync: Pominięto ClearModelSpecificSuggestionsCache z powodu flagi _isRefreshingProfilesPostMove.");
                }

                string? previouslySelectedCategoryName = SelectedProfile?.CategoryName;
                await _profileService.LoadProfilesAsync();
                var allFlatProfiles = _profileService.GetAllProfiles()?.OrderBy(p => p.CategoryName).ToList();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    HierarchicalProfilesList.Clear();
                    if (allFlatProfiles != null && allFlatProfiles.Any())
                    {
                        var groupedByModel = allFlatProfiles
                            .GroupBy(p => _profileService.GetModelNameFromCategory(p.CategoryName))
                            .OrderBy(g => g.Key);

                        foreach (var modelGroup in groupedByModel)
                        {
                            var modelVM = new ModelDisplayViewModel(modelGroup.Key);
                            foreach (var characterProfile in modelGroup.OrderBy(p => _profileService.GetCharacterNameFromCategory(p.CategoryName)))
                            {
                                modelVM.AddCharacterProfile(characterProfile);
                            }
                            HierarchicalProfilesList.Add(modelVM);
                        }
                    }

                    int totalProfiles = HierarchicalProfilesList.Sum(m => m.CharacterProfiles.Count);
                    StatusMessage = $"Załadowano {totalProfiles} profili dla {HierarchicalProfilesList.Count} modelek.";
                    SimpleFileLogger.Log($"ViewModel: Zakończono ładowanie (wątek UI). Załadowano {totalProfiles} profili dla {HierarchicalProfilesList.Count} modelek.");

                    if (!string.IsNullOrEmpty(previouslySelectedCategoryName))
                    {
                        SelectedProfile = allFlatProfiles?.FirstOrDefault(p => p.CategoryName.Equals(previouslySelectedCategoryName, StringComparison.OrdinalIgnoreCase));
                    }
                    else if (SelectedProfile != null && !(allFlatProfiles?.Any(p => p.CategoryName == SelectedProfile.CategoryName) ?? false))
                    {
                        SelectedProfile = null;
                    }

                    OnPropertyChanged(nameof(AnyProfilesLoaded));
                    (SuggestImagesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (SaveProfilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RemoveProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (AutoCreateProfilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (MatchModelSpecificCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CheckCharacterSuggestionsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RemoveModelTreeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (AnalyzeModelForSplittingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

                    if (_isRefreshingProfilesPostMove)
                    {
                        SimpleFileLogger.Log("ExecuteLoadProfilesAsync: _isRefreshingProfilesPostMove=true, wywołuję RefreshPendingSuggestionCountsFromCache aby zsynchronizować nowe obiekty UI z zachowanym cachem.");
                        RefreshPendingSuggestionCountsFromCache();
                    }
                });
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteGenerateProfileAsync(object? parameter = null)
        {
            if (!CanExecuteGenerateProfile())
            {
                MessageBox.Show("Aby wygenerować profil, podaj nazwę modelki, postaci oraz dodaj przynajmniej jeden obraz źródłowy.", "Niekompletne dane", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string categoryName = CurrentProfileNameForEdit;
            StatusMessage = $"Generowanie profilu dla '{categoryName}'...";
            SimpleFileLogger.Log($"ViewModel: Generowanie profilu dla '{categoryName}' z {ImageFiles.Count} obrazami.");
            IsBusy = true;
            try
            {
                await _profileService.GenerateProfileAsync(categoryName, ImageFiles.ToList());
                StatusMessage = $"Profil '{categoryName}' wygenerowany/zaktualizowany.";
                SimpleFileLogger.Log($"Profil '{categoryName}' wygenerowany/zaktualizowany.");
                await ExecuteLoadProfilesAsync();
                SelectedProfile = _profileService.GetProfile(categoryName);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Błąd generowania profilu: {ex.Message}";
                SimpleFileLogger.LogError($"Błąd generowania profilu dla '{categoryName}'", ex);
                MessageBox.Show($"Wystąpił błąd podczas generowania profilu: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteSaveAllProfilesAsync(object? parameter = null)
        {
            StatusMessage = "Zapisywanie wszystkich profili...";
            SimpleFileLogger.Log("ViewModel: Zapisywanie wszystkich profili (ExecuteSaveAllProfilesAsync)...");
            IsBusy = true;
            try
            {
                await _profileService.SaveAllProfilesAsync();
                StatusMessage = "Wszystkie profile zostały zapisane.";
                SimpleFileLogger.Log("ViewModel: Wszystkie profile zapisane.");
                MessageBox.Show("Wszystkie profile zostały zapisane.", "Zapisano", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteRemoveProfileAsync(object? parameter)
        {
            CategoryProfile? profileToRemove = null;
            if (parameter is CategoryProfile contextProfile)
            {
                profileToRemove = contextProfile;
            }
            else if (SelectedProfile != null)
            {
                profileToRemove = SelectedProfile;
                SimpleFileLogger.LogWarning($"ExecuteRemoveProfileAsync: Parametr nie był typu CategoryProfile. Użyto SelectedProfile ('{SelectedProfile?.CategoryName}'). To może nie być oczekiwane dla menu kontekstowego.");
            }

            if (profileToRemove == null)
            {
                MessageBox.Show("Wybierz profil postaci z drzewka (lub użyj menu kontekstowego), aby go usunąć.", "Brak wyboru", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"Czy na pewno chcesz usunąć profil '{profileToRemove.CategoryName}'?",
                                         "Potwierdź usunięcie", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                string profileNameToRemoveStr = profileToRemove.CategoryName;
                StatusMessage = $"Usuwanie profilu '{profileNameToRemoveStr}'...";
                SimpleFileLogger.Log($"ViewModel: Usuwanie profilu '{profileNameToRemoveStr}'.");
                IsBusy = true;
                try
                {
                    bool removed = await _profileService.RemoveProfileAsync(profileNameToRemoveStr);
                    if (removed)
                    {
                        StatusMessage = $"Profil '{profileNameToRemoveStr}' usunięty.";
                        if (SelectedProfile?.CategoryName == profileNameToRemoveStr) SelectedProfile = null;
                        await ExecuteLoadProfilesAsync();
                    }
                    else
                    {
                        StatusMessage = $"Nie udało się usunąć profilu '{profileNameToRemoveStr}'.";
                        SimpleFileLogger.Log($"ViewModel: Nie udało się usunąć profilu '{profileNameToRemoveStr}'.");
                    }
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        private async void ExecuteAddFilesToProfile(object? parameter = null)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Image files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All files (*.*)|*.*",
                Title = "Wybierz obrazy dla profilu",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                IsBusy = true; // Pokaż zajętość na czas dodawania i ładowania miniatur
                try
                {
                    bool filesAdded = false;
                    foreach (string fileName in openFileDialog.FileNames)
                    {
                        if (!ImageFiles.Any(f => f.FilePath.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            var entry = new ImageFileEntry { FilePath = fileName, FileName = Path.GetFileName(fileName) };
                            ImageFiles.Add(entry);
                            _ = entry.LoadThumbnailAsync(); // Uruchom ładowanie, nie czekaj
                            filesAdded = true;
                        }
                    }
                    if (filesAdded)
                    {
                        (GenerateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    }
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        private void ExecuteClearFilesFromProfile(object? parameter = null)
        {
            ImageFiles.Clear();
            (GenerateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void ExecuteCreateNewProfileSetup(object? parameter = null)
        {
            SelectedProfile = null;
            CurrentProfileNameForEdit = "Nowa Kategoria";
            ModelNameInput = string.Empty;
            CharacterNameInput = string.Empty;
            StatusMessage = "Gotowy do utworzenia nowego profilu. Wprowadź dane i dodaj obrazy.";
        }

        private void ExecuteSelectLibraryPath(object? parameter = null)
        {
            try
            {
                var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
                {
                    Description = "Wybierz główny folder biblioteki Cosplay",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true
                };
                if (!string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath))
                {
                    dialog.SelectedPath = LibraryRootPath;
                }
                var owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive);
                if (dialog.ShowDialog(owner) == true)
                {
                    LibraryRootPath = dialog.SelectedPath;
                    SimpleFileLogger.Log($"Wybrano nową ścieżkę biblioteki: {LibraryRootPath}");
                }
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError("Błąd podczas wybierania folderu biblioteki (Ookii Dialogs)", ex);
                MessageBox.Show($"Wystąpił błąd przy próbie otwarcia dialogu wyboru folderu: {ex.Message}\nUpewnij się, że biblioteka Ookii.Dialogs.Wpf jest poprawnie zainstalowana i skonfigurowana.", "Błąd dialogu folderu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecuteAutoCreateProfilesAsync(object? parameter)
        {
            if (!CanExecuteAutoCreateProfiles(null))
            {
                MessageBox.Show($"Główny folder biblioteki '{LibraryRootPath}' nie jest ustawiony lub nie istnieje.", "Błąd konfiguracji", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StatusMessage = "Automatyczne tworzenie/aktualizacja profili z: " + LibraryRootPath;
            SimpleFileLogger.Log($"ExecuteAutoCreateProfilesAsync: Rozpoczęto skanowanie {LibraryRootPath}.");
            IsBusy = true;
            try
            {
                var configuredMixedFolderNames = new HashSet<string>(
                    SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(name => name.Trim()),
                    StringComparer.OrdinalIgnoreCase
                );

                int profilesCreatedOrUpdatedTotal = 0;
                List<string> modelDirectories;
                try
                {
                    modelDirectories = Directory.GetDirectories(LibraryRootPath).ToList();
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"Błąd podczas pobierania folderów modelek z '{LibraryRootPath}'", ex);
                    StatusMessage = $"Błąd dostępu do folderu biblioteki: {ex.Message}";
                    MessageBox.Show(StatusMessage, "Błąd Skanowania", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                foreach (var modelDir in modelDirectories)
                {
                    string modelName = Path.GetFileName(modelDir);
                    SimpleFileLogger.Log($"ExecuteAutoCreateProfilesAsync: Przetwarzanie modelki '{modelName}' w folderze '{modelDir}'.");

                    try
                    {
                        foreach (var directSubDirOfModel in Directory.GetDirectories(modelDir))
                        {
                            string directSubDirName = Path.GetFileName(directSubDirOfModel);
                            if (configuredMixedFolderNames.Contains(directSubDirName))
                            {
                                SimpleFileLogger.Log($"ExecuteAutoCreateProfilesAsync: Pomijanie folderu '{directSubDirName}' wewnątrz '{modelName}' (jest na liście folderów Mix).");
                                continue;
                            }
                            profilesCreatedOrUpdatedTotal += await ProcessDirectoryForProfileCreationAsync(directSubDirOfModel, modelName, new List<string>(), configuredMixedFolderNames);
                        }
                    }
                    catch (Exception ex)
                    {
                        SimpleFileLogger.LogError($"Błąd podczas iterowania podfolderów dla modelki '{modelName}' w '{modelDir}'", ex);
                    }
                }

                StatusMessage = $"Zakończono. Utworzono/zaktualizowano: {profilesCreatedOrUpdatedTotal} profili.";
                SimpleFileLogger.Log(StatusMessage);
                await ExecuteLoadProfilesAsync(); // To też ustawi IsBusy
                MessageBox.Show(StatusMessage, "Skanowanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                if (!Application.Current.Dispatcher.CheckAccess()) { Application.Current.Dispatcher.Invoke(() => IsBusy = false); } else { IsBusy = false; } // Upewnij się, że IsBusy jest ustawiane w wątku UI
            }
        }

        private async Task<int> ProcessDirectoryForProfileCreationAsync(
            string currentDirPath,
            string modelName,
            List<string> parentCategoryParts,
            HashSet<string> configuredMixedFolderNames)
        {
            int profilesProcessedInThisBranch = 0;
            string currentDirName = Path.GetFileName(currentDirPath);

            List<string> currentCategoryPathParts = new List<string>(parentCategoryParts);
            currentCategoryPathParts.Add(currentDirName);

            string categoryName = $"{modelName} - {string.Join(" - ", currentCategoryPathParts)}";
            SimpleFileLogger.Log($"ProcessDirectoryForProfileCreationAsync: Przetwarzanie folderu '{currentDirPath}' dla potencjalnego profilu '{categoryName}'.");

            List<string> imagePathsInCurrentDir = await Task.Run(() =>
            {
                try
                {
                    return Directory.GetFiles(currentDirPath, "*.*", SearchOption.TopDirectoryOnly)
                                    .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f)))
                                    .ToList();
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"Błąd odczytu plików z '{currentDirPath}'", ex);
                    return new List<string>();
                }
            });

            if (imagePathsInCurrentDir.Any())
            {
                List<ImageFileEntry> imageEntries = new List<ImageFileEntry>();
                foreach (var path in imagePathsInCurrentDir)
                {
                    var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                    if (entry != null)
                    {
                        imageEntries.Add(entry);
                    }
                }

                if (imageEntries.Any())
                {
                    await _profileService.GenerateProfileAsync(categoryName, imageEntries);
                    profilesProcessedInThisBranch++;
                    SimpleFileLogger.Log($"ProcessDirectoryForProfileCreationAsync: Profil '{categoryName}' utworzony/zaktualizowany z {imageEntries.Count} obrazami z folderu '{currentDirPath}'.");
                }
            }
            else
            {
                if (_profileService.GetProfile(categoryName) != null)
                {
                    await _profileService.GenerateProfileAsync(categoryName, new List<ImageFileEntry>());
                    SimpleFileLogger.Log($"ProcessDirectoryForProfileCreationAsync: Folder '{currentDirPath}' dla profilu '{categoryName}' jest pusty. Profil (jeśli istniał) został wyczyszczony.");
                }
                else
                {
                    SimpleFileLogger.Log($"ProcessDirectoryForProfileCreationAsync: Folder '{currentDirPath}' jest pusty i profil '{categoryName}' nie istnieje. Pomijanie tworzenia profilu.");
                }
            }

            try
            {
                foreach (var subDirPath in Directory.GetDirectories(currentDirPath))
                {
                    string subDirName = Path.GetFileName(subDirPath);
                    if (configuredMixedFolderNames.Contains(subDirName))
                    {
                        SimpleFileLogger.Log($"ProcessDirectoryForProfileCreationAsync: Pomijanie podfolderu '{subDirName}' w '{currentDirPath}' (jest na liście folderów Mix).");
                        continue;
                    }
                    profilesProcessedInThisBranch += await ProcessDirectoryForProfileCreationAsync(subDirPath, modelName, currentCategoryPathParts, configuredMixedFolderNames);
                }
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Błąd podczas przetwarzania podfolderów dla '{currentDirPath}'", ex);
            }

            return profilesProcessedInThisBranch;
        }

        private async Task<Models.ProposedMove?> CreateProposedMoveAsync(ImageFileEntry sourceImageEntry, CategoryProfile suggestedProfileData, double similarityToCentroid, string modelDirectoryPath, float[] sourceEmbedding)
        {
            var (modelNameFromProfile, characterFullNameFromProfile) = ParseCategoryName(suggestedProfileData.CategoryName);
            string targetCharacterFolderName = SanitizeFolderName(characterFullNameFromProfile);
            string targetCharacterFolder = Path.Combine(modelDirectoryPath, targetCharacterFolderName);

            Directory.CreateDirectory(targetCharacterFolder);
            string proposedPathIfCopiedWithSourceName = Path.Combine(targetCharacterFolder, sourceImageEntry.FileName);

            ImageFileEntry? bestMatchingTargetInFolder = null;
            double maxSimilarityToExistingInFolder = 0.0;

            foreach (string existingFilePathInTarget in Directory.EnumerateFiles(targetCharacterFolder)
                .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f))))
            {
                if (string.Equals(Path.GetFullPath(existingFilePathInTarget), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase)) continue;

                ImageFileEntry? existingFileMeta = await _imageMetadataService.ExtractMetadataAsync(existingFilePathInTarget);
                float[]? existingEmbedding = await _profileService.GetImageEmbeddingAsync(existingFilePathInTarget);

                if (existingFileMeta != null && existingEmbedding != null)
                {
                    double currentSimilarity = Utils.MathUtils.CalculateCosineSimilarity(sourceEmbedding, existingEmbedding);
                    if (currentSimilarity > maxSimilarityToExistingInFolder)
                    {
                        maxSimilarityToExistingInFolder = currentSimilarity;
                        bestMatchingTargetInFolder = existingFileMeta;
                    }
                }
            }

            ProposedMoveActionType actionType;
            string finalProposedTargetPath = proposedPathIfCopiedWithSourceName;
            ImageFileEntry? finalTargetImageForDisplay = null;
            double displaySimilarity = similarityToCentroid;

            if (bestMatchingTargetInFolder != null && maxSimilarityToExistingInFolder >= DUPLICATE_SIMILARITY_THRESHOLD)
            {
                finalTargetImageForDisplay = bestMatchingTargetInFolder;
                displaySimilarity = maxSimilarityToExistingInFolder;
                finalProposedTargetPath = bestMatchingTargetInFolder.FilePath;

                long sourceRes = (long)sourceImageEntry.Width * sourceImageEntry.Height;
                long targetRes = (long)bestMatchingTargetInFolder.Width * bestMatchingTargetInFolder.Height;
                long sourceSize = new FileInfo(sourceImageEntry.FilePath).Length;
                long targetSize = new FileInfo(bestMatchingTargetInFolder.FilePath).Length;

                if (sourceRes > targetRes || (sourceRes == targetRes && sourceSize > targetSize))
                {
                    actionType = ProposedMoveActionType.OverwriteExisting;
                }
                else
                {
                    actionType = ProposedMoveActionType.KeepExistingDeleteSource;
                }
            }
            else
            {
                if (File.Exists(proposedPathIfCopiedWithSourceName) &&
                    !string.Equals(Path.GetFullPath(proposedPathIfCopiedWithSourceName), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    finalTargetImageForDisplay = await _imageMetadataService.ExtractMetadataAsync(proposedPathIfCopiedWithSourceName);
                    actionType = ProposedMoveActionType.ConflictKeepBoth;
                }
                else
                {
                    actionType = ProposedMoveActionType.CopyNew;
                }
            }

            if (actionType == ProposedMoveActionType.KeepExistingDeleteSource &&
                string.Equals(Path.GetFullPath(sourceImageEntry.FilePath), Path.GetFullPath(finalProposedTargetPath), StringComparison.OrdinalIgnoreCase))
            {
                SimpleFileLogger.Log($"CreateProposedMoveAsync: Pomijanie tworzenia ruchu dla {sourceImageEntry.FileName}, akcja KeepExistingDeleteSource wskazuje na ten sam plik.");
                return null;
            }
            return new Models.ProposedMove(sourceImageEntry, finalTargetImageForDisplay, finalProposedTargetPath, displaySimilarity, suggestedProfileData.CategoryName, actionType);
        }

        private async Task ExecuteMatchModelSpecificAsync(object? parameter)
        {
            SimpleFileLogger.Log("ExecuteMatchModelSpecificAsync: Rozpoczęto wykonanie.");
            if (!(parameter is ModelDisplayViewModel modelVM))
            {
                SimpleFileLogger.LogWarning("ExecuteMatchModelSpecificAsync: Parametr nie jest ModelDisplayViewModel. Komenda nie zostanie wykonana.");
                StatusMessage = "Błąd: Nie wybrano modelki do analizy.";
                return;
            }
            SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Przetwarzanie dla modelki: {modelVM.ModelName}");
            IsBusy = true;
            try
            {
                if (!await Application.Current.Dispatcher.InvokeAsync(() => CanExecuteMatchModelSpecific(modelVM)))
                {
                    SimpleFileLogger.LogWarning($"ExecuteMatchModelSpecificAsync: Warunek CanExecuteMatchModelSpecific zwrócił false dla modelki {modelVM.ModelName} (sprawdzone w wątku UI).");
                    StatusMessage = $"Nie można uruchomić dopasowania dla {modelVM.ModelName} - sprawdź konfigurację.";
                    MessageBox.Show($"Nie można uruchomić dopasowania dla '{modelVM.ModelName}'.\nSprawdź, czy:\n- Ścieżka biblioteki jest poprawna.\n- Modelka ma zdefiniowane profile postaci.\n- Zdefiniowano nazwy folderów źródłowych (Mix).", "Nie można wykonać", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var configuredMixedFolderNames = new HashSet<string>(
                    SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(name => name.Trim()),
                    StringComparer.OrdinalIgnoreCase);

                if (!configuredMixedFolderNames.Any())
                {
                    SimpleFileLogger.LogWarning("ExecuteMatchModelSpecificAsync: Brak zdefiniowanych folderów źródłowych (Mix).");
                    MessageBox.Show("Zdefiniuj nazwy folderów źródłowych (np. Mix, Unsorted) w ustawieniach zaawansowanych.", "Brak folderów źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Skonfigurowane foldery Mix: {string.Join(", ", configuredMixedFolderNames)}");


                StatusMessage = $"Obliczanie sugestii dla modelki: {modelVM.ModelName}...";
                var currentModelProposedMoves = new List<Models.ProposedMove>();
                string modelDirectoryPath = Path.Combine(LibraryRootPath, modelVM.ModelName);

                SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Ścieżka do folderu modelki: {modelDirectoryPath}");

                modelVM.PendingSuggestionsCount = 0;
                foreach (var charProfile in modelVM.CharacterProfiles) charProfile.PendingSuggestionsCount = 0;

                int imagesFoundInMixFolders = 0;
                int imagesWithEmbeddings = 0;
                int suggestionsMade = 0;

                try
                {
                    foreach (var mixFolderNamePattern in configuredMixedFolderNames)
                    {
                        string mixFolderPath = Path.Combine(modelDirectoryPath, mixFolderNamePattern);
                        SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Sprawdzanie folderu Mix: {mixFolderPath}");
                        if (Directory.Exists(mixFolderPath))
                        {
                            SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Skanowanie folderu Mix: {mixFolderPath}");
                            var imagePathsInMix = await _fileScannerService.ScanDirectoryAsync(mixFolderPath);
                            SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Znaleziono {imagePathsInMix.Count} obrazów w {mixFolderPath}.");
                            imagesFoundInMixFolders += imagePathsInMix.Count;

                            foreach (var imagePath in imagePathsInMix)
                            {
                                ImageFileEntry? sourceImageEntry = await _imageMetadataService.ExtractMetadataAsync(imagePath);
                                if (sourceImageEntry == null)
                                {
                                    SimpleFileLogger.LogWarning($"ExecuteMatchModelSpecificAsync: Nie udało się odczytać metadanych dla: {imagePath}");
                                    continue;
                                }
                                float[]? sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceImageEntry.FilePath);
                                if (sourceEmbedding == null)
                                {
                                    SimpleFileLogger.LogWarning($"ExecuteMatchModelSpecificAsync: Nie udało się uzyskać embeddingu dla: {sourceImageEntry.FilePath}");
                                    continue;
                                }
                                imagesWithEmbeddings++;

                                var suggestionTuple = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName);
                                if (suggestionTuple != null)
                                {
                                    SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Sugestia dla '{sourceImageEntry.FileName}' -> '{suggestionTuple.Item1.CategoryName}' (Podobieństwo do centroidu: {suggestionTuple.Item2:F4})");
                                    Models.ProposedMove? move = await CreateProposedMoveAsync(sourceImageEntry, suggestionTuple.Item1, suggestionTuple.Item2, modelDirectoryPath, sourceEmbedding);
                                    if (move != null)
                                    {
                                        currentModelProposedMoves.Add(move);
                                        suggestionsMade++;
                                    }
                                    else
                                    {
                                        SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: CreateProposedMoveAsync zwrócił null dla '{sourceImageEntry.FileName}' i sugestii '{suggestionTuple.Item1.CategoryName}'.");
                                    }
                                }
                                else
                                {
                                    SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Brak sugestii (powyżej progu) dla '{sourceImageEntry.FileName}' w kontekście modelki '{modelVM.ModelName}'.");
                                }
                            }
                        }
                        else
                        {
                            SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Folder Mix '{mixFolderPath}' nie istnieje, pomijanie.");
                        }
                    }
                    SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Zakończono skanowanie folderów Mix. Obrazów znalezionych: {imagesFoundInMixFolders}, z embeddingami: {imagesWithEmbeddings}, wygenerowanych propozycji: {suggestionsMade}.");

                    _lastModelSpecificSuggestions = new List<Models.ProposedMove>(currentModelProposedMoves);
                    _lastScannedModelNameForSuggestions = modelVM.ModelName;
                    RefreshPendingSuggestionCountsFromCache();

                    StatusMessage = $"Zakończono obliczanie sugestii dla '{modelVM.ModelName}'. Znaleziono: {modelVM.PendingSuggestionsCount} potencjalnych dopasowań (powyżej progu).";
                    SimpleFileLogger.Log(StatusMessage);

                    if (modelVM.PendingSuggestionsCount > 0)
                    {
                        MessageBox.Show($"Zakończono obliczanie sugestii dla modelki '{modelVM.ModelName}'.\nZnaleziono {modelVM.PendingSuggestionsCount} potencjalnych dopasowań (powyżej progu {SuggestionSimilarityThreshold:F2}).\n\nKliknij prawym przyciskiem na konkretnej postaci, aby sprawdzić jej indywidualne sugestie i przenieść pliki.", "Obliczanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show($"Nie znaleziono żadnych sugestii dla modelki '{modelVM.ModelName}' (powyżej progu {SuggestionSimilarityThreshold:F2}).\nSprawdź, czy w folderach Mix są obrazy i czy profile postaci są poprawnie zdefiniowane.", "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Błąd podczas obliczania sugestii dla '{modelVM.ModelName}': {ex.Message}";
                    SimpleFileLogger.LogError($"Błąd krytyczny w ExecuteMatchModelSpecificAsync dla '{modelVM.ModelName}'", ex);
                    MessageBox.Show($"Wystąpił błąd krytyczny: {ex.Message}", "Błąd Obliczania Sugestii", MessageBoxButton.OK, MessageBoxImage.Error);
                    ClearModelSpecificSuggestionsCache();
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteSuggestImagesAsync(object? parameter = null)
        {
            ClearModelSpecificSuggestionsCache();
            if (!CanExecuteSuggestImages(null)) { return; }
            IsBusy = true;
            try
            {
                var configuredMixedFolderNames = new HashSet<string>(
                    SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(name => name.Trim()),
                    StringComparer.OrdinalIgnoreCase);
                if (!configuredMixedFolderNames.Any())
                {
                    MessageBox.Show("Zdefiniuj nazwy folderów źródłowych (np. Mix, Unsorted) w ustawieniach zaawansowanych.", "Brak folderów źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                StatusMessage = "Przeszukiwanie folderów źródłowych we wszystkich modelkach (Globalne Sugestie)...";
                var allProposedMovesAcrossModels = new List<Models.ProposedMove>();

                foreach (var mVM_iterator in HierarchicalProfilesList)
                {
                    mVM_iterator.PendingSuggestionsCount = 0;
                    foreach (var cp_iterator in mVM_iterator.CharacterProfiles) cp_iterator.PendingSuggestionsCount = 0;
                }


                foreach (var modelVM_iterator in HierarchicalProfilesList)
                {
                    string modelDirectoryPath = Path.Combine(LibraryRootPath, modelVM_iterator.ModelName);
                    if (!Directory.Exists(modelDirectoryPath) || !modelVM_iterator.HasCharacterProfiles) continue;

                    SimpleFileLogger.Log($"ExecuteSuggestImagesAsync (Global): Przetwarzanie modelki '{modelVM_iterator.ModelName}'");
                    foreach (var mixFolderNamePattern in configuredMixedFolderNames)
                    {
                        string mixFolderPath = Path.Combine(modelDirectoryPath, mixFolderNamePattern);
                        if (Directory.Exists(mixFolderPath))
                        {
                            SimpleFileLogger.Log($"ExecuteSuggestImagesAsync (Global): Skanowanie folderu Mix: {mixFolderPath}");
                            foreach (var imagePath in await _fileScannerService.ScanDirectoryAsync(mixFolderPath))
                            {
                                ImageFileEntry? sourceImageEntry = await _imageMetadataService.ExtractMetadataAsync(imagePath);
                                if (sourceImageEntry == null) continue;
                                float[]? sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceImageEntry.FilePath);
                                if (sourceEmbedding == null) continue;
                                var suggestionTuple = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM_iterator.ModelName);
                                if (suggestionTuple != null)
                                {
                                    Models.ProposedMove? move = await CreateProposedMoveAsync(sourceImageEntry, suggestionTuple.Item1, suggestionTuple.Item2, modelDirectoryPath, sourceEmbedding);
                                    if (move != null)
                                    {
                                        allProposedMovesAcrossModels.Add(move);
                                    }
                                }
                            }
                        }
                    }
                }

                StatusMessage = $"Zakończono globalne skanowanie. Znaleziono {allProposedMovesAcrossModels.Count(m => m.Similarity >= SuggestionSimilarityThreshold)} sugestii (powyżej progu).";
                SimpleFileLogger.Log(StatusMessage);

                var filteredGlobalMoves = allProposedMovesAcrossModels
                    .Where(m => m.Similarity >= SuggestionSimilarityThreshold)
                    .ToList();

                if (filteredGlobalMoves.Any())
                {
                    var previewVM = new PreviewChangesViewModel(filteredGlobalMoves, SuggestionSimilarityThreshold);
                    var previewWindow = new PreviewChangesWindow { DataContext = previewVM };
                    previewWindow.Owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive) ?? Application.Current.MainWindow;
                    if (previewWindow is Views.PreviewChangesWindow actualTypedWindow) { actualTypedWindow.SetCloseAction(previewVM); }

                    bool? dialogOutcome = previewWindow.ShowDialog();
                    if (dialogOutcome == true)
                    {
                        HandleApprovedMoves(previewVM.GetApprovedMoves(), null, null); // HandleApprovedMoves jest async void, więc IsBusy zostanie obsłużone wewnątrz
                    }
                    else
                    {
                        StatusMessage = "Anulowano zmiany (Globalne Sugestie).";
                        SimpleFileLogger.Log(StatusMessage);
                    }
                    ClearModelSpecificSuggestionsCache();
                }
                else
                {
                    MessageBox.Show("Nie znaleziono żadnych pasujących obrazów (powyżej progu) podczas globalnego skanowania.", "Brak sugestii (Globalne)", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Błąd podczas globalnego wyszukiwania sugestii: {ex.Message}";
                SimpleFileLogger.LogError("Błąd w ExecuteSuggestImagesAsync (Global)", ex);
                ClearModelSpecificSuggestionsCache();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteCheckCharacterSuggestionsAsync(object? parameter)
        {
            if (!(parameter is CategoryProfile characterProfile))
            {
                SimpleFileLogger.LogWarning("ExecuteCheckCharacterSuggestionsAsync: Parametr nie jest CategoryProfile.");
                return;
            }
            SimpleFileLogger.Log($"ExecuteCheckCharacterSuggestionsAsync: Rozpoczęto dla postaci '{characterProfile.CategoryName}'.");
            IsBusy = true;
            try
            {
                if (!CanExecuteCheckCharacterSuggestions(characterProfile)) // CanExecute powinno być sprawdzone przed wywołaniem przez UI, ale dodatkowe sprawdzenie nie zaszkodzi
                {
                    SimpleFileLogger.LogWarning($"ExecuteCheckCharacterSuggestionsAsync: Warunek CanExecute niespełniony dla '{characterProfile.CategoryName}'.");
                    MessageBox.Show($"Nie można sprawdzić sugestii dla '{characterProfile.CategoryName}'.\nMożliwe przyczyny: brak ścieżki biblioteki, brak folderów źródłowych lub profil postaci nie ma obliczonego centroidu.",
                                    "Nie można wykonać",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                    return;
                }

                string modelName = _profileService.GetModelNameFromCategory(characterProfile.CategoryName);
                List<Models.ProposedMove> suggestionsForThisCharacter;

                if (_lastScannedModelNameForSuggestions == modelName && _lastModelSpecificSuggestions.Any())
                {
                    SimpleFileLogger.Log($"ExecuteCheckCharacterSuggestionsAsync: Używanie istniejącego cache'u sugestii dla modelki '{modelName}'.");
                    suggestionsForThisCharacter = _lastModelSpecificSuggestions
                        .Where(move => move.TargetCategoryProfileName.Equals(characterProfile.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                                       move.Similarity >= SuggestionSimilarityThreshold)
                        .ToList();
                    SimpleFileLogger.Log($"ExecuteCheckCharacterSuggestionsAsync: Z cache'u dla '{characterProfile.CategoryName}' znaleziono {suggestionsForThisCharacter.Count} sugestii (po progu).");
                }
                else
                {
                    SimpleFileLogger.Log($"ExecuteCheckCharacterSuggestionsAsync: Cache nieaktualny lub pusty dla modelki '{modelName}'. Wykonywanie dedykowanego skanowania dla postaci '{characterProfile.CategoryName}'.");
                    StatusMessage = $"Skanowanie dedykowane dla '{characterProfile.CategoryName}'...";
                    suggestionsForThisCharacter = new List<Models.ProposedMove>();

                    var configuredMixedFolderNames = new HashSet<string>(
                        SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                             .Select(name => name.Trim()),
                        StringComparer.OrdinalIgnoreCase);
                    string modelDirectoryPath = Path.Combine(LibraryRootPath, modelName);

                    if (Directory.Exists(modelDirectoryPath) && configuredMixedFolderNames.Any())
                    {
                        foreach (var mixFolderNamePattern in configuredMixedFolderNames)
                        {
                            string mixFolderPath = Path.Combine(modelDirectoryPath, mixFolderNamePattern);
                            if (Directory.Exists(mixFolderPath))
                            {
                                foreach (var imagePath in await _fileScannerService.ScanDirectoryAsync(mixFolderPath))
                                {
                                    ImageFileEntry? sourceImageEntry = await _imageMetadataService.ExtractMetadataAsync(imagePath);
                                    if (sourceImageEntry == null) continue;
                                    float[]? sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceImageEntry.FilePath);

                                    if (sourceEmbedding == null || characterProfile.CentroidEmbedding == null) continue;

                                    double similarityToCharCentroid = Utils.MathUtils.CalculateCosineSimilarity(sourceEmbedding, characterProfile.CentroidEmbedding);
                                    if (similarityToCharCentroid >= SuggestionSimilarityThreshold)
                                    {
                                        Models.ProposedMove? move = await CreateProposedMoveAsync(sourceImageEntry, characterProfile, similarityToCharCentroid, modelDirectoryPath, sourceEmbedding);
                                        if (move != null && move.TargetCategoryProfileName == characterProfile.CategoryName)
                                        {
                                            suggestionsForThisCharacter.Add(move);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    StatusMessage = $"Zakończono dedykowane skanowanie dla '{characterProfile.CategoryName}'. Znaleziono: {suggestionsForThisCharacter.Count}.";
                    SimpleFileLogger.Log(StatusMessage);

                    if (_lastScannedModelNameForSuggestions != modelName && _lastScannedModelNameForSuggestions != null)
                    {
                        SimpleFileLogger.Log($"ExecuteCheckCharacterSuggestionsAsync (Fallback): Zmiana modelu z '{_lastScannedModelNameForSuggestions}' na '{modelName}'. Czyszczenie _lastModelSpecificSuggestions.");
                        _lastModelSpecificSuggestions.Clear();
                    }
                    _lastModelSpecificSuggestions.RemoveAll(m => m.TargetCategoryProfileName.Equals(characterProfile.CategoryName, StringComparison.OrdinalIgnoreCase));
                    _lastModelSpecificSuggestions.AddRange(suggestionsForThisCharacter);
                    _lastScannedModelNameForSuggestions = modelName;
                }

                var actualSuggestionsToShowInDialog = suggestionsForThisCharacter;

                if (actualSuggestionsToShowInDialog.Any())
                {
                    var previewVM = new PreviewChangesViewModel(actualSuggestionsToShowInDialog, SuggestionSimilarityThreshold);
                    var previewWindow = new PreviewChangesWindow { DataContext = previewVM };
                    previewWindow.Owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive) ?? Application.Current.MainWindow;
                    if (previewWindow is Views.PreviewChangesWindow actualTypedWindow) { actualTypedWindow.SetCloseAction(previewVM); }

                    bool? dialogOutcome = previewWindow.ShowDialog();
                    if (dialogOutcome == true)
                    {
                        ModelDisplayViewModel? parentModelVM = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName == modelName);
                        HandleApprovedMoves(previewVM.GetApprovedMoves(), parentModelVM, characterProfile); // async void
                    }
                    else
                    {
                        StatusMessage = $"Anulowano sprawdzanie sugestii dla '{characterProfile.CategoryName}'.";
                        SimpleFileLogger.Log(StatusMessage);
                    }
                }
                else
                {
                    MessageBox.Show($"Nie znaleziono sugestii (powyżej progu {SuggestionSimilarityThreshold:F2}) dla '{characterProfile.CategoryName}'.", "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                RefreshPendingSuggestionCountsFromCache();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RefreshPendingSuggestionCountsFromCache()
        {
            SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache: Próba odświeżenia liczników. Obecny próg: {SuggestionSimilarityThreshold:F4}. Nazwa cachowanej modelki: '{_lastScannedModelNameForSuggestions ?? "BRAK"}'. Liczba sugestii w _lastModelSpecificSuggestions: {_lastModelSpecificSuggestions?.Count ?? -1}");

            if (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions))
            {
                SimpleFileLogger.Log("RefreshPendingSuggestionCountsFromCache: Brak nazwy cachowanej modelki lub cache wyczyszczony. Zerowanie liczników dla wszystkich modelek.");
                foreach (var modelVM_iterator in HierarchicalProfilesList)
                {
                    if (modelVM_iterator.PendingSuggestionsCount != 0)
                    {
                        SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache: Zerowanie licznika dla modelki '{modelVM_iterator.ModelName}' (miała {modelVM_iterator.PendingSuggestionsCount}).");
                    }
                    modelVM_iterator.PendingSuggestionsCount = 0;
                    foreach (var charProfile_iterator in modelVM_iterator.CharacterProfiles)
                    {
                        charProfile_iterator.PendingSuggestionsCount = 0;
                    }
                }
                return;
            }

            var modelVMToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(_lastScannedModelNameForSuggestions, StringComparison.OrdinalIgnoreCase));

            if (modelVMToUpdate == null)
            {
                SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache: Cachowana modelka '{_lastScannedModelNameForSuggestions}' nie została znaleziona na liście hierarchicznej. Liczniki nie zostaną odświeżone.");
                return;
            }

            SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache: Rozpoczynam odświeżanie dla modelki '{modelVMToUpdate.ModelName}'.");

            foreach (var otherModelVM in HierarchicalProfilesList.Where(m => m.ModelName != modelVMToUpdate.ModelName))
            {
                if (otherModelVM.PendingSuggestionsCount > 0)
                {
                    SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache: Zerowanie licznika dla modelki spoza cache '{otherModelVM.ModelName}' (miała {otherModelVM.PendingSuggestionsCount}).");
                    otherModelVM.PendingSuggestionsCount = 0;
                    foreach (var charProfileOther in otherModelVM.CharacterProfiles)
                    {
                        charProfileOther.PendingSuggestionsCount = 0;
                    }
                }
            }

            int totalSuggestionsForThisModelAfterFilteringByThreshold = 0;
            foreach (var charProfile in modelVMToUpdate.CharacterProfiles)
            {
                int oldCount = charProfile.PendingSuggestionsCount;
                charProfile.PendingSuggestionsCount = _lastModelSpecificSuggestions
                    .Count(move => move.TargetCategoryProfileName.Equals(charProfile.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                                   move.Similarity >= SuggestionSimilarityThreshold);

                if (oldCount != charProfile.PendingSuggestionsCount)
                {
                    SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache: Postać '{charProfile.CategoryName}', licznik zmieniony z {oldCount} na {charProfile.PendingSuggestionsCount}.");
                }
                totalSuggestionsForThisModelAfterFilteringByThreshold += charProfile.PendingSuggestionsCount;
            }

            if (modelVMToUpdate.PendingSuggestionsCount != totalSuggestionsForThisModelAfterFilteringByThreshold)
            {
                SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache: Modelka '{modelVMToUpdate.ModelName}', suma dla modelu zmieniona z {modelVMToUpdate.PendingSuggestionsCount} na {totalSuggestionsForThisModelAfterFilteringByThreshold}.");
            }
            modelVMToUpdate.PendingSuggestionsCount = totalSuggestionsForThisModelAfterFilteringByThreshold;

            SimpleFileLogger.Log($"RefreshPendingSuggestionCountsFromCache: Zakończono odświeżanie dla modelki '{modelVMToUpdate.ModelName}'. Nowy PendingSuggestionsCount (suma postaci): {modelVMToUpdate.PendingSuggestionsCount}.");
        }


        private async void HandleApprovedMoves(List<Models.ProposedMove> approvedMoves, ModelDisplayViewModel? specificModelVM, CategoryProfile? specificCharacterProfile)
        {
            StatusMessage = "Przetwarzanie zatwierdzonych operacji...";
            IsBusy = true;
            try
            {
                int successCount = 0, copyErrorCount = 0, deleteErrorCount = 0, skippedDueToQualityCount = 0, otherSkippedCount = 0;
                HashSet<string> changedProfileNamesForRefresh = new HashSet<string>();
                List<string> processedSourceFilePaths = new List<string>();

                foreach (var move in approvedMoves)
                {
                    string sourceFilePath = move.SourceImage.FilePath;
                    string finalTargetPathForOperation = move.ProposedTargetPath;
                    ProposedMoveActionType action = move.Action;
                    bool operationOnFileSuccessful = false;
                    bool sourceFileToDelete = false;

                    SimpleFileLogger.Log($"HandleApprovedMoves: Akcja dla '{sourceFilePath}' -> '{finalTargetPathForOperation}' to: {action}");

                    try
                    {
                        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                        {
                            SimpleFileLogger.Log($"Pominięto (HandleApprovedMoves): Plik źródłowy '{sourceFilePath}' nie istnieje lub ścieżka jest pusta.");
                            otherSkippedCount++;
                            continue;
                        }
                        string targetDir = Path.GetDirectoryName(finalTargetPathForOperation);
                        if (string.IsNullOrEmpty(targetDir))
                        {
                            SimpleFileLogger.Log($"Pominięto (HandleApprovedMoves): Nie można ustalić katalogu docelowego dla '{finalTargetPathForOperation}'.");
                            otherSkippedCount++;
                            continue;
                        }
                        Directory.CreateDirectory(targetDir);

                        switch (action)
                        {
                            case ProposedMoveActionType.CopyNew:
                                if (File.Exists(finalTargetPathForOperation))
                                {
                                    SimpleFileLogger.Log($"OSTRZEŻENIE (CopyNew): Plik docelowy {finalTargetPathForOperation} istnieje. Generowanie unikalnej nazwy.");
                                    finalTargetPathForOperation = GenerateUniqueTargetPath(targetDir, Path.GetFileName(sourceFilePath), "_new");
                                }
                                File.Copy(sourceFilePath, finalTargetPathForOperation, false);
                                SimpleFileLogger.Log($"Skopiowano (CopyNew): '{sourceFilePath}' -> '{finalTargetPathForOperation}'");
                                operationOnFileSuccessful = true;
                                sourceFileToDelete = true;
                                break;

                            case ProposedMoveActionType.OverwriteExisting:
                                SimpleFileLogger.Log($"Nadpisywanie (OverwriteExisting): '{finalTargetPathForOperation}' plikiem '{sourceFilePath}'");
                                GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(100);
                                File.Copy(sourceFilePath, finalTargetPathForOperation, true);
                                operationOnFileSuccessful = true;
                                sourceFileToDelete = true;
                                break;

                            case ProposedMoveActionType.KeepExistingDeleteSource:
                                SimpleFileLogger.Log($"Zachowano istniejący (KeepExistingDeleteSource): '{finalTargetPathForOperation}'. Plik źródłowy '{sourceFilePath}' zostanie usunięty.");
                                operationOnFileSuccessful = true;
                                sourceFileToDelete = true;
                                skippedDueToQualityCount++;
                                break;

                            case ProposedMoveActionType.ConflictKeepBoth:
                                ImageFileEntry? sourceMetaConflict = move.SourceImage;
                                ImageFileEntry? targetMetaConflict = move.TargetImage;
                                string logPrefixConflict = $"Konflikt (ConflictKeepBoth) dla S: '{sourceFilePath}' vs T: '{finalTargetPathForOperation}'";

                                bool canCompareByResolution = sourceMetaConflict != null && targetMetaConflict != null &&
                                                              targetMetaConflict.FilePath.Equals(finalTargetPathForOperation, StringComparison.OrdinalIgnoreCase) &&
                                                              sourceMetaConflict.Width > 0 && sourceMetaConflict.Height > 0 &&
                                                              targetMetaConflict.Width > 0 && targetMetaConflict.Height > 0;

                                if (canCompareByResolution)
                                {
                                    long sourceResC = (long)sourceMetaConflict.Width * sourceMetaConflict.Height;
                                    long targetResC = (long)targetMetaConflict.Width * targetMetaConflict.Height;
                                    long sourceSizeC = new FileInfo(sourceMetaConflict.FilePath).Length;
                                    long targetSizeC = new FileInfo(targetMetaConflict.FilePath).Length;


                                    if (sourceResC > targetResC || (sourceResC == targetResC && sourceSizeC > targetSizeC))
                                    {
                                        SimpleFileLogger.Log($"{logPrefixConflict}: Źródło lepsze (rozdzielczość/rozmiar). Nadpisywanie.");
                                        GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(100);
                                        File.Copy(sourceFilePath, finalTargetPathForOperation, true);
                                        operationOnFileSuccessful = true;
                                    }
                                    else
                                    {
                                        SimpleFileLogger.Log($"{logPrefixConflict}: Źródło nie jest lepsze (rozdzielczość/rozmiar). Istniejący plik '{finalTargetPathForOperation}' pozostaje. Źródło zostanie usunięte.");
                                        operationOnFileSuccessful = true;
                                        skippedDueToQualityCount++;
                                    }
                                }
                                else
                                {
                                    SimpleFileLogger.Log($"{logPrefixConflict}: Brak metadanych wymiarów do pełnego porównania. Porównywanie na podstawie rozmiaru pliku.");
                                    try
                                    {
                                        if (!File.Exists(finalTargetPathForOperation))
                                        {
                                            SimpleFileLogger.LogWarning($"{logPrefixConflict}: Plik docelowy '{finalTargetPathForOperation}' wskazany w TargetImage nie istnieje na dysku! Kopiowanie źródła jako nowy.");
                                            GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(100);
                                            File.Copy(sourceFilePath, finalTargetPathForOperation, false);
                                            operationOnFileSuccessful = true;
                                        }
                                        else
                                        {
                                            long sourceSizeFallback = new FileInfo(sourceFilePath).Length;
                                            long targetSizeFallback = new FileInfo(finalTargetPathForOperation).Length;

                                            if (sourceSizeFallback > targetSizeFallback)
                                            {
                                                SimpleFileLogger.Log($"{logPrefixConflict}: Źródło większe (rozmiar pliku: {sourceSizeFallback} > {targetSizeFallback}). Nadpisywanie.");
                                                GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(100);
                                                File.Copy(sourceFilePath, finalTargetPathForOperation, true);
                                                operationOnFileSuccessful = true;
                                            }
                                            else
                                            {
                                                SimpleFileLogger.Log($"{logPrefixConflict}: Źródło nie jest większe (rozmiar pliku: {sourceSizeFallback} <= {targetSizeFallback}). Istniejący plik '{finalTargetPathForOperation}' pozostaje. Źródło zostanie usunięte.");
                                                operationOnFileSuccessful = true;
                                                skippedDueToQualityCount++;
                                            }
                                        }
                                    }
                                    catch (Exception fileSizeEx)
                                    {
                                        SimpleFileLogger.LogError($"{logPrefixConflict}: Błąd podczas porównywania rozmiarów plików. Zachowuję ostrożność - istniejący plik '{finalTargetPathForOperation}' pozostaje. Źródło zostanie usunięte.", fileSizeEx);
                                        operationOnFileSuccessful = true;
                                        otherSkippedCount++;
                                    }
                                }
                                sourceFileToDelete = true;
                                break;
                        }

                        if (operationOnFileSuccessful)
                        {
                            successCount++;
                            processedSourceFilePaths.Add(sourceFilePath);

                            if (sourceFileToDelete)
                            {
                                bool deleteOk = false;
                                for (int i = 0; i < 3; i++)
                                {
                                    try
                                    {
                                        if (i > 0) await Task.Delay(200 * (i + 1));
                                        if (File.Exists(sourceFilePath))
                                        {
                                            File.Delete(sourceFilePath);
                                            SimpleFileLogger.Log($"Usunięto plik źródłowy: {sourceFilePath}");
                                        }
                                        else
                                        {
                                            SimpleFileLogger.Log($"Plik źródłowy {sourceFilePath} już nie istniał przed próbą usunięcia.");
                                        }
                                        deleteOk = true;
                                        break;
                                    }
                                    catch (IOException ioEx)
                                    {
                                        if (i == 2) SimpleFileLogger.LogError($"Nie udało się usunąć pliku źródłowego {sourceFilePath} po 3 próbach (IOExc).", ioEx);
                                    }
                                    catch (Exception ex)
                                    {
                                        SimpleFileLogger.LogError($"Błąd przy usuwaniu {sourceFilePath}", ex);
                                        break;
                                    }
                                }
                                if (!deleteOk && File.Exists(sourceFilePath)) deleteErrorCount++;
                            }
                            changedProfileNamesForRefresh.Add(move.TargetCategoryProfileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        copyErrorCount++;
                        SimpleFileLogger.LogError($"HandleApprovedMoves: Błąd ogólny operacji dla '{sourceFilePath}' -> '{finalTargetPathForOperation}'. Akcja: {action}", ex);
                        operationOnFileSuccessful = false;
                    }
                }

                if (_lastScannedModelNameForSuggestions != null && processedSourceFilePaths.Any())
                {
                    int beforeRemoveCount = _lastModelSpecificSuggestions.Count;
                    _lastModelSpecificSuggestions.RemoveAll(s => processedSourceFilePaths.Contains(s.SourceImage.FilePath));
                    SimpleFileLogger.Log($"HandleApprovedMoves: Usunięto {beforeRemoveCount - _lastModelSpecificSuggestions.Count} przetworzonych sugestii z _lastModelSpecificSuggestions. Pozostało: {_lastModelSpecificSuggestions.Count}.");
                }

                SimpleFileLogger.Log("HandleApprovedMoves: Odświeżanie liczników sugestii (1) na podstawie cache'u pomniejszonego o przetworzone elementy.");
                RefreshPendingSuggestionCountsFromCache();

                if (changedProfileNamesForRefresh.Any())
                {
                    SimpleFileLogger.Log("HandleApprovedMoves: Rozpoczynanie odświeżania profili, które uległy zmianie.");
                    _isRefreshingProfilesPostMove = true;
                    await RefreshProfilesAsync(changedProfileNamesForRefresh.Distinct().ToList()); // RefreshProfilesAsync również ustawi IsBusy
                    _isRefreshingProfilesPostMove = false;
                    SimpleFileLogger.Log("HandleApprovedMoves: Zakończono odświeżanie profili.");

                    SimpleFileLogger.Log("HandleApprovedMoves: Ponowne odświeżanie liczników sugestii (2) po przebudowie profili, używając zachowanego cache'u sugestii.");
                    RefreshPendingSuggestionCountsFromCache();
                }

                string summaryMsg = $"Operacje zakończone.\nWykonano akcji: {successCount}\n- Pominięto (istniejący plik lepszy/taki sam lub problem z metadanymi): {skippedDueToQualityCount + otherSkippedCount}\nBłędy operacji na plikach (kopiowanie/nadpisywanie): {copyErrorCount}\nBłędy usuwania plików źródłowych: {deleteErrorCount}";
                StatusMessage = $"Zakończono. Wykonano: {successCount}, Pominięto(jakość/meta): {skippedDueToQualityCount + otherSkippedCount}, Bł.op.: {copyErrorCount}, Bł.us.: {deleteErrorCount}.";

                if (changedProfileNamesForRefresh.Any()) StatusMessage += " Profile odświeżone.";
                SimpleFileLogger.Log($"HandleApprovedMoves: {StatusMessage}");
                MessageBox.Show(summaryMsg, "Operacja zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RefreshProfilesAsync(List<string> profileNamesToRefresh)
        {
            if (string.IsNullOrWhiteSpace(LibraryRootPath) || !Directory.Exists(LibraryRootPath))
            {
                SimpleFileLogger.LogWarning("RefreshProfilesAsync: Ścieżka biblioteki nie jest ustawiona lub nie istnieje. Pomijanie odświeżania profili.");
                return;
            }
            await Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = "Odświeżanie zmodyfikowanych profili...");
            SimpleFileLogger.Log($"RefreshProfilesAsync: Rozpoczęto odświeżanie {profileNamesToRefresh.Count} profili.");
            // IsBusy jest już prawdopodobnie true z HandleApprovedMoves, jeśli nie, to można tu ustawić
            bool changedAnythingSignificant = false;
            try
            {
                foreach (var profileName in profileNamesToRefresh)
                {
                    var (model, characterFullName) = ParseCategoryName(profileName);
                    string targetCharacterPathSegment = SanitizeFolderName(characterFullName);
                    string charPath = Path.Combine(LibraryRootPath, SanitizeFolderName(model), targetCharacterPathSegment);

                    if (Directory.Exists(charPath))
                    {
                        SimpleFileLogger.Log($"RefreshProfilesAsync: Odświeżanie profilu '{profileName}' z folderu '{charPath}'.");
                        var entries = await Task.Run(async () =>
                            (await Task.WhenAll(Directory.GetFiles(charPath)
                                .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f)))
                                .Select(p => _imageMetadataService.ExtractMetadataAsync(p))))
                                .Where(e => e != null).ToList()
                        );

                        await _profileService.GenerateProfileAsync(profileName, entries!);
                        changedAnythingSignificant = true;
                    }
                    else if (_profileService.GetProfile(profileName) != null)
                    {
                        SimpleFileLogger.LogWarning($"RefreshProfilesAsync: Folder dla profilu '{profileName}' ('{charPath}') nie istnieje, ale profil tak. Czyszczenie profilu.");
                        await _profileService.GenerateProfileAsync(profileName, new List<ImageFileEntry>());
                        changedAnythingSignificant = true;
                    }
                }

                if (changedAnythingSignificant)
                {
                    SimpleFileLogger.Log("RefreshProfilesAsync: Wykryto znaczące zmiany w profilach, przeładowywanie całej listy profili.");
                    await ExecuteLoadProfilesAsync(); // To obsłuży IsBusy i aktualizację statusu
                }
                else
                {
                    SimpleFileLogger.Log("RefreshProfilesAsync: Nie wykryto znaczących zmian, nie ma potrzeby pełnego przeładowania profili.");
                    await Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = "Profile aktualne.");
                }
            }
            finally
            {
                // IsBusy zostanie zresetowane przez zewnętrzne wywołanie lub ExecuteLoadProfilesAsync
            }
        }

        private string GenerateUniqueTargetPath(string targetDirectory, string originalFileName, string suffixBase = "_conflict")
        {
            string baseName = Path.GetFileNameWithoutExtension(originalFileName);
            string extension = Path.GetExtension(originalFileName);
            int counter = 1;
            string safeBaseName = baseName.Length > 200 ? baseName.Substring(0, 200) : baseName;
            string finalTargetPath;

            do
            {
                string newFileName = $"{safeBaseName}{suffixBase}{counter}{extension}";
                finalTargetPath = Path.Combine(targetDirectory, newFileName);
                if (counter > 9999)
                {
                    newFileName = $"{safeBaseName}_{Guid.NewGuid().ToString("N").Substring(0, 8)}{extension}";
                    finalTargetPath = Path.Combine(targetDirectory, newFileName);
                    SimpleFileLogger.LogWarning($"GenerateUniqueTargetPath: Przekroczono limit prób dla suffixu '{suffixBase}'. Użyto GUID: {newFileName}");
                    break;
                }
                counter++;
            } while (File.Exists(finalTargetPath));

            return finalTargetPath;
        }

        private async Task ExecuteRemoveModelTreeAsync(object? parameter)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) return;

            var result = MessageBox.Show($"Czy na pewno chcesz usunąć całą modelkę '{modelVM.ModelName}' wraz ze wszystkimi jej profilami postaci ({modelVM.CharacterProfiles.Count}) z systemu profili?\n\nTa operacja NIE usuwa plików z dysku.",
                                         "Potwierdź usunięcie modelki", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                StatusMessage = $"Usuwanie modelki '{modelVM.ModelName}'...";
                SimpleFileLogger.Log($"ExecuteRemoveModelTreeAsync: Rozpoczęto usuwanie modelki '{modelVM.ModelName}'.");
                IsBusy = true;
                try
                {
                    bool success = await _profileService.RemoveAllProfilesForModelAsync(modelVM.ModelName);
                    if (success)
                    {
                        StatusMessage = $"Modelka '{modelVM.ModelName}' i jej profile zostały usunięte z systemu profili.";
                        SimpleFileLogger.Log(StatusMessage);
                        if (_lastScannedModelNameForSuggestions == modelVM.ModelName)
                        {
                            ClearModelSpecificSuggestionsCache();
                        }
                        if (SelectedProfile != null && _profileService.GetModelNameFromCategory(SelectedProfile.CategoryName) == modelVM.ModelName)
                        {
                            SelectedProfile = null;
                        }
                        await ExecuteLoadProfilesAsync();
                    }
                    else
                    {
                        StatusMessage = $"Nie udało się całkowicie usunąć modelki '{modelVM.ModelName}' z systemu profili. Sprawdź logi.";
                        SimpleFileLogger.LogError(StatusMessage, null);
                        await ExecuteLoadProfilesAsync(); // Mimo wszystko odśwież, by pokazać obecny stan
                    }
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        private bool CanExecuteAnalyzeModelForSplitting(object? parameter)
        {
            return parameter is ModelDisplayViewModel modelVM && modelVM.HasCharacterProfiles;
        }

        private async Task ExecuteAnalyzeModelForSplittingAsync(object? parameter)
        {
            if (!(parameter is ModelDisplayViewModel modelVM))
            {
                SimpleFileLogger.LogWarning("ExecuteAnalyzeModelForSplittingAsync: Parametr nie jest ModelDisplayViewModel.");
                return;
            }

            StatusMessage = $"Analizowanie profili postaci dla modelki '{modelVM.ModelName}' pod kątem możliwości podziału...";
            SimpleFileLogger.Log($"ExecuteAnalyzeModelForSplittingAsync: Rozpoczęto dla modelki '{modelVM.ModelName}'.");
            IsBusy = true;
            try
            {
                int profilesMarkedForSplit = 0;

                foreach (var existingCharProfile in modelVM.CharacterProfiles)
                {
                    existingCharProfile.HasSplitSuggestion = false;
                }

                foreach (var characterProfile in modelVM.CharacterProfiles.ToList()) // ToList(), aby uniknąć modyfikacji kolekcji podczas iteracji, jeśli by zaszła potrzeba
                {
                    const int minImagesForSplitConsideration = 10;
                    const int minImagesForSignificantSplit = 20;

                    if (characterProfile.SourceImagePaths == null || characterProfile.SourceImagePaths.Count < minImagesForSplitConsideration)
                    {
                        characterProfile.HasSplitSuggestion = false;
                        SimpleFileLogger.Log($"ExecuteAnalyzeModelForSplittingAsync: Profil '{characterProfile.CategoryName}' ma za mało obrazów ({characterProfile.SourceImagePaths?.Count ?? 0} < {minImagesForSplitConsideration}), pomijanie analizy podziału.");
                        continue;
                    }

                    List<float[]> allEmbeddings = new List<float[]>();

                    SimpleFileLogger.Log($"ExecuteAnalyzeModelForSplittingAsync: Analizowanie profilu '{characterProfile.CategoryName}', liczba obrazów źródłowych: {characterProfile.SourceImagePaths.Count}.");

                    foreach (string imagePath in characterProfile.SourceImagePaths)
                    {
                        if (File.Exists(imagePath))
                        {
                            float[]? embedding = await _profileService.GetImageEmbeddingAsync(imagePath);
                            if (embedding != null)
                            {
                                allEmbeddings.Add(embedding);
                            }
                            else
                            {
                                SimpleFileLogger.LogWarning($"ExecuteAnalyzeModelForSplittingAsync: Nie udało się uzyskać embeddingu dla obrazu '{imagePath}' z profilu '{characterProfile.CategoryName}'.");
                            }
                        }
                        else
                        {
                            SimpleFileLogger.LogWarning($"ExecuteAnalyzeModelForSplittingAsync: Obraz '{imagePath}' z profilu '{characterProfile.CategoryName}' nie istnieje.");
                        }
                    }

                    if (allEmbeddings.Count < minImagesForSplitConsideration)
                    {
                        characterProfile.HasSplitSuggestion = false;
                        SimpleFileLogger.Log($"ExecuteAnalyzeModelForSplittingAsync: Profil '{characterProfile.CategoryName}' ma za mało poprawnych embeddingów ({allEmbeddings.Count} < {minImagesForSplitConsideration}), pomijanie analizy podziału.");
                        continue;
                    }

                    // TODO: Bardziej zaawansowana logika klastrowania lub analizy wariancji embeddingów
                    // Na razie, prosty placeholder: jeśli ma wystarczająco dużo obrazów, oznacz do podziału.
                    bool foundSignificantSplit = false;
                    if (allEmbeddings.Count >= minImagesForSignificantSplit)
                    {
                        foundSignificantSplit = true;
                        SimpleFileLogger.Log($"ExecuteAnalyzeModelForSplittingAsync: Profil '{characterProfile.CategoryName}' (liczba przetworzonych obrazów: {allEmbeddings.Count}) oznaczony do podziału (LOGIKA PLACEHOLDERA).");
                    }
                    else
                    {
                        SimpleFileLogger.Log($"ExecuteAnalyzeModelForSplittingAsync: Profil '{characterProfile.CategoryName}' (liczba przetworzonych obrazów: {allEmbeddings.Count}) nie spełnia kryterium placeholderu ({minImagesForSignificantSplit}) do podziału.");
                    }

                    characterProfile.HasSplitSuggestion = foundSignificantSplit;
                    if (foundSignificantSplit)
                    {
                        profilesMarkedForSplit++;
                    }
                }

                StatusMessage = $"Analiza możliwości podziału dla '{modelVM.ModelName}' zakończona. Oznaczono {profilesMarkedForSplit} profili postaci.";
                SimpleFileLogger.Log(StatusMessage);
                if (profilesMarkedForSplit > 0)
                {
                    MessageBox.Show($"Znaleziono {profilesMarkedForSplit} profili postaci dla modelki '{modelVM.ModelName}', które mogą kwalifikować się do podziału (oznaczone literą 'P' w drzewie).\nFunkcjonalność samego podziału (nowe okno, przenoszenie plików) wymaga dalszej implementacji.", "Analiza Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Nie znaleziono profili postaci dla modelki '{modelVM.ModelName}', które jednoznacznie kwalifikowałyby się do podziału na podstawie obecnych (placeholderowych) kryteriów.", "Analiza Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanExecuteOpenSplitProfileDialog(object? parameter)
        {
            return parameter is CategoryProfile characterProfile && characterProfile.HasSplitSuggestion;
        }

        private async Task ExecuteOpenSplitProfileDialogAsync(object? parameter)
        {
            if (!(parameter is CategoryProfile characterProfile))
            {
                SimpleFileLogger.LogWarning("ExecuteOpenSplitProfileDialogAsync: Parametr nie jest CategoryProfile.");
                return;
            }

            StatusMessage = $"Przygotowywanie interfejsu podziału dla profilu '{characterProfile.CategoryName}'...";
            SimpleFileLogger.Log($"ExecuteOpenSplitProfileDialogAsync: Otwieranie okna podziału dla '{characterProfile.CategoryName}'.");
            IsBusy = true;
            try
            {
                List<ImageFileEntry> imagesInProfile = new List<ImageFileEntry>();
                if (characterProfile.SourceImagePaths != null)
                {
                    List<Task> loadTasks = new List<Task>();
                    foreach (var path in characterProfile.SourceImagePaths)
                    {
                        if (File.Exists(path))
                        {
                            var metaEntry = await _imageMetadataService.ExtractMetadataAsync(path);
                            if (metaEntry != null)
                            {
                                imagesInProfile.Add(metaEntry);
                                loadTasks.Add(metaEntry.LoadThumbnailAsync());
                            }
                            else
                            {
                                var entry = new ImageFileEntry { FilePath = path, FileName = Path.GetFileName(path) };
                                imagesInProfile.Add(entry);
                                loadTasks.Add(entry.LoadThumbnailAsync());
                            }
                        }
                    }
                    await Task.WhenAll(loadTasks);
                }

                if (!imagesInProfile.Any())
                {
                    MessageBox.Show("Wybrany profil nie zawiera obrazów do podziału.", "Brak obrazów", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusMessage = "Gotowy.";
                    return;
                }

                List<ImageFileEntry> group1 = imagesInProfile.Take(imagesInProfile.Count / 2).ToList();
                List<ImageFileEntry> group2 = imagesInProfile.Skip(imagesInProfile.Count / 2).ToList();

                string baseName = _profileService.GetCharacterNameFromCategory(characterProfile.CategoryName);
                if (baseName == "General") baseName = _profileService.GetModelNameFromCategory(characterProfile.CategoryName);

                string suggestedName1 = $"{baseName} - Grupa 1";
                string suggestedName2 = $"{baseName} - Grupa 2";

                var splitVM = new SplitProfileViewModel(characterProfile, group1, group2, suggestedName1, suggestedName2);
                var splitWindow = new SplitProfileWindow
                {
                    DataContext = splitVM,
                    Owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive) ?? Application.Current.MainWindow
                };
                splitWindow.SetCloseAction(splitVM);

                bool? dialogResult = splitWindow.ShowDialog();

                if (dialogResult == true)
                {
                    StatusMessage = $"Użytkownik zatwierdził podział dla '{characterProfile.CategoryName}'. (Logika finalizacji do implementacji)";
                    SimpleFileLogger.Log(StatusMessage);
                    MessageBox.Show("Funkcjonalność finalizacji podziału (przenoszenie plików, tworzenie nowych profili) nie jest jeszcze zaimplementowana.", "Do zrobienia", MessageBoxButton.OK, MessageBoxImage.Information);

                    characterProfile.HasSplitSuggestion = false;
                    await ExecuteLoadProfilesAsync();
                }
                else
                {
                    StatusMessage = $"Podział profilu '{characterProfile.CategoryName}' anulowany przez użytkownika.";
                    SimpleFileLogger.Log(StatusMessage);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
} 