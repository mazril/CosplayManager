using CosplayManager.Models;
using CosplayManager.Services;
using CosplayManager.ViewModels.Base;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows; // Dla Application.Current.Dispatcher
using System.Windows.Input;

namespace CosplayManager.ViewModels
{
    public class ManageProfileSuggestionsViewModel : ObservableObject
    {
        private readonly ProfileService _profileService;
        private readonly FileScannerService _fileScannerService;
        private readonly ImageMetadataService _imageMetadataService;
        private readonly MainWindowViewModel _mainWindowViewModel; // Potrzebne do dostępu do _lastModelSpecificSuggestions i innych ustawień

        private CategoryProfile _targetProfile;
        public CategoryProfile TargetProfile
        {
            get => _targetProfile;
            set => SetProperty(ref _targetProfile, value);
        }

        private ModelDisplayViewModel _parentModelVm;
        public ModelDisplayViewModel ParentModelVm
        {
            get => _parentModelVm;
            set => SetProperty(ref _parentModelVm, value);
        }

        public string WindowTitle => $"Sugestie dla: {ParentModelVm?.ModelName} - {TargetProfile?.GetCharacterName()}";


        private ObservableCollection<SuggestedImageItemViewModel> _suggestedImages;
        public ObservableCollection<SuggestedImageItemViewModel> SuggestedImages
        {
            get => _suggestedImages;
            set => SetProperty(ref _suggestedImages, value);
        }

        private ObservableCollection<ProfileImageItemViewModel> _currentProfileImages;
        public ObservableCollection<ProfileImageItemViewModel> CurrentProfileImages
        {
            get => _currentProfileImages;
            set => SetProperty(ref _currentProfileImages, value);
        }

