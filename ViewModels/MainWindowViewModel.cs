// Plik: ViewModels/MainWindowViewModel.cs
using CosplayManager.Models;
using CosplayManager.Services;
using CosplayManager.ViewModels.Base;
using CosplayManager.Views;
using Microsoft.Win32; // Dla OpenFileDialog
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading; // Dla CancellationToken, jeśli byłoby używane
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
// Upewnij się, że masz using dla Ookii.Dialogs.Wpf, jeśli go używasz
// np. using Ookii.Dialogs.Wpf;


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
        private bool _isRefreshingProfilesPostMove = false; // <<< NOWA FLAGA

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
                }
                if (_selectedProfile == null && oldSelectedProfileName != null &&
                    !_profileService.GetAllProfiles().Any(p => p.CategoryName == oldSelectedProfileName))
                {
                    // Jeśli profil został usunięty, a nie tylko odznaczony, wyczyść pola
                    UpdateEditFieldsFromSelectedProfile(); // To wyczyści pola, bo _selectedProfile jest null
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
                    ClearModelSpecificSuggestionsCache(); // Cache jest specyficzny dla ścieżek
                    (AutoCreateProfilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (SuggestImagesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (MatchModelSpecificCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CheckCharacterSuggestionsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
                    ClearModelSpecificSuggestionsCache(); // Cache zależy od folderów źródłowych
                    (AutoCreateProfilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (SuggestImagesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (MatchModelSpecificCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CheckCharacterSuggestionsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
                    // Odśwież liczniki tylko jeśli cache jest załadowany dla jakiegoś modelu
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
            RemoveProfileCommand = new AsyncRelayCommand(ExecuteRemoveProfileAsync, _ => IsProfileSelected); // CanExecute zależy od IsProfileSelected
            AddFilesToProfileCommand = new RelayCommand(ExecuteAddFilesToProfile);
            ClearFilesFromProfileCommand = new RelayCommand(ExecuteClearFilesFromProfile, _ => ImageFiles.Any());
            CreateNewProfileSetupCommand = new RelayCommand(ExecuteCreateNewProfileSetup);
            SelectLibraryPathCommand = new RelayCommand(ExecuteSelectLibraryPath);
            AutoCreateProfilesCommand = new AsyncRelayCommand(ExecuteAutoCreateProfilesAsync, CanExecuteAutoCreateProfiles);
            SuggestImagesCommand = new AsyncRelayCommand(ExecuteSuggestImagesAsync, CanExecuteSuggestImages);
            SaveAppSettingsCommand = new AsyncRelayCommand(ExecuteSaveAppSettingsAsync); // Zapis na żądanie, zawsze możliwy
            MatchModelSpecificCommand = new AsyncRelayCommand(ExecuteMatchModelSpecificAsync, CanExecuteMatchModelSpecific);
            CheckCharacterSuggestionsCommand = new AsyncRelayCommand(ExecuteCheckCharacterSuggestionsAsync, CanExecuteCheckCharacterSuggestions);
            RemoveModelTreeCommand = new AsyncRelayCommand(ExecuteRemoveModelTreeAsync, CanExecuteRemoveModelTree);
        }

        private void UpdateCurrentProfileNameForEdit()
        {
            if (!string.IsNullOrWhiteSpace(ModelNameInput) && !string.IsNullOrWhiteSpace(CharacterNameInput))
            {
                CurrentProfileNameForEdit = $"{ModelNameInput} - {CharacterNameInput}";
            }
            else if (!string.IsNullOrWhiteSpace(ModelNameInput))
            {
                CurrentProfileNameForEdit = ModelNameInput; // Możliwe, że użytkownik chce profil tylko dla modelki
            }
            else if (!string.IsNullOrWhiteSpace(CharacterNameInput))
            {
                CurrentProfileNameForEdit = CharacterNameInput; // Mniej typowe, ale możliwe
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
            string character = parts.Length > 1 ? parts[1].Trim() : "General"; // Jeśli brak postaci, to "General"

            // Jeśli po " - " jest pusty string, to też "General"
            if (parts.Length > 1 && string.IsNullOrWhiteSpace(parts[1]))
            {
                character = "General";
            }
            // Sanityzacja nazw folderów
            model = SanitizeFolderName(model);
            character = SanitizeFolderName(character);
            return (string.IsNullOrWhiteSpace(model) ? "UnknownModel" : model,
                    string.IsNullOrWhiteSpace(character) ? "General" : character);
        }

        private string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "_"; // Domyślna nazwa dla pustych
            string invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            string sanitizedName = name;
            foreach (char invalidChar in invalidChars.Distinct()) // Usuń duplikaty z GetInvalidPathChars
            {
                sanitizedName = sanitizedName.Replace(invalidChar.ToString(), "_");
            }
            // Dodatkowe typowe znaki, które mogą powodować problemy lub są niechciane
            sanitizedName = sanitizedName.Replace(":", "_").Replace("?", "_").Replace("*", "_")
                                       .Replace("\"", "_").Replace("<", "_").Replace(">", "_")
                                       .Replace("|", "_").Replace("/", "_").Replace("\\", "_");
            sanitizedName = sanitizedName.Trim().TrimStart('.').TrimEnd('.'); // Usuń kropki na początku/końcu
            if (string.IsNullOrWhiteSpace(sanitizedName)) return "_"; // Jeśli wszystko zostało usunięte
            return sanitizedName;
        }


        private void UpdateEditFieldsFromSelectedProfile()
        {
            if (_selectedProfile != null)
            {
                CurrentProfileNameForEdit = _selectedProfile.CategoryName;
                var (model, character) = ParseCategoryName(_selectedProfile.CategoryName);
                ModelNameInput = model;
                CharacterNameInput = (character == "General" && _selectedProfile.CategoryName == model) ? "" : character; // Jeśli to profil "General" dla samej modelki, nie pokazuj "General" w polu

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
                            SimpleFileLogger.LogWarning($"OSTRZEŻENIE (UpdateEditFields): Ścieżka obrazu '{path}' dla profilu '{_selectedProfile.CategoryName}' nie istnieje.");
                        }
                    }
                }
                ImageFiles = newImageFiles; // To wywoła PropertyChanged
            }
            else
            {
                CurrentProfileNameForEdit = string.Empty;
                ModelNameInput = string.Empty;
                CharacterNameInput = string.Empty;
                ImageFiles = new ObservableCollection<ImageFileEntry>(); // To wywoła PropertyChanged
            }
        }

        private void ClearModelSpecificSuggestionsCache()
        {
            SimpleFileLogger.Log("ClearModelSpecificSuggestionsCache: Czyszczenie cache sugestii dla modelu.");
            _lastModelSpecificSuggestions.Clear();
            _lastScannedModelNameForSuggestions = null;
            // Po wyczyszczeniu cache'u, odświeżamy liczniki, aby UI pokazało brak sugestii
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
                // Używamy pól prywatnych, aby PropertyChanged było wywołane przez publiczne settery
                // i aby logika w setterach (np. ClearModelSpecificSuggestionsCache) została wykonana.
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

            UserSettings? loadedSettings = await _settingsService.LoadSettingsAsync();
            if (loadedSettings != null)
            {
                ApplySettings(loadedSettings);
            }
            else
            {
                SimpleFileLogger.Log("InitializeAsync: Nie wczytano ustawień, używam domyślnych wartości z ViewModelu.");
            }

            // Ładujemy profile PO ustawieniach, ponieważ ścieżki profili mogą zależeć od ustawień
            await ExecuteLoadProfilesAsync(); // To wywoła ClearModelSpecificSuggestionsCache

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
            SimpleFileLogger.Log("ViewModel: Zakończono InitializeAsync.");
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
        private bool CanExecuteCheckCharacterSuggestions(object? parameter)
        {
            return parameter is CategoryProfile profile &&
                   !string.IsNullOrWhiteSpace(LibraryRootPath) &&
                   Directory.Exists(LibraryRootPath) &&
                   !string.IsNullOrWhiteSpace(SourceFolderNamesInput) &&
                   profile.CentroidEmbedding != null; // Musi być centroid, aby porównywać
        }
        private bool CanExecuteMatchModelSpecific(object? parameter)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) return false;
            return !string.IsNullOrWhiteSpace(LibraryRootPath) &&
                   Directory.Exists(LibraryRootPath) &&
                   modelVM.HasCharacterProfiles && // Modelka musi mieć jakieś profile postaci
                   !string.IsNullOrWhiteSpace(SourceFolderNamesInput);
        }
        private bool CanExecuteRemoveModelTree(object? parameter) => parameter is ModelDisplayViewModel;


        private async Task ExecuteLoadProfilesAsync(object? parameter = null)
        {
            StatusMessage = "Ładowanie profili...";
            SimpleFileLogger.Log($"ViewModel: Rozpoczęto ładowanie profili (ExecuteLoadProfilesAsync). Flaga _isRefreshingProfilesPostMove: {_isRefreshingProfilesPostMove}");

            if (!_isRefreshingProfilesPostMove) // <<< UŻYCIE FLAGI
            {
                ClearModelSpecificSuggestionsCache(); // Czyści też _lastScannedModelNameForSuggestions i odświeża liczniki do zera
            }
            else
            {
                SimpleFileLogger.Log("ExecuteLoadProfilesAsync: Pominięto ClearModelSpecificSuggestionsCache z powodu flagi _isRefreshingProfilesPostMove.");
            }

            string? previouslySelectedCategoryName = SelectedProfile?.CategoryName;
            await _profileService.LoadProfilesAsync(); // To ładuje z dysku do _profileService._profiles
            var allFlatProfiles = _profileService.GetAllProfiles()?.OrderBy(p => p.CategoryName).ToList();

            // Aktualizacja HierarchicalProfilesList musi być w wątku UI
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HierarchicalProfilesList.Clear(); // Czyści UI
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
                            // Obiekty CategoryProfile są tutaj nowymi instancjami (lub odtworzonymi z deserializacji).
                            // Ich PendingSuggestionsCount będzie domyślnie 0 (z konstruktora CategoryProfile).
                            // Zostanie to zaktualizowane przez RefreshPendingSuggestionCountsFromCache, jeśli cache sugestii nie został wyczyszczony.
                            modelVM.AddCharacterProfile(characterProfile);
                        }
                        HierarchicalProfilesList.Add(modelVM);
                    }
                }

                int totalProfiles = HierarchicalProfilesList.Sum(m => m.CharacterProfiles.Count);
                StatusMessage = $"Załadowano {totalProfiles} profili dla {HierarchicalProfilesList.Count} modelek.";
                SimpleFileLogger.Log($"ViewModel: Zakończono ładowanie (wątek UI). Załadowano {totalProfiles} profili dla {HierarchicalProfilesList.Count} modelek.");

                // Przywróć zaznaczenie, jeśli było
                if (!string.IsNullOrEmpty(previouslySelectedCategoryName))
                {
                    SelectedProfile = allFlatProfiles?.FirstOrDefault(p => p.CategoryName.Equals(previouslySelectedCategoryName, StringComparison.OrdinalIgnoreCase));
                }
                else if (SelectedProfile != null && !(allFlatProfiles?.Any(p => p.CategoryName == SelectedProfile.CategoryName) ?? false))
                {
                    // Jeśli poprzednio zaznaczony profil już nie istnieje
                    SelectedProfile = null;
                }


                OnPropertyChanged(nameof(AnyProfilesLoaded));
                // Odśwież CanExecute dla komend, które mogły się zmienić
                (SuggestImagesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (SaveProfilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RemoveProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (AutoCreateProfilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (MatchModelSpecificCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CheckCharacterSuggestionsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RemoveModelTreeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

                // Jeśli _isRefreshingProfilesPostMove było true, cache sugestii NIE został wyczyszczony.
                // Ponieważ HierarchicalProfilesList została przebudowana z nowymi obiektami CategoryProfile,
                // musimy teraz zsynchronizować ich PendingSuggestionsCount z istniejącym cachem.
                // Jeśli _isRefreshingProfilesPostMove było false, cache został wyczyszczony,
                // a RefreshPendingSuggestionCountsFromCache (wywołane przez ClearModelSpecificSuggestionsCache) już ustawiło wszystko na 0.
                if (_isRefreshingProfilesPostMove)
                {
                    SimpleFileLogger.Log("ExecuteLoadProfilesAsync: _isRefreshingProfilesPostMove=true, wywołuję RefreshPendingSuggestionCountsFromCache aby zsynchronizować nowe obiekty UI z zachowanym cachem.");
                    RefreshPendingSuggestionCountsFromCache();
                }
            });
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

            try
            {
                // Przekazujemy listę ImageFileEntry
                await _profileService.GenerateProfileAsync(categoryName, ImageFiles.ToList());
                StatusMessage = $"Profil '{categoryName}' wygenerowany/zaktualizowany.";
                SimpleFileLogger.Log($"Profil '{categoryName}' wygenerowany/zaktualizowany.");

                // Po wygenerowaniu/aktualizacji profilu, przeładuj listę profili w UI
                await ExecuteLoadProfilesAsync(); // To również odświeży cache sugestii, jeśli trzeba
                SelectedProfile = _profileService.GetProfile(categoryName); // Ponownie zaznacz profil
            }
            catch (Exception ex)
            {
                StatusMessage = $"Błąd generowania profilu: {ex.Message}";
                SimpleFileLogger.LogError($"Błąd generowania profilu dla '{categoryName}'", ex);
                MessageBox.Show($"Wystąpił błąd podczas generowania profilu: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecuteSaveAllProfilesAsync(object? parameter = null)
        {
            StatusMessage = "Zapisywanie wszystkich profili...";
            SimpleFileLogger.Log("ViewModel: Zapisywanie wszystkich profili (ExecuteSaveAllProfilesAsync)...");
            await _profileService.SaveAllProfilesAsync();
            StatusMessage = "Wszystkie profile zostały zapisane.";
            SimpleFileLogger.Log("ViewModel: Wszystkie profile zapisane.");
            MessageBox.Show("Wszystkie profile zostały zapisane.", "Zapisano", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task ExecuteRemoveProfileAsync(object? parameter)
        {
            CategoryProfile? profileToRemove = null;
            if (parameter is CategoryProfile contextProfile) // Usunięcie z menu kontekstowego
            {
                profileToRemove = contextProfile;
            }
            else if (SelectedProfile != null) // Usunięcie zaznaczonego profilu
            {
                profileToRemove = SelectedProfile;
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
                string profileNameToRemoveStr = profileToRemove.CategoryName; // Zapisz nazwę przed potencjalnym ustawieniem SelectedProfile na null
                StatusMessage = $"Usuwanie profilu '{profileNameToRemoveStr}'...";
                SimpleFileLogger.Log($"ViewModel: Usuwanie profilu '{profileNameToRemoveStr}'.");
                bool removed = await _profileService.RemoveProfileAsync(profileNameToRemoveStr);
                if (removed)
                {
                    StatusMessage = $"Profil '{profileNameToRemoveStr}' usunięty.";
                    // Jeśli usunięto aktualnie zaznaczony profil, odznacz go.
                    if (SelectedProfile?.CategoryName == profileNameToRemoveStr) SelectedProfile = null;
                    await ExecuteLoadProfilesAsync(); // Przeładuj listę profili
                }
                else
                {
                    StatusMessage = $"Nie udało się usunąć profilu '{profileNameToRemoveStr}'.";
                    SimpleFileLogger.Log($"ViewModel: Nie udało się usunąć profilu '{profileNameToRemoveStr}'.");
                }
            }
        }
        private void ExecuteAddFilesToProfile(object? parameter = null)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Image files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All files (*.*)|*.*",
                Title = "Wybierz obrazy dla profilu",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                bool filesAdded = false;
                foreach (string fileName in openFileDialog.FileNames)
                {
                    if (!ImageFiles.Any(f => f.FilePath.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        ImageFiles.Add(new ImageFileEntry { FilePath = fileName, FileName = Path.GetFileName(fileName) });
                        filesAdded = true;
                    }
                }
                if (filesAdded)
                {
                    (GenerateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        private void ExecuteClearFilesFromProfile(object? parameter = null)
        {
            ImageFiles.Clear(); // Użyj Clear() zamiast tworzyć nową kolekcję, aby uniknąć problemów z odświeżaniem UI
            // RaiseCanExecuteChanged dla GenerateProfileCommand jest obsługiwane przez setter ImageFiles,
            // ale Clear nie wywoła settera. Trzeba to zrobić ręcznie.
            (GenerateProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void ExecuteCreateNewProfileSetup(object? parameter = null)
        {
            SelectedProfile = null; // Odznacz bieżący profil
            CurrentProfileNameForEdit = "Nowa Kategoria"; // Placeholder
            ModelNameInput = string.Empty; // Wyczyść pola wprowadzania
            CharacterNameInput = string.Empty;
            ImageFiles.Clear(); // Wyczyść listę plików
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
                    ShowNewFolderButton = true // Umożliwia tworzenie nowego folderu z poziomu dialogu
                };
                // Jeśli ścieżka jest już ustawiona i istnieje, ustaw ją jako startową
                if (!string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath))
                {
                    dialog.SelectedPath = LibraryRootPath;
                }
                var owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive); // Pobierz aktywne okno jako właściciela dialogu
                if (dialog.ShowDialog(owner) == true)
                {
                    LibraryRootPath = dialog.SelectedPath; // To wywoła setter i logikę z nim związaną (np. ClearCache)
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
            // ClearModelSpecificSuggestionsCache(); // Już jest w setterze LibraryRootPath, ale dla pewności
            SimpleFileLogger.Log($"ExecuteAutoCreateProfilesAsync: Rozpoczęto skanowanie {LibraryRootPath}.");

            var configuredMixedFolderNames = new HashSet<string>(
                SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(name => name.Trim()),
                StringComparer.OrdinalIgnoreCase
            );

            int profilesCreatedOrUpdated = 0;
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
                if (configuredMixedFolderNames.Contains(modelName)) continue; // Pomiń, jeśli folder modelki jest na liście folderów Mix (co nie powinno mieć miejsca)

                try
                {
                    foreach (var characterDir in Directory.GetDirectories(modelDir))
                    {
                        string characterName = Path.GetFileName(characterDir);
                        // Pomiń foldery postaci, które są zdefiniowane jako "Mix" na poziomie postaci
                        if (configuredMixedFolderNames.Contains(characterName))
                        {
                            SimpleFileLogger.Log($"ExecuteAutoCreateProfilesAsync: Pomijanie folderu '{characterName}' wewnątrz '{modelName}' (jest na liście folderów Mix).");
                            continue;
                        }

                        string categoryName = $"{modelName} - {characterName}";
                        SimpleFileLogger.Log($"ExecuteAutoCreateProfilesAsync: Przetwarzanie folderu postaci: {categoryName}");

                        List<string> imagePathsInCharacterDir = await Task.Run(() => // Uruchom w tle, aby nie blokować UI
                            Directory.GetFiles(characterDir, "*.*", SearchOption.TopDirectoryOnly) // Tylko bieżący folder postaci
                                     .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f)))
                                     .ToList());

                        if (imagePathsInCharacterDir.Any())
                        {
                            List<ImageFileEntry> imageEntries = new List<ImageFileEntry>();
                            foreach (var path in imagePathsInCharacterDir)
                            {
                                // Dla automatycznego tworzenia, możemy pominąć kosztowne wyciąganie embeddingu,
                                // chyba że chcemy od razu pełne profile. Na razie tylko metadane.
                                var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                                if (entry != null) imageEntries.Add(entry);
                            }

                            if (imageEntries.Any())
                            {
                                // GenerateProfileAsync obliczy centroid, jeśli obrazy są dostarczone
                                await _profileService.GenerateProfileAsync(categoryName, imageEntries);
                                profilesCreatedOrUpdated++;
                                SimpleFileLogger.Log($"ExecuteAutoCreateProfilesAsync: Profil '{categoryName}' utworzony/zaktualizowany z {imageEntries.Count} obrazami.");
                            }
                        }
                        else
                        {
                            // Jeśli folder postaci jest pusty, ale profil dla niego istnieje,
                            // można go wyczyścić (usunąć ścieżki i centroid)
                            if (_profileService.GetProfile(categoryName) != null)
                            {
                                await _profileService.GenerateProfileAsync(categoryName, new List<ImageFileEntry>()); // Wywołanie z pustą listą czyści profil
                                SimpleFileLogger.Log($"ExecuteAutoCreateProfilesAsync: Profil '{categoryName}' istniał, ale folder jest pusty. Profil wyczyszczony.");
                            }
                        }
                    } // end foreach characterDir
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"Błąd podczas przetwarzania folderu modelki '{modelName}'", ex);
                    // Kontynuuj z następną modelką
                }
            } // end foreach modelDir
            StatusMessage = $"Zakończono. Utworzono/zaktualizowano: {profilesCreatedOrUpdated} profili.";
            await ExecuteLoadProfilesAsync(); // Przeładuj wszystkie profile do UI
            MessageBox.Show(StatusMessage, "Skanowanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
        }


        private async Task<Models.ProposedMove?> CreateProposedMoveAsync(ImageFileEntry sourceImageEntry, CategoryProfile suggestedProfileData, double similarityToCentroid, string modelDirectoryPath, float[] sourceEmbedding)
        {
            var (modelNameFromSource, characterNameFromSuggestedProfile) = ParseCategoryName(suggestedProfileData.CategoryName);
            // Upewnij się, że modelDirectoryPath jest poprawny dla modelNameFromSource
            // Zwykle modelDirectoryPath jest już kontekstem aktualnej modelki.

            string targetCharacterFolder = Path.Combine(modelDirectoryPath, characterNameFromSuggestedProfile);
            Directory.CreateDirectory(targetCharacterFolder);

            // Proponowana ścieżka, jeśli plik jest po prostu kopiowany z oryginalną nazwą pliku źródłowego
            string proposedPathIfCopiedWithSourceName = Path.Combine(targetCharacterFolder, sourceImageEntry.FileName);

            ImageFileEntry? bestMatchingTargetInFolder = null;
            double maxSimilarityToExistingInFolder = 0.0;

            // Sprawdź, czy w folderze docelowym istnieje już plik będący duplikatem KONTENTOWYM
            foreach (string existingFilePathInTarget in Directory.EnumerateFiles(targetCharacterFolder)
                .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f))))
            {
                // Nie porównuj pliku źródłowego samego ze sobą, jeśli jakimś cudem już tam jest
                if (string.Equals(Path.GetFullPath(existingFilePathInTarget), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase)) continue;

                // Potrzebujemy embeddingu istniejącego pliku do porównania
                ImageFileEntry? existingFileMeta = await _imageMetadataService.ExtractMetadataAsync(existingFilePathInTarget);
                float[]? existingEmbedding = await _profileService.GetImageEmbeddingAsync(existingFilePathInTarget); // Użyj metody z ProfileService

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
            string finalProposedTargetPath = proposedPathIfCopiedWithSourceName; // Domyślnie, jeśli nie ma konfliktów
            ImageFileEntry? finalTargetImageForDisplay = null; // Obraz, który będzie wyświetlany jako "docelowy" w UI
            double displaySimilarity = similarityToCentroid; // Domyślnie pokazujemy podobieństwo do centroidu profilu

            if (bestMatchingTargetInFolder != null && maxSimilarityToExistingInFolder >= DUPLICATE_SIMILARITY_THRESHOLD)
            {
                // Znaleziono silny duplikat KONTENTOWY w folderze docelowym
                finalTargetImageForDisplay = bestMatchingTargetInFolder;
                displaySimilarity = maxSimilarityToExistingInFolder; // Wyświetl podobieństwo do tego konkretnego pliku
                finalProposedTargetPath = bestMatchingTargetInFolder.FilePath; // Celem operacji będzie ten istniejący plik

                // Porównaj jakość źródła z tym znalezionym duplikatem kontentowym
                long sourceRes = (long)sourceImageEntry.Width * sourceImageEntry.Height;
                long targetRes = (long)bestMatchingTargetInFolder.Width * bestMatchingTargetInFolder.Height;
                long sourceSize = new FileInfo(sourceImageEntry.FilePath).Length;
                long targetSize = new FileInfo(bestMatchingTargetInFolder.FilePath).Length;

                if (sourceRes > targetRes || (sourceRes == targetRes && sourceSize > targetSize))
                {
                    actionType = ProposedMoveActionType.OverwriteExisting; // Źródło jest lepsze, nadpisz istniejący duplikat
                }
                else
                {
                    actionType = ProposedMoveActionType.KeepExistingDeleteSource; // Istniejący duplikat jest lepszy lub taki sam, zachowaj go, usuń źródło
                }
            }
            else
            {
                // Nie znaleziono silnego duplikatu KONTENTOWEGO.
                // Sprawdź teraz konflikt NAZWY: czy plik o tej samej nazwie co źródłowy już istnieje w folderze docelowym.
                // `proposedPathIfCopiedWithSourceName` to ścieżka z nazwą pliku źródłowego w folderze docelowym.
                if (File.Exists(proposedPathIfCopiedWithSourceName) &&
                    !string.Equals(Path.GetFullPath(proposedPathIfCopiedWithSourceName), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    // Tak, plik o tej samej nazwie co źródłowy już tam jest (ale nie jest to silny duplikat kontentowy).
                    // To jest sytuacja "ConflictKeepBoth" według oryginalnej logiki, którą teraz będziemy obsługiwać przez porównanie jakości.
                    finalTargetImageForDisplay = await _imageMetadataService.ExtractMetadataAsync(proposedPathIfCopiedWithSourceName);
                    actionType = ProposedMoveActionType.ConflictKeepBoth;
                    // `finalProposedTargetPath` pozostaje `proposedPathIfCopiedWithSourceName` - to jest plik, z którym mamy konflikt nazwy.
                }
                else
                {
                    // Nie ma konfliktu nazwy (plik o nazwie źródłowego nie istnieje w docelowym folderze)
                    // LUB `proposedPathIfCopiedWithSourceName` to ten sam plik co źródłowy (co nie powinno się zdarzyć, jeśli źródło jest z "Mix").
                    actionType = ProposedMoveActionType.CopyNew;
                    // `finalProposedTargetPath` pozostaje `proposedPathIfCopiedWithSourceName`.
                }
            }

            // Ostatnie sprawdzenie: jeśli akcja to KeepExistingDeleteSource, a ścieżka źródłowa i docelowa są identyczne
            // (co nie powinno się zdarzyć, jeśli źródło jest z folderu Mix), to nie twórz ruchu.
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
            if (!(parameter is ModelDisplayViewModel modelVM)) { SimpleFileLogger.LogWarning("ExecuteMatchModelSpecificAsync: Parametr nie jest ModelDisplayViewModel."); return; }

            // Sprawdzenie CanExecute w wątku UI, jeśli to konieczne, ale zwykle CanExecute jest już sprawdzone przez ICommand.
            // if (!await Application.Current.Dispatcher.InvokeAsync(() => CanExecuteMatchModelSpecific(modelVM))) { SimpleFileLogger.Log("ExecuteMatchModelSpecificAsync: CanExecute zwróciło false."); return; }

            var configuredMixedFolderNames = new HashSet<string>(
                SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(name => name.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (!configuredMixedFolderNames.Any())
            {
                MessageBox.Show("Zdefiniuj nazwy folderów źródłowych (np. Mix, Unsorted) w ustawieniach zaawansowanych.", "Brak folderów źródłowych", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusMessage = $"Obliczanie sugestii dla modelki: {modelVM.ModelName}...";
            var currentModelProposedMoves = new List<Models.ProposedMove>();
            string modelDirectoryPath = Path.Combine(LibraryRootPath, modelVM.ModelName); // Np. E:\Cosplay\ModelkaX
            if (!Directory.Exists(modelDirectoryPath))
            {
                MessageBox.Show($"Folder modelki '{modelVM.ModelName}' ('{modelDirectoryPath}') nie istnieje.", "Błąd ścieżki", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Zeruj liczniki przed nowym skanowaniem dla tego modelu
            modelVM.PendingSuggestionsCount = 0;
            foreach (var charProfile in modelVM.CharacterProfiles) charProfile.PendingSuggestionsCount = 0;

            try
            {
                foreach (var mixFolderNamePattern in configuredMixedFolderNames) // Np. "Mix", "Unsorted"
                {
                    string mixFolderPath = Path.Combine(modelDirectoryPath, mixFolderNamePattern); // Np. E:\Cosplay\ModelkaX\Mix
                    if (Directory.Exists(mixFolderPath))
                    {
                        SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Skanowanie folderu Mix: {mixFolderPath}");
                        // ScanDirectoryAsync zwraca pełne ścieżki do plików
                        foreach (var imagePath in await _fileScannerService.ScanDirectoryAsync(mixFolderPath))
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

                            // Sugeruj kategorię na podstawie embeddingu, ograniczając do profili danej modelki
                            var suggestionTuple = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName);
                            if (suggestionTuple != null) // (CategoryProfile suggestedProfile, double similarityToCentroid)
                            {
                                Models.ProposedMove? move = await CreateProposedMoveAsync(sourceImageEntry, suggestionTuple.Item1, suggestionTuple.Item2, modelDirectoryPath, sourceEmbedding);
                                if (move != null)
                                {
                                    currentModelProposedMoves.Add(move);
                                }
                            }
                        }
                    }
                    else
                    {
                        SimpleFileLogger.Log($"ExecuteMatchModelSpecificAsync: Folder Mix '{mixFolderPath}' nie istnieje, pomijanie.");
                    }
                } // koniec pętli po configuredMixedFolderNames

                _lastModelSpecificSuggestions = new List<Models.ProposedMove>(currentModelProposedMoves);
                _lastScannedModelNameForSuggestions = modelVM.ModelName;
                RefreshPendingSuggestionCountsFromCache(); // Aktualizuje UI

                StatusMessage = $"Zakończono obliczanie sugestii dla '{modelVM.ModelName}'. Znaleziono: {modelVM.PendingSuggestionsCount} potencjalnych dopasowań (powyżej progu).";
                SimpleFileLogger.Log(StatusMessage);

                if (modelVM.PendingSuggestionsCount > 0)
                {
                    MessageBox.Show($"Zakończono obliczanie sugestii dla modelki '{modelVM.ModelName}'.\nZnaleziono {modelVM.PendingSuggestionsCount} potencjalnych dopasowań (powyżej progu {SuggestionSimilarityThreshold:F2}).\n\nKliknij prawym przyciskiem na konkretnej postaci, aby sprawdzić jej indywidualne sugestie i przenieść pliki.", "Obliczanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Nie znaleziono żadnych sugestii dla modelki '{modelVM.ModelName}' (powyżej progu {SuggestionSimilarityThreshold:F2}).", "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Błąd podczas obliczania sugestii dla '{modelVM.ModelName}': {ex.Message}";
                SimpleFileLogger.LogError($"Błąd krytyczny w ExecuteMatchModelSpecificAsync dla '{modelVM.ModelName}'", ex);
                MessageBox.Show($"Wystąpił błąd krytyczny: {ex.Message}", "Błąd Obliczania Sugestii", MessageBoxButton.OK, MessageBoxImage.Error);
                ClearModelSpecificSuggestionsCache(); // W razie błędu, wyczyść cache, bo może być niekompletny
                // RefreshPendingSuggestionCountsFromCache(); // ClearModelSpecificSuggestionsCache już to robi
            }
        }

        private async Task ExecuteSuggestImagesAsync(object? parameter = null) // Globalne skanowanie
        {
            ClearModelSpecificSuggestionsCache(); // Globalne skanowanie powinno mieć swój własny, świeży zestaw wyników
            if (!CanExecuteSuggestImages(null)) { return; }

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

            // Zeruj liczniki dla wszystkich modelek przed globalnym skanowaniem
            foreach (var mVM_iterator in HierarchicalProfilesList)
            {
                mVM_iterator.PendingSuggestionsCount = 0;
                foreach (var cp_iterator in mVM_iterator.CharacterProfiles) cp_iterator.PendingSuggestionsCount = 0;
            }

            try
            {
                // Iteruj po każdej modelce w bibliotece
                foreach (var modelVM_iterator in HierarchicalProfilesList)
                {
                    string modelDirectoryPath = Path.Combine(LibraryRootPath, modelVM_iterator.ModelName);
                    if (!Directory.Exists(modelDirectoryPath) || !modelVM_iterator.HasCharacterProfiles) continue; // Pomiń modelki bez folderu lub bez profili postaci

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

                                // Sugeruj kategorię, ograniczając do profili BIEŻĄCEJ modelki (modelVM_iterator.ModelName)
                                var suggestionTuple = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM_iterator.ModelName);
                                if (suggestionTuple != null)
                                {
                                    Models.ProposedMove? move = await CreateProposedMoveAsync(sourceImageEntry, suggestionTuple.Item1, suggestionTuple.Item2, modelDirectoryPath, sourceEmbedding);
                                    if (move != null)
                                    {
                                        allProposedMovesAcrossModels.Add(move);
                                        // Aktualizuj liczniki od razu (opcjonalnie, bo PreviewChangesViewModel i tak filtruje)
                                        // Ale to może dać feedback użytkownikowi w trakcie długiego skanowania
                                        // if (move.Similarity >= SuggestionSimilarityThreshold) // Dodatkowe sprawdzenie, choć SuggestCategory już to robi
                                        // {
                                        //     var charP = modelVM_iterator.CharacterProfiles.FirstOrDefault(cp => cp.CategoryName == move.TargetCategoryProfileName);
                                        //     if (charP != null)
                                        //     {
                                        //         charP.PendingSuggestionsCount++;
                                        //         modelVM_iterator.PendingSuggestionsCount = modelVM_iterator.CharacterProfiles.Sum(cp => cp.PendingSuggestionsCount);
                                        //     }
                                        // }
                                    }
                                }
                            }
                        }
                    }
                } // koniec pętli po modelkach

                StatusMessage = $"Zakończono globalne skanowanie. Znaleziono {allProposedMovesAcrossModels.Count(m => m.Similarity >= SuggestionSimilarityThreshold)} sugestii (powyżej progu).";
                SimpleFileLogger.Log(StatusMessage);

                var filteredGlobalMoves = allProposedMovesAcrossModels
                    .Where(m => m.Similarity >= SuggestionSimilarityThreshold)
                    .ToList();

                if (filteredGlobalMoves.Any())
                {
                    var previewVM = new PreviewChangesViewModel(filteredGlobalMoves, SuggestionSimilarityThreshold);
                    var previewWindow = new PreviewChangesWindow { DataContext = previewVM };
                    // Ustawienie Owner dla poprawnego zachowania modalnego
                    previewWindow.Owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive) ?? Application.Current.MainWindow;
                    if (previewWindow is Views.PreviewChangesWindow actualTypedWindow) { actualTypedWindow.SetCloseAction(previewVM); }

                    bool? dialogOutcome = previewWindow.ShowDialog();
                    if (dialogOutcome == true)
                    {
                        // Przekazujemy null dla specificModelVM i specificCharacterProfile, bo to operacja globalna
                        HandleApprovedMoves(previewVM.GetApprovedMoves(), null, null);
                    }
                    else
                    {
                        StatusMessage = "Anulowano zmiany (Globalne Sugestie).";
                        SimpleFileLogger.Log(StatusMessage);
                    }
                    // Po zamknięciu okna Preview, warto odświeżyć wszystkie liczniki, bo nie wiemy, które modele były dotknięte
                    // Jeśli HandleApprovedMoves nie wyczyściło _lastScannedModelNameForSuggestions, to odświeży tylko ostatni model.
                    // To jest problematyczne dla globalnego skanowania.
                    // Globalne skanowanie nie powinno używać _lastModelSpecificSuggestions.
                    // Po globalnym skanowaniu i przetworzeniu, najlepiej wyczyścić ten cache.
                    ClearModelSpecificSuggestionsCache(); // Czyści cache i odświeża liczniki do zera.
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
                ClearModelSpecificSuggestionsCache(); // W razie błędu, wyczyść
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
            if (!CanExecuteCheckCharacterSuggestions(characterProfile))
            {
                SimpleFileLogger.LogWarning($"ExecuteCheckCharacterSuggestionsAsync: Warunek CanExecute niespełniony dla '{characterProfile.CategoryName}'.");
                MessageBox.Show($"Nie można sprawdzić sugestii dla '{characterProfile.CategoryName}'.\nMożliwe przyczyny: brak ścieżki biblioteki, brak folderów źródłowych lub profil postaci nie ma obliczonego centroidu.",
                                "Nie można wykonać",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning); // <<< POPRAWKA TUTAJ
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

                if (_lastScannedModelNameForSuggestions != modelName)
                {
                    _lastModelSpecificSuggestions.Clear();
                }
                _lastModelSpecificSuggestions.RemoveAll(m => m.TargetCategoryProfileName == characterProfile.CategoryName);
                _lastModelSpecificSuggestions.AddRange(suggestionsForThisCharacter);
                _lastScannedModelNameForSuggestions = modelName;
            }

            var actualSuggestionsToShowInDialog = suggestionsForThisCharacter; // Lista jest już przefiltrowana lub nowo utworzona

            if (actualSuggestionsToShowInDialog.Any())
            {
                var previewVM = new PreviewChangesViewModel(actualSuggestionsToShowInDialog, SuggestionSimilarityThreshold);
                var previewWindow = new PreviewChangesWindow { DataContext = previewVM };
                previewWindow.Owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive) ?? Application.Current.MainWindow;
                if (previewWindow is Views.PreviewChangesWindow actualTypedWindow) { actualTypedWindow.SetCloseAction(previewVM); }

                bool? dialogOutcome = previewWindow.ShowDialog();
                if (dialogOutcome == true)
                {
                    ModelDisplayViewModel? parentModelVM = HierarchicalProfilesList.FirstOrDefault(m => m.CharacterProfiles.Contains(characterProfile));
                    HandleApprovedMoves(previewVM.GetApprovedMoves(), parentModelVM, characterProfile);
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
                await RefreshProfilesAsync(changedProfileNamesForRefresh.Distinct().ToList());
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

        private async Task RefreshProfilesAsync(List<string> profileNamesToRefresh)
        {
            if (string.IsNullOrWhiteSpace(LibraryRootPath) || !Directory.Exists(LibraryRootPath))
            {
                SimpleFileLogger.LogWarning("RefreshProfilesAsync: Ścieżka biblioteki nie jest ustawiona lub nie istnieje. Pomijanie odświeżania profili.");
                return;
            }
            await Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = "Odświeżanie zmodyfikowanych profili...");
            SimpleFileLogger.Log($"RefreshProfilesAsync: Rozpoczęto odświeżanie {profileNamesToRefresh.Count} profili.");
            bool changedAnythingSignificant = false;
            foreach (var profileName in profileNamesToRefresh)
            {
                var (model, character) = ParseCategoryName(profileName);
                string charPath = Path.Combine(LibraryRootPath, model, character);
                if (Directory.Exists(charPath))
                {
                    SimpleFileLogger.Log($"RefreshProfilesAsync: Odświeżanie profilu '{profileName}' z folderu '{charPath}'.");
                    // Używamy Task.Run, aby uniknąć blokowania wątku UI na długo przy dużej liczbie plików
                    var entries = await Task.Run(async () =>
                        (await Task.WhenAll(Directory.GetFiles(charPath)
                            .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f)))
                            .Select(p => _imageMetadataService.ExtractMetadataAsync(p))))
                            .Where(e => e != null).ToList()
                    );

                    await _profileService.GenerateProfileAsync(profileName, entries!);
                    changedAnythingSignificant = true;
                }
                else if (_profileService.GetProfile(profileName) != null) // Profil istnieje, ale folder zniknął
                {
                    SimpleFileLogger.LogWarning($"RefreshProfilesAsync: Folder dla profilu '{profileName}' ('{charPath}') nie istnieje, ale profil tak. Czyszczenie profilu.");
                    await _profileService.GenerateProfileAsync(profileName, new List<ImageFileEntry>()); // Wyczyść profil
                    changedAnythingSignificant = true;
                }
            }

            if (changedAnythingSignificant)
            {
                SimpleFileLogger.Log("RefreshProfilesAsync: Wykryto znaczące zmiany w profilach, przeładowywanie całej listy profili.");
                await ExecuteLoadProfilesAsync(); // To przebuduje HierarchicalProfilesList i może wyczyścić cache sugestii (zależnie od flagi)
            }
            else
            {
                SimpleFileLogger.Log("RefreshProfilesAsync: Nie wykryto znaczących zmian, nie ma potrzeby pełnego przeładowania profili.");
                await Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = "Profile aktualne.");
            }
        }

        private string GenerateUniqueTargetPath(string targetDirectory, string originalFileName, string suffixBase = "_conflict")
        {
            string baseName = Path.GetFileNameWithoutExtension(originalFileName);
            string extension = Path.GetExtension(originalFileName);
            int counter = 1;
            // Ogranicz długość nazwy bazowej, aby uniknąć problemów z maksymalną długością ścieżki
            string safeBaseName = baseName.Length > 200 ? baseName.Substring(0, 200) : baseName;
            string finalTargetPath;

            do
            {
                string newFileName = $"{safeBaseName}{suffixBase}{counter}{extension}";
                finalTargetPath = Path.Combine(targetDirectory, newFileName);
                if (counter > 9999) // Zabezpieczenie przed nieskończoną pętlą
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
                bool success = await _profileService.RemoveAllProfilesForModelAsync(modelVM.ModelName);
                if (success)
                {
                    StatusMessage = $"Modelka '{modelVM.ModelName}' i jej profile zostały usunięte z systemu profili.";
                    SimpleFileLogger.Log(StatusMessage);
                    // Jeśli usunięta modelka była tą, dla której mieliśmy cache sugestii, wyczyść go
                    if (_lastScannedModelNameForSuggestions == modelVM.ModelName)
                    {
                        ClearModelSpecificSuggestionsCache();
                    }
                    // Jeśli usunięto modelkę, która zawierała zaznaczony profil postaci
                    if (SelectedProfile != null && _profileService.GetModelNameFromCategory(SelectedProfile.CategoryName) == modelVM.ModelName)
                    {
                        SelectedProfile = null;
                    }
                    await ExecuteLoadProfilesAsync(); // Przeładuj, aby usunąć z UI
                }
                else
                {
                    StatusMessage = $"Nie udało się całkowicie usunąć modelki '{modelVM.ModelName}' z systemu profili. Sprawdź logi.";
                    SimpleFileLogger.LogError(StatusMessage, null);
                    await ExecuteLoadProfilesAsync(); // Mimo wszystko przeładuj, może coś się usunęło
                }
            }
        }
    }
}