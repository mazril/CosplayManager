// Plik: ViewModels/ManageProfileSuggestionsViewModel.cs
using CosplayManager.Models;
using CosplayManager.Services;
using CosplayManager.ViewModels.Base;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel; // Potrzebne dla CollectionChanged
using System.IO;
using System.Linq;
using System.Threading; // Potrzebne dla CancellationToken
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace CosplayManager.ViewModels
{
    public class ManageProfileSuggestionsViewModel : ObservableObject
    {
        private readonly ProfileService _profileService;
        private readonly FileScannerService _fileScannerService;
        private readonly ImageMetadataService _imageMetadataService;
        private readonly MainWindowViewModel _mainWindowViewModel;

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

        public string WindowTitle => $"Sugestie dla: {ParentModelVm?.ModelName} - {_profileService.GetCharacterNameFromCategory(TargetProfile.CategoryName)}";

        private ObservableCollection<SuggestedImageItemViewModel> _suggestedImages;
        public ObservableCollection<SuggestedImageItemViewModel> SuggestedImages
        {
            get => _suggestedImages;
            set
            {
                if (_suggestedImages != null)
                {
                    _suggestedImages.CollectionChanged -= SuggestedImages_CollectionChanged;
                    foreach (var item in _suggestedImages) item.PropertyChanged -= SuggestedItem_PropertyChanged;
                }
                if (SetProperty(ref _suggestedImages, value))
                {
                    if (_suggestedImages != null)
                    {
                        _suggestedImages.CollectionChanged += SuggestedImages_CollectionChanged;
                        foreach (var item in _suggestedImages) item.PropertyChanged += SuggestedItem_PropertyChanged;
                    }
                    UpdateSelectAllSuggestedState();
                }
            }
        }

        private ObservableCollection<ProfileImageItemViewModel> _currentProfileImages;
        public ObservableCollection<ProfileImageItemViewModel> CurrentProfileImages
        {
            get => _currentProfileImages;
            set
            {
                if (_currentProfileImages != null)
                {
                    _currentProfileImages.CollectionChanged -= CurrentProfileImages_CollectionChanged;
                    foreach (var item in _currentProfileImages) item.PropertyChanged -= ProfileItem_PropertyChanged;
                }
                if (SetProperty(ref _currentProfileImages, value))
                {
                    if (_currentProfileImages != null)
                    {
                        _currentProfileImages.CollectionChanged += CurrentProfileImages_CollectionChanged;
                        foreach (var item in _currentProfileImages) item.PropertyChanged += ProfileItem_PropertyChanged;
                    }
                    UpdateSelectAllCurrentProfileState();
                }
            }
        }


        private double _similarityThreshold;
        public double SimilarityThreshold
        {
            get => _similarityThreshold;
            set => SetProperty(ref _similarityThreshold, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            // Zmieniamy IsBusy na pełną właściwość, aby odświeżać CanExecute komend
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        // --- Nowe właściwości dla checkboxów "Zaznacz wszystko" ---
        private bool? _selectAllSuggested = false; // Używamy nullable bool dla stanu pośredniego
        public bool? SelectAllSuggested
        {
            get => _selectAllSuggested;
            set
            {
                if (SetProperty(ref _selectAllSuggested, value))
                {
                    if (value.HasValue) // Jeśli nie jest to stan pośredni
                    {
                        SetSelectionForAll(SuggestedImages, value.Value);
                    }
                }
            }
        }

        private bool? _selectAllCurrentProfile = false; // Używamy nullable bool dla stanu pośredniego
        public bool? SelectAllCurrentProfile
        {
            get => _selectAllCurrentProfile;
            set
            {
                if (SetProperty(ref _selectAllCurrentProfile, value))
                {
                    if (value.HasValue) // Jeśli nie jest to stan pośredni
                    {
                        SetSelectionForAll(CurrentProfileImages, value.Value);
                    }
                }
            }
        }
        // --- Koniec nowych właściwości ---


        public ICommand RefreshSuggestedCommand { get; }
        public ICommand ApplyChangesCommand { get; }
        public ICommand CancelCommand { get; }


        public Action<bool?>? CloseAction { get; set; }
        private List<SuggestedImageItemViewModel> _allSuggestionsForProfileMasterList;

        public ManageProfileSuggestionsViewModel(
            CategoryProfile targetProfile,
            ModelDisplayViewModel parentModelVm,
            MainWindowViewModel mainWindowViewModel,
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

            _similarityThreshold = _mainWindowViewModel.SuggestionSimilarityThreshold;

            SuggestedImages = new ObservableCollection<SuggestedImageItemViewModel>();
            CurrentProfileImages = new ObservableCollection<ProfileImageItemViewModel>();
            _allSuggestionsForProfileMasterList = new List<SuggestedImageItemViewModel>();

            RefreshSuggestedCommand = new RelayCommand(async _ => await LoadAndFilterSuggestedImagesAsync(), _ => !IsBusy);
            ApplyChangesCommand = new AsyncRelayCommand(ExecuteApplyChangesAsync, CanExecuteApplyChanges);
            CancelCommand = new RelayCommand(_ => CloseAction?.Invoke(false), _ => !IsBusy);

            _ = InitializeDataAsync();
        }


        private async Task InitializeDataAsync()
        {
            IsBusy = true;
            try
            {
                await LoadCurrentProfileImagesAsync();
                await LoadAndFilterSuggestedImagesAsync(true);
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Error initializing ManageProfileSuggestionsViewModel: {ex.Message}", ex);
                MessageBox.Show($"Błąd inicjalizacji danych: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadCurrentProfileImagesAsync()
        {
            var tempCollection = new ObservableCollection<ProfileImageItemViewModel>();
            if (TargetProfile.SourceImagePaths != null && TargetProfile.SourceImagePaths.Any())
            {
                var tasks = new List<Task>();
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
                await Task.WhenAll(tasks);
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentProfileImages = new ObservableCollection<ProfileImageItemViewModel>(tempCollection.OrderBy(i => i.FileName));
                UpdateSelectAllCurrentProfileState();
            });
        }


        private async Task LoadAndFilterSuggestedImagesAsync(bool initialLoad = false)
        {
            IsBusy = true;
            if (initialLoad)
            {
                _allSuggestionsForProfileMasterList.Clear();
                var rawSuggestions = _mainWindowViewModel.GetLastModelSpecificSuggestions();

                if (rawSuggestions != null)
                {
                    var profileSpecificRawSuggestions = rawSuggestions
                        .Where(s => s.TargetCategoryProfileName.Equals(TargetProfile.CategoryName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var itemVMs = new List<SuggestedImageItemViewModel>();
                    var thumbnailTasks = new List<Task>();

                    foreach (var move in profileSpecificRawSuggestions)
                    {
                        string sourceFolderName = Path.GetFileName(Path.GetDirectoryName(move.SourceImage.FilePath) ?? string.Empty);
                        var itemVM = new SuggestedImageItemViewModel(move.SourceImage, move.Similarity, sourceFolderName);
                        itemVMs.Add(itemVM);
                        thumbnailTasks.Add(itemVM.LoadThumbnailAsync());
                    }
                    _allSuggestionsForProfileMasterList.AddRange(itemVMs);
                    await Task.WhenAll(thumbnailTasks);
                }
            }

            var filtered = _allSuggestionsForProfileMasterList
                .Where(s => s.SimilarityScore >= SimilarityThreshold)
                .OrderByDescending(s => s.SimilarityScore)
                .ThenBy(s => s.SourceFileName)
                .ToList();

            Application.Current.Dispatcher.Invoke(() =>
            {
                SuggestedImages = new ObservableCollection<SuggestedImageItemViewModel>(filtered);
                UpdateSelectAllSuggestedState();
            });
            IsBusy = false;
        }

        private void SetSelectionForAll<TItem>(ObservableCollection<TItem> collection, bool isSelected)
            where TItem : class
        {
            if (collection == null) return;
            foreach (var item in collection)
            {
                if (item is SuggestedImageItemViewModel sItem) sItem.IsSelected = isSelected;
                else if (item is ProfileImageItemViewModel pItem) pItem.IsSelected = isSelected;
            }
        }

        private void SuggestedImages_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (SuggestedImageItemViewModel item in e.OldItems) item.PropertyChanged -= SuggestedItem_PropertyChanged;
            if (e.NewItems != null)
                foreach (SuggestedImageItemViewModel item in e.NewItems) item.PropertyChanged += SuggestedItem_PropertyChanged;
            UpdateSelectAllSuggestedState();
        }

        private void SuggestedItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SuggestedImageItemViewModel.IsSelected))
                UpdateSelectAllSuggestedState();
        }

        private void CurrentProfileImages_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (ProfileImageItemViewModel item in e.OldItems) item.PropertyChanged -= ProfileItem_PropertyChanged;
            if (e.NewItems != null)
                foreach (ProfileImageItemViewModel item in e.NewItems) item.PropertyChanged += ProfileItem_PropertyChanged;
            UpdateSelectAllCurrentProfileState();
        }

        private void ProfileItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProfileImageItemViewModel.IsSelected))
                UpdateSelectAllCurrentProfileState();
        }

        private void UpdateSelectAllSuggestedState()
        {
            if (SuggestedImages == null || !SuggestedImages.Any())
            {
                _selectAllSuggested = false;
                OnPropertyChanged(nameof(SelectAllSuggested));
                return;
            }

            bool allSelected = SuggestedImages.All(item => item.IsSelected);
            bool noneSelected = SuggestedImages.All(item => !item.IsSelected);

            if (allSelected) _selectAllSuggested = true;
            else if (noneSelected) _selectAllSuggested = false;
            else _selectAllSuggested = null;

            OnPropertyChanged(nameof(SelectAllSuggested));
        }

        private void UpdateSelectAllCurrentProfileState()
        {
            if (CurrentProfileImages == null || !CurrentProfileImages.Any())
            {
                _selectAllCurrentProfile = false;
                OnPropertyChanged(nameof(SelectAllCurrentProfile));
                return;
            }

            bool allSelected = CurrentProfileImages.All(item => item.IsSelected);
            bool noneSelected = CurrentProfileImages.All(item => !item.IsSelected);

            if (allSelected) _selectAllCurrentProfile = true;
            else if (noneSelected) _selectAllCurrentProfile = false;
            else _selectAllCurrentProfile = null;

            OnPropertyChanged(nameof(SelectAllCurrentProfile));
        }


        private bool CanExecuteApplyChanges(object? parameter)
        {
            return !IsBusy &&
                   ((SuggestedImages != null && SuggestedImages.Any(s => s.IsSelected)) ||
                    (CurrentProfileImages != null && CurrentProfileImages.Any(c => c.IsSelected)));
        }

        private async Task ExecuteApplyChangesAsync(object? parameter)
        {
            IsBusy = true;
            SimpleFileLogger.LogHighLevelInfo($"ManageProfileSuggestionsVM: Rozpoczęto ApplyChanges dla profilu '{TargetProfile.CategoryName}'.");

            bool changesMade = false;
            var imagesToAddFromSuggestions = SuggestedImages?.Where(s => s.IsSelected).Select(s => s.OriginalImage).ToList() ?? new List<ImageFileEntry>();
            var imagesToRemoveFromProfile = CurrentProfileImages?.Where(p => p.IsSelected).Select(p => p.OriginalImage).ToList() ?? new List<ImageFileEntry>();

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

            List<string> handledSourceImagePathsForCacheClearing = new List<string>();

            if (imagesToRemoveFromProfile.Any())
            {
                SimpleFileLogger.Log($"ManageProfileSuggestionsVM: Przenoszenie {imagesToRemoveFromProfile.Count} obrazów z profilu '{TargetProfile.CategoryName}' do '{fullTargetMixPath}'.");
                List<string> pathsToRemoveFromProfileDefinition = new List<string>();
                foreach (var imageEntry in imagesToRemoveFromProfile)
                {
                    string sourcePath = imageEntry.FilePath;
                    string destFileName = Path.GetFileName(sourcePath);
                    string destPath = Path.Combine(fullTargetMixPath, destFileName);

                    try
                    {
                        if (File.Exists(sourcePath))
                        {
                            string uniqueDestPath = GenerateUniqueTargetPath(fullTargetMixPath, destFileName, "_moved");

                            if (!uniqueDestPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Move(sourcePath, uniqueDestPath);
                                SimpleFileLogger.Log($"Przeniesiono: '{sourcePath}' -> '{uniqueDestPath}'");
                                pathsToRemoveFromProfileDefinition.Add(sourcePath);
                                changesMade = true;
                            }
                            else
                            {
                                SimpleFileLogger.Log($"Plik '{sourcePath}' już jest w folderze docelowym lub ma taką samą ścieżkę. Pomijanie przenoszenia fizycznego, ale zostanie usunięty z definicji profilu.");
                                pathsToRemoveFromProfileDefinition.Add(sourcePath);
                                changesMade = true;
                            }
                        }
                        else
                        {
                            SimpleFileLogger.LogWarning($"Plik źródłowy '{sourcePath}' nie istnieje. Zostanie usunięty z definicji profilu, jeśli tam jest.");
                            pathsToRemoveFromProfileDefinition.Add(sourcePath);
                            changesMade = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        SimpleFileLogger.LogError($"Błąd podczas przenoszenia '{sourcePath}' do '{destPath}': {ex.Message}", ex);
                    }
                }
                if (pathsToRemoveFromProfileDefinition.Any())
                {
                    TargetProfile.SourceImagePaths.RemoveAll(p => pathsToRemoveFromProfileDefinition.Contains(p, StringComparer.OrdinalIgnoreCase));
                }
            }

            if (imagesToAddFromSuggestions.Any())
            {
                SimpleFileLogger.Log($"ManageProfileSuggestionsVM: Dodawanie {imagesToAddFromSuggestions.Count} obrazów do profilu '{TargetProfile.CategoryName}'.");
                string targetProfileCharacterFolder = _profileService.GetCharacterNameFromCategory(TargetProfile.CategoryName);
                string targetProfileFullPath = Path.Combine(modelPath, SanitizeFolderName(targetProfileCharacterFolder));
                Directory.CreateDirectory(targetProfileFullPath);

                List<string> pathsToAddToProfileDefinition = new List<string>();
                foreach (var imageEntry in imagesToAddFromSuggestions)
                {
                    string sourcePath = imageEntry.FilePath; // To jest oryginalna ścieżka z folderu "Mix"
                    handledSourceImagePathsForCacheClearing.Add(sourcePath); // Dodaj do listy do wyczyszczenia z cache

                    string destFileName = Path.GetFileName(sourcePath);
                    string destPath = Path.Combine(targetProfileFullPath, destFileName);

                    try
                    {
                        if (File.Exists(sourcePath))
                        {
                            string uniqueDestPath = GenerateUniqueTargetPath(targetProfileFullPath, destFileName, "_added");

                            if (!uniqueDestPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Move(sourcePath, uniqueDestPath);
                                SimpleFileLogger.Log($"Przeniesiono (dodano do profilu): '{sourcePath}' -> '{uniqueDestPath}'");
                                pathsToAddToProfileDefinition.Add(uniqueDestPath);
                                changesMade = true;
                            }
                            else
                            {
                                SimpleFileLogger.Log($"Plik '{sourcePath}' już jest w folderze docelowym profilu. Upewnianie się, że jest w definicji.");
                                pathsToAddToProfileDefinition.Add(sourcePath);
                                changesMade = true;
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
                if (pathsToAddToProfileDefinition.Any())
                {
                    foreach (var pathToAdd in pathsToAddToProfileDefinition)
                    {
                        if (!TargetProfile.SourceImagePaths.Any(p => p.Equals(pathToAdd, StringComparison.OrdinalIgnoreCase)))
                        {
                            TargetProfile.SourceImagePaths.Add(pathToAdd);
                        }
                    }
                }
            }

            if (changesMade)
            {
                SimpleFileLogger.Log($"ManageProfileSuggestionsVM: Zmiany wprowadzone. Przeliczanie profilu '{TargetProfile.CategoryName}'.");
                var dummyProgress = new Progress<ProgressReport>();
                var imagesForProfileRegen = new List<ImageFileEntry>();
                var currentSourcePathsCopy = TargetProfile.SourceImagePaths.ToList(); // Kopia na wypadek modyfikacji
                foreach (var path in currentSourcePathsCopy)
                {
                    if (File.Exists(path))
                    {
                        var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                        if (entry != null) imagesForProfileRegen.Add(entry);
                    }
                    else
                    {
                        TargetProfile.SourceImagePaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
                        SimpleFileLogger.LogWarning($"Plik '{path}' nie istnieje podczas regeneracji profilu '{TargetProfile.CategoryName}'. Usunięto z definicji.");
                    }
                }
                await _profileService.GenerateProfileAsync(TargetProfile.CategoryName, imagesForProfileRegen, dummyProgress, CancellationToken.None);

                // *** KLUCZOWA ZMIANA - POCZĄTEK ***
                // Wyczyść obsłużone sugestie z cache MainWindowViewModel PRZED odświeżeniem profili w MainWindowViewModel
                if (handledSourceImagePathsForCacheClearing.Any())
                {
                    SimpleFileLogger.Log($"ManageProfileSuggestionsVM: Czyszczenie {handledSourceImagePathsForCacheClearing.Count} obsłużonych sugestii z cache MainWindowViewModel dla profilu '{TargetProfile.CategoryName}'.");
                    _mainWindowViewModel.ClearHandledSuggestionsForProfile(handledSourceImagePathsForCacheClearing, TargetProfile.CategoryName);
                }
                // *** KLUCZOWA ZMIANA - KONIEC ***

                await _mainWindowViewModel.RefreshProfilesAfterChangeAsync();
            }
            else if (handledSourceImagePathsForCacheClearing.Any())
            {
                // Jeśli nie było zmian w plikach (np. wszystkie były już na miejscu),
                // ale jakieś sugestie zostały "zaakceptowane" (zaznaczone),
                // to i tak powinniśmy je wyczyścić z cache.
                SimpleFileLogger.Log($"ManageProfileSuggestionsVM: Brak fizycznych zmian w plikach, ale czyszczenie {handledSourceImagePathsForCacheClearing.Count} 'zaakceptowanych' sugestii z cache dla '{TargetProfile.CategoryName}'.");
                _mainWindowViewModel.ClearHandledSuggestionsForProfile(handledSourceImagePathsForCacheClearing, TargetProfile.CategoryName);
                // W tym przypadku nie ma potrzeby RefreshProfilesAfterChangeAsync, bo profile się nie zmieniły,
                // ale ClearHandledSuggestionsForProfile samo w sobie wywoła RefreshPendingSuggestionCountsFromCache.
            }


            IsBusy = false;
            SimpleFileLogger.LogHighLevelInfo($"ManageProfileSuggestionsVM: Zakończono ApplyChanges dla profilu '{TargetProfile.CategoryName}'. Zmiany: {changesMade}");
            CloseAction?.Invoke(changesMade);
        }

        private string GenerateUniqueTargetPath(string targetDirectory, string originalFileNameWithExtension, string suffixIfConflict)
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
                    SimpleFileLogger.LogWarning($"GenerateUniqueTargetPath: Osiągnięto limit liczników, użyto GUID: {finalPath}");
                    break;
                }
            }
            return finalPath;
        }

        private string SanitizeFolderName(string name)
        {
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