        private double _similarityThreshold;
        public double SimilarityThreshold
        {
            get => _similarityThreshold;
            set
            {
                // Zmiana progu nie odświeża automatycznie, dopiero przycisk "Odśwież"
                SetProperty(ref _similarityThreshold, value);
                // RefreshSuggestedImages(); // To byłoby automatyczne odświeżanie
            }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        // Komendy
        public ICommand RefreshSuggestedCommand { get; }
        public ICommand ApplyChangesCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand SelectAllSuggestedCommand { get; }
        public ICommand DeselectAllSuggestedCommand { get; }
        public ICommand SelectAllCurrentProfileCommand { get; }
        public ICommand DeselectAllCurrentProfileCommand { get; }

        public Action<bool?>? CloseAction { get; set; }

        // Przechowuje pełną listę sugestii dla danego profilu (przed filtrowaniem progiem)
        private List<SuggestedImageItemViewModel> _allSuggestionsForProfileMasterList;






        public ManageProfileSuggestionsViewModel(
            CategoryProfile targetProfile,
            ModelDisplayViewModel parentModelVm,
            MainWindowViewModel mainWindowViewModel, // Przekazujemy MainWindowViewModel
            ProfileService profileService,
            FileScannerService fileScannerService,
            ImageMetadataService imageMetadataService)
        {
            _targetProfile = targetProfile ?? throw new ArgumentNullException(nameof(targetProfile));
            _parentModelVm = parentModelVm ?? throw new ArgumentNullException(nameof(parentModelVm));
            _mainWindowViewModel = mainWindowViewModel ?? throw new ArgumentNullException(nameof(mainWindowViewModel));
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _fileScannerService = fileScannerService ?? throw new ArgumentNullException(nameof(fileScannerService));
            _imageMetadataService = imageMetadataService ?? throw new ArgumentNullException(nameof(imageMetadataService));

            _similarityThreshold = _mainWindowViewModel.SuggestionSimilarityThreshold; // Ustaw początkowy próg z MainWindowVM

            SuggestedImages = new ObservableCollection<SuggestedImageItemViewModel>();
            CurrentProfileImages = new ObservableCollection<ProfileImageItemViewModel>();
            _allSuggestionsForProfileMasterList = new List<SuggestedImageItemViewModel>();

            RefreshSuggestedCommand = new RelayCommand(async _ => await LoadAndFilterSuggestedImagesAsync(), _ => !IsBusy);
            ApplyChangesCommand = new AsyncRelayCommand(ExecuteApplyChangesAsync, CanExecuteApplyChanges);
            CancelCommand = new RelayCommand(_ => CloseAction?.Invoke(false));

            SelectAllSuggestedCommand = new RelayCommand(_ => SetSelectionForAll(SuggestedImages, true), _ => SuggestedImages.Any());
            DeselectAllSuggestedCommand = new RelayCommand(_ => SetSelectionForAll(SuggestedImages, false), _ => SuggestedImages.Any(s => s.IsSelected));
            SelectAllCurrentProfileCommand = new RelayCommand(_ => SetSelectionForAll(CurrentProfileImages, true), _ => CurrentProfileImages.Any());
            DeselectAllCurrentProfileCommand = new RelayCommand(_ => SetSelectionForAll(CurrentProfileImages, false), _ => CurrentProfileImages.Any(s => s.IsSelected));

            // Inicjalne ładowanie danych
            _ = InitializeDataAsync();
        }

        private async Task InitializeDataAsync()
        {
            IsBusy = true;
            try
            {
                await LoadCurrentProfileImagesAsync();
                await LoadAndFilterSuggestedImagesAsync(true); // true - initial load of master list
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Error initializing ManageProfileSuggestionsViewModel: {ex.Message}", ex);
                // Można pokazać MessageBox użytkownikowi
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadCurrentProfileImagesAsync()
        {
            CurrentProfileImages.Clear();
            if (TargetProfile.SourceImagePaths == null || !TargetProfile.SourceImagePaths.Any())
                return;

            var tasks = new List<Task>();
            var tempCollection = new ObservableCollection<ProfileImageItemViewModel>();

            foreach (var path in TargetProfile.SourceImagePaths)
            {
                if (File.Exists(path))
                {
                    var imageEntry = await _imageMetadataService.ExtractMetadataAsync(path);
                    if (imageEntry != null)
                    {
                        var itemVM = new ProfileImageItemViewModel(imageEntry);
                        tempCollection.Add(itemVM);
                        tasks.Add(itemVM.LoadThumbnailAsync());
                    }
                }
            }
            // Użyj Application.Current.Dispatcher, aby zaktualizować kolekcję w wątku UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var item in tempCollection.OrderBy(i => i.FileName))
                {
                    CurrentProfileImages.Add(item);
                }
            });
            await Task.WhenAll(tasks);
        }

        private async Task LoadAndFilterSuggestedImagesAsync(bool initialLoad = false)
        {
            if (initialLoad)
            {
                _allSuggestionsForProfileMasterList.Clear();
                // Pobierz sugestie z MainWindowViewModel._lastModelSpecificSuggestions
                // Filtruj te, które dotyczą TargetProfile.CategoryName
                // Pamiętaj o pobraniu informacji o folderze źródłowym (np. "Mix", "Mieszane")
                // dla każdego SuggestedImageItemViewModel

                var rawSuggestions = _mainWindowViewModel.GetLastModelSpecificSuggestions(); // Potrzebujemy metody w MainWindowVM

                if (rawSuggestions != null)
                {
                    var profileSpecificRawSuggestions = rawSuggestions
                        .Where(s => s.TargetCategoryProfileName.Equals(TargetProfile.CategoryName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var tasks = new List<Task>();
                    foreach (var move in profileSpecificRawSuggestions)
                    {
                        // Ustalenie nazwy folderu źródłowego
                        // To jest uproszczenie, być może trzeba będzie inaczej to rozwiązać,
                        // jeśli ścieżka nie zawiera nazwy folderu "Mix" itp. bezpośrednio
                        string sourceFolderName = Path.GetFileName(Path.GetDirectoryName(move.SourceImage.FilePath) ?? string.Empty);

                        var itemVM = new SuggestedImageItemViewModel(move.SourceImage, move.Similarity, sourceFolderName);
                        _allSuggestionsForProfileMasterList.Add(itemVM);
                        tasks.Add(itemVM.LoadThumbnailAsync());
                    }
                    await Task.WhenAll(tasks); // Poczekaj na załadowanie miniaturek dla master list
                }
            }

            // Filtruj _allSuggestionsForProfileMasterList na podstawie SimilarityThreshold
            var filtered = _allSuggestionsForProfileMasterList
                .Where(s => s.SimilarityScore >= SimilarityThreshold)
                .OrderByDescending(s => s.SimilarityScore)
                .ThenBy(s => s.SourceFileName)
                .ToList();

            // Aktualizuj kolekcję bindowaną do UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                SuggestedImages.Clear();
                foreach (var item in filtered)
                {
                    SuggestedImages.Add(item);
                }
            });
        }

        private void SetSelectionForAll<T>(ObservableCollection<T> collection, bool isSelected) where T : class
        {
            foreach (var item in collection)
            {
                if (item is SuggestedImageItemViewModel sItem) sItem.IsSelected = isSelected;
                else if (item is ProfileImageItemViewModel pItem) pItem.IsSelected = isSelected;
            }
        }

        private bool CanExecuteApplyChanges(object? parameter)
        {
            return !IsBusy && (SuggestedImages.Any(s => s.IsSelected) || CurrentProfileImages.Any(c => c.IsSelected));
        }

        private async Task ExecuteApplyChangesAsync(object? parameter)
        {
            IsBusy = true;
            SimpleFileLogger.LogHighLevelInfo($"ManageProfileSuggestionsVM: Rozpoczęto ApplyChanges dla profilu '{TargetProfile.CategoryName}'.");

            bool changesMade = false;
            var imagesToAdd = SuggestedImages.Where(s => s.IsSelected).Select(s => s.OriginalImage).ToList();
            var imagesToRemoveFromProfile = CurrentProfileImages.Where(p => p.IsSelected).Select(p => p.OriginalImage).ToList();

            string modelName = _profileService.GetModelNameFromCategory(TargetProfile.CategoryName);
            string modelPath = Path.Combine(_mainWindowViewModel.LibraryRootPath, SanitizeFolderName(modelName));

            string targetMixFolderName = _mainWindowViewModel.SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(targetMixFolderName))
            {
                MessageBox.Show("Nie zdefiniowano folderu docelowego 'Mix' w ustawieniach aplikacji. Anulowano przenoszenie.", "Błąd konfiguracji", MessageBoxButton.OK, MessageBoxImage.Warning);
                IsBusy = false;
                return;
            }
            string fullTargetMixPath = Path.Combine(modelPath, SanitizeFolderName(targetMixFolderName));
            Directory.CreateDirectory(fullTargetMixPath);

            if (imagesToRemoveFromProfile.Any())
            {
                SimpleFileLogger.Log($"ManageProfileSuggestionsVM: Przenoszenie {imagesToRemoveFromProfile.Count} obrazów z profilu '{TargetProfile.CategoryName}' do '{fullTargetMixPath}'.");
                foreach (var imageEntry in imagesToRemoveFromProfile)
                {
                    string sourcePath = imageEntry.FilePath;
                    string destFileName = Path.GetFileName(sourcePath);
                    string destPath = Path.Combine(fullTargetMixPath, destFileName);

                    try
                    {
                        if (File.Exists(sourcePath))
                        {
                            int counter = 1;
                            string uniqueDestPath = destPath;
                            while (File.Exists(uniqueDestPath) && !uniqueDestPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                            {
                                string baseName = Path.GetFileNameWithoutExtension(destFileName);
                                string extension = Path.GetExtension(destFileName);
                                uniqueDestPath = Path.Combine(fullTargetMixPath, $"{baseName}_moved_{counter}{extension}");
                                counter++;
                            }

                            if (!uniqueDestPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Move(sourcePath, uniqueDestPath);
                                SimpleFileLogger.Log($"Przeniesiono: '{sourcePath}' -> '{uniqueDestPath}'");
                                TargetProfile.SourceImagePaths.RemoveAll(p => p.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
                                changesMade = true;
                            }
                            else
                            {
                                SimpleFileLogger.Log($"Plik '{sourcePath}' już jest w folderze docelowym lub ma taką samą ścieżkę. Pomijanie przenoszenia.");
                            }
                        }
                        else
                        {
                            SimpleFileLogger.LogWarning($"Plik źródłowy '{sourcePath}' nie istnieje. Nie można przenieść.");
                        }
                    }
                    catch (Exception ex)
                    {
                        SimpleFileLogger.LogError($"Błąd podczas przenoszenia '{sourcePath}' do '{destPath}': {ex.Message}", ex);
                    }
                }
            }

            if (imagesToAdd.Any())
            {
                SimpleFileLogger.Log($"ManageProfileSuggestionsVM: Dodawanie {imagesToAdd.Count} obrazów do profilu '{TargetProfile.CategoryName}'.");
                string targetProfileCharacterFolder = _profileService.GetCharacterNameFromCategory(TargetProfile.CategoryName);
                string targetProfileFullPath = Path.Combine(modelPath, SanitizeFolderName(targetProfileCharacterFolder));
                Directory.CreateDirectory(targetProfileFullPath);

                foreach (var imageEntry in imagesToAdd)
                {
                    string sourcePath = imageEntry.FilePath;
                    string destFileName = Path.GetFileName(sourcePath);
                    string destPath = Path.Combine(targetProfileFullPath, destFileName);

                    try
                    {
                        if (File.Exists(sourcePath))
                        {
                            int counter = 1;
                            string uniqueDestPath = destPath;
                            while (File.Exists(uniqueDestPath) && !uniqueDestPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                            {
                                string baseName = Path.GetFileNameWithoutExtension(destFileName);
                                string extension = Path.GetExtension(destFileName);
                                uniqueDestPath = Path.Combine(targetProfileFullPath, $"{baseName}_added_{counter}{extension}");
                                counter++;
                            }

                            if (!uniqueDestPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Move(sourcePath, uniqueDestPath);
                                SimpleFileLogger.Log($"Przeniesiono (dodano do profilu): '{sourcePath}' -> '{uniqueDestPath}'");
                                if (!TargetProfile.SourceImagePaths.Any(p => p.Equals(uniqueDestPath, StringComparison.OrdinalIgnoreCase)))
                                {
                                    TargetProfile.SourceImagePaths.Add(uniqueDestPath);
                                }
                                changesMade = true;
                            }
                            else
                            {
                                SimpleFileLogger.Log($"Plik '{sourcePath}' już jest w folderze docelowym profilu lub ma taką samą ścieżkę. Pomijanie przenoszenia.");
                                if (!TargetProfile.SourceImagePaths.Any(p => p.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)))
                                {
                                    TargetProfile.SourceImagePaths.Add(sourcePath);
                                    changesMade = true;
                                }
                            }
                        }
                        else
                        {
                            SimpleFileLogger.LogWarning($"Plik źródłowy '{sourcePath}' dla sugestii nie istnieje. Nie można dodać do profilu.");
                        }
                    }
                    catch (Exception ex)
                    {
                        SimpleFileLogger.LogError($"Błąd podczas przenoszenia (dodawania) '{sourcePath}' do '{destPath}': {ex.Message}", ex);
                    }
                }
            }

            if (changesMade)
            {
                SimpleFileLogger.Log($"ManageProfileSuggestionsVM: Zmiany wprowadzone. Przeliczanie profilu '{TargetProfile.CategoryName}'.");
                var dummyProgress = new Progress<ProgressReport>();
                var imagesForProfileRegen = new List<ImageFileEntry>();
                foreach (var path in TargetProfile.SourceImagePaths)
                {
                    if (File.Exists(path))
                    {
                        var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                        if (entry != null) imagesForProfileRegen.Add(entry);
                    }
                }
                // Poprawione wywołanie - użycie CancellationToken.None
                await _profileService.GenerateProfileAsync(TargetProfile.CategoryName, imagesForProfileRegen, dummyProgress, CancellationToken.None);
                await _mainWindowViewModel.RefreshProfilesAfterChangeAsync();
            }

            IsBusy = false;
            SimpleFileLogger.LogHighLevelInfo($"ManageProfileSuggestionsVM: Zakończono ApplyChanges dla profilu '{TargetProfile.CategoryName}'. Zmiany: {changesMade}");
            CloseAction?.Invoke(changesMade);
        }

        // Helper do MainWindowViewModel, aby mógł pobrać aktualne sugestie
        public List<Models.ProposedMove> GetCurrentSuggestionsForProfileFromCache()
        {
            // Ta metoda powinna być w MainWindowViewModel lub ProfileService,
            // tutaj tylko symulujemy jej istnienie dla celów tego VM
            var rawSuggestions = _mainWindowViewModel.GetLastModelSpecificSuggestions();
            if (rawSuggestions == null) return new List<Models.ProposedMove>();

            return rawSuggestions
                .Where(s => s.TargetCategoryProfileName.Equals(TargetProfile.CategoryName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        private string SanitizeFolderName(string name)
        {
            // Ta metoda jest już w MainWindowViewModel, można by ją przenieść do jakiejś klasy Utils
            if (string.IsNullOrWhiteSpace(name)) return "_";
            string invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            string sanitizedName = name;
            foreach (char invalidChar in invalidChars.Distinct()) sanitizedName = sanitizedName.Replace(invalidChar.ToString(), "_");
            sanitizedName = sanitizedName.Replace(":", "_").Replace("?", "_").Replace("*", "_").Replace("\"", "_").Replace("<", "_").Replace(">", "_").Replace("|", "_").Replace("/", "_").Replace("\\", "_");
            sanitizedName = sanitizedName.Trim().TrimStart('.').TrimEnd('.');
            if (string.IsNullOrWhiteSpace(sanitizedName)) return "_";
            return sanitizedName;
        }
    }




}