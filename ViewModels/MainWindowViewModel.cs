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
using System.Diagnostics;
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

        private string _statusMessage = "Gotowy."; // Ogólny status aplikacji
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

        private int _currentProgress;
        public int CurrentProgress { get => _currentProgress; set => SetProperty(ref _currentProgress, value); }

        private int _maximumProgress = 100;
        public int MaximumProgress { get => _maximumProgress; set => SetProperty(ref _maximumProgress, value); }

        private bool _isProgressIndeterminate;
        public bool IsProgressIndeterminate { get => _isProgressIndeterminate; set => SetProperty(ref _isProgressIndeterminate, value); }

        private string _progressStatusText = "Gotowy."; // Status dla paska postępu
        public string ProgressStatusText { get => _progressStatusText; set => SetProperty(ref _progressStatusText, value); }

        private string _processingSpeedText = string.Empty;
        public string ProcessingSpeedText { get => _processingSpeedText; set => SetProperty(ref _processingSpeedText, value); }

        private readonly Stopwatch _operationStopwatch = new Stopwatch();
        private long _itemsProcessedForSpeedReport = 0;
        private DateTime _lastSpeedReportTime = DateTime.MinValue;

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

            LoadProfilesCommand = new AsyncRelayCommand(param => RunLongOperation(
                (token, progress) => InternalExecuteLoadProfilesAsync(token, progress), "Ładowanie profili"), CanExecuteLoadProfiles);
            GenerateProfileCommand = new AsyncRelayCommand(param => RunLongOperation(
                (token, progress) => ExecuteGenerateProfileAsync(token, progress), "Generowanie profilu"), CanExecuteGenerateProfile);
            SaveProfilesCommand = new AsyncRelayCommand(param => RunLongOperation(
                (token, progress) => ExecuteSaveAllProfilesAsync(token, progress), "Zapisywanie wszystkich profili"), CanExecuteSaveAllProfiles);
            RemoveProfileCommand = new AsyncRelayCommand(parameter => RunLongOperation(
                (token, progress) => ExecuteRemoveProfileAsync(parameter, token, progress), "Usuwanie profilu"), CanExecuteRemoveProfile);

            AutoCreateProfilesCommand = new AsyncRelayCommand(param => RunLongOperation(
                 (token, progress) => ExecuteAutoCreateProfilesAsync(token, progress), "Automatyczne tworzenie profili"), CanExecuteAutoCreateProfiles);
            SuggestImagesCommand = new AsyncRelayCommand(param => RunLongOperation(
                 (token, progress) => ExecuteSuggestImagesAsync(token, progress), "Globalne wyszukiwanie sugestii"), CanExecuteSuggestImages);
            MatchModelSpecificCommand = new AsyncRelayCommand(param => RunLongOperation(
                 (token, progress) => ExecuteMatchModelSpecificAsync(param, token, progress), "Dopasowywanie dla modelki"), CanExecuteMatchModelSpecific);
            CheckCharacterSuggestionsCommand = new AsyncRelayCommand(param => RunLongOperation(
                (token, progress) => ExecuteCheckCharacterSuggestionsAsync(param, token, progress), "Sprawdzanie sugestii dla postaci"), CanExecuteCheckCharacterSuggestions);
            RemoveModelTreeCommand = new AsyncRelayCommand(param => RunLongOperation(
                (token, progress) => ExecuteRemoveModelTreeAsync(param, token, progress), "Usuwanie całej modelki"), CanExecuteRemoveModelTree);
            AnalyzeModelForSplittingCommand = new AsyncRelayCommand(param => RunLongOperation(
                (token, progress) => ExecuteAnalyzeModelForSplittingAsync(param, token, progress), "Analiza profili pod kątem podziału"), CanExecuteAnalyzeModelForSplitting);
            OpenSplitProfileDialogCommand = new AsyncRelayCommand(param => RunLongOperation(
                (token, progress) => ExecuteOpenSplitProfileDialogAsync(param, token, progress), "Otwieranie okna podziału profilu"), CanExecuteOpenSplitProfileDialog);
            RemoveDuplicatesInModelCommand = new AsyncRelayCommand(param => RunLongOperation(
                (token, progress) => ExecuteRemoveDuplicatesInModelAsync(param, token, progress), "Usuwanie duplikatów"), CanExecuteRemoveDuplicatesInModel);
            ApplyAllMatchesForModelCommand = new AsyncRelayCommand(param => RunLongOperation(
                (token, progress) => ExecuteApplyAllMatchesForModelAsync(param, token, progress), "Automatyczne stosowanie dopasowań"), CanExecuteApplyAllMatchesForModel);
            EnsureThumbnailsLoadedCommand = new AsyncRelayCommand(param => RunLongOperation(
                (token, progress) => ExecuteEnsureThumbnailsLoadedAsync(param, token, progress), "Ładowanie miniaturek"), CanExecuteEnsureThumbnailsLoaded);

            SaveAppSettingsCommand = new AsyncRelayCommand(param => RunLongOperation( // SaveAppSettings nie potrzebuje explicite IProgress, ale RunLongOperation go dostarczy
                 (token, progress) => ExecuteSaveAppSettingsAsync(token), "Zapisywanie ustawień aplikacji"), CanExecuteSaveAppSettings);

            AddFilesToProfileCommand = new RelayCommand(ExecuteAddFilesToProfile, CanExecuteAddFilesToProfile);
            ClearFilesFromProfileCommand = new RelayCommand(ExecuteClearFilesFromProfile, CanExecuteClearFilesFromProfile);
            CreateNewProfileSetupCommand = new RelayCommand(ExecuteCreateNewProfileSetup, CanExecuteCreateNewProfileSetup);
            SelectLibraryPathCommand = new RelayCommand(ExecuteSelectLibraryPath, CanExecuteSelectLibraryPath);
            CancelCurrentOperationCommand = new RelayCommand(ExecuteCancelCurrentOperation, CanExecuteCancelCurrentOperation);
        }

        private void ReportProgress(ProgressReport report)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (report.TotalItems > 0 && report.ProcessedItems <= report.TotalItems)
                {
                    CurrentProgress = report.ProcessedItems;
                    MaximumProgress = report.TotalItems;
                    IsProgressIndeterminate = false;
                }
                else if (report.IsIndeterminate || report.TotalItems == 0) // Obsługa IsIndeterminate z ProgressReport
                {
                    CurrentProgress = 0;
                    MaximumProgress = 100;
                    IsProgressIndeterminate = true;
                }
                else // Na wypadek, gdyby ProcessedItems przekroczyło TotalItems, lub TotalItems < 0
                {
                    CurrentProgress = report.ProcessedItems; // Pokaż aktualny postęp
                    MaximumProgress = Math.Max(report.ProcessedItems, report.TotalItems); // Upewnij się, że maximum nie jest mniejsze
                    IsProgressIndeterminate = false;
                }

                ProgressStatusText = report.StatusMessage ?? string.Empty;

                if (_operationStopwatch.IsRunning)
                {
                    var elapsedSeconds = _operationStopwatch.Elapsed.TotalSeconds;
                    // Użyj ProcessedItems bezpośrednio z raportu, ponieważ _itemsProcessedForSpeedReport
                    // jest aktualizowane tylko przy generowaniu tekstu prędkości.
                    // Jeśli TotalItems jest dostępne, użyj go do obliczenia prędkości w kontekście całości.
                    long currentReportedProcessedItems = report.IsIndeterminate ? _itemsProcessedForSpeedReport + 1 : (long)report.ProcessedItems;


                    if (elapsedSeconds > 0.2 && currentReportedProcessedItems > _itemsProcessedForSpeedReport)
                    {
                        double itemsSinceLastReport = currentReportedProcessedItems - _itemsProcessedForSpeedReport;
                        double timeSinceLastReport = (DateTime.UtcNow - _lastSpeedReportTime).TotalSeconds;

                        if (timeSinceLastReport > 0.1 && itemsSinceLastReport > 0)
                        {
                            double speed = itemsSinceLastReport / timeSinceLastReport;
                            ProcessingSpeedText = $"Prędkość: {speed:F1} jedn./s";
                            _itemsProcessedForSpeedReport = currentReportedProcessedItems;
                            _lastSpeedReportTime = DateTime.UtcNow;
                        }
                        else if (elapsedSeconds > 1.0 && !report.IsIndeterminate && report.TotalItems > 0)
                        {
                            double overallSpeed = report.ProcessedItems / elapsedSeconds;
                            ProcessingSpeedText = $"Prędkość: {overallSpeed:F1} jedn./s (średnia)";
                        }
                    }
                    else if (report.IsIndeterminate && elapsedSeconds > 1.0) // Dla indeterminate, pokaż upływający czas
                    {
                        ProcessingSpeedText = $"Czas: {elapsedSeconds:F1}s";
                    }
                }
            });
        }

        public async Task RunLongOperation(Func<CancellationToken, IProgress<ProgressReport>, Task> operationWithProgress, string statusMessagePrefix)
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
            StatusMessage = $"{statusMessagePrefix}... (Można anulować)"; // Ogólny status
            ProgressStatusText = $"{statusMessagePrefix}..."; // Status dla paska
            IsProgressIndeterminate = true;
            CurrentProgress = 0;
            MaximumProgress = 100;
            ProcessingSpeedText = string.Empty;
            _itemsProcessedForSpeedReport = 0;
            _lastSpeedReportTime = DateTime.UtcNow;
            _operationStopwatch.Restart();

            SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Rozpoczęto '{statusMessagePrefix}'. Token: {token.GetHashCode()}");
            var progressReporter = new Progress<ProgressReport>(ReportProgress);

            try
            {
                await operationWithProgress(token, progressReporter);
                if (token.IsCancellationRequested)
                {
                    StatusMessage = $"{statusMessagePrefix} - Anulowano.";
                    ProgressStatusText = "Anulowano.";
                    SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Operacja '{statusMessagePrefix}' (token: {token.GetHashCode()}) anulowana przez użytkownika.");
                }
                else
                {
                    StatusMessage = $"{statusMessagePrefix} - Zakończono.";
                    ProgressStatusText = "Zakończono.";
                    SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Operacja '{statusMessagePrefix}' (token: {token.GetHashCode()}) zakończona.");
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = $"{statusMessagePrefix} - Anulowano.";
                ProgressStatusText = "Anulowano.";
                SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Operacja '{statusMessagePrefix}' (token: {token.GetHashCode()}) anulowana (OperationCanceledException).");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Błąd podczas: {statusMessagePrefix}: {ex.Message}";
                ProgressStatusText = "Błąd!";
                SimpleFileLogger.LogError($"RunLongOperation: Błąd podczas operacji '{statusMessagePrefix}' (token: {token.GetHashCode()})", ex);
                MessageBox.Show($"Wystąpił nieoczekiwany błąd podczas operacji '{statusMessagePrefix}':\n{ex.Message}\n\nSprawdź logi aplikacji.", "Błąd Krytyczny Operacji", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                _operationStopwatch.Stop();

                if (token.IsCancellationRequested)
                {
                    CurrentProgress = 0; // Resetuj pasek przy anulowaniu
                    MaximumProgress = 100;
                    IsProgressIndeterminate = false; // Wyłącz indeterminate
                }
                else if (MaximumProgress > 0) // Jeśli nie anulowano i znamy max, ustaw na 100%
                {
                    CurrentProgress = MaximumProgress;
                    IsProgressIndeterminate = false;
                }
                else // Jeśli nie anulowano, ale nie znamy max (np. błąd przed ustawieniem)
                {
                    CurrentProgress = 0;
                    MaximumProgress = 100;
                    IsProgressIndeterminate = false;
                }


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
                if (string.IsNullOrEmpty(ProgressStatusText) || ProgressStatusText.EndsWith("...") || ProgressStatusText.Contains(statusMessagePrefix))
                {
                    if (token.IsCancellationRequested) ProgressStatusText = "Anulowano.";
                    else if (StatusMessage.Contains("Błąd")) ProgressStatusText = "Błąd!";
                    else ProgressStatusText = "Zakończono.";
                }

                ProcessingSpeedText = string.Empty;
                SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Zakończono (finally) dla '{statusMessagePrefix}' (token: {token.GetHashCode()}). Aktualny StatusMessage: {StatusMessage}, ProgressStatusText: {ProgressStatusText}");
            }
        }

        public Task RunLongOperation(Func<CancellationToken, Task> operation, string statusMessagePrefix)
        {
            return RunLongOperation(async (token, progress) =>
            {
                progress.Report(new ProgressReport { OperationName = statusMessagePrefix, StatusMessage = statusMessagePrefix + "...", IsIndeterminate = true });
                await operation(token);
                progress.Report(new ProgressReport { OperationName = statusMessagePrefix, StatusMessage = statusMessagePrefix + " - Zakończono.", ProcessedItems = 1, TotalItems = 1 });
            }, statusMessagePrefix);
        }

        private void UpdateCurrentProfileNameForEdit()
        {
            if (!string.IsNullOrWhiteSpace(ModelNameInput) && !string.IsNullOrWhiteSpace(CharacterNameInput)) CurrentProfileNameForEdit = $"{ModelNameInput} - {CharacterNameInput}";
            else if (!string.IsNullOrWhiteSpace(ModelNameInput)) CurrentProfileNameForEdit = $"{ModelNameInput} - General";
            else CurrentProfileNameForEdit = string.Empty;
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
            foreach (char invalidChar in invalidChars.Distinct()) sanitizedName = sanitizedName.Replace(invalidChar.ToString(), "_");
            sanitizedName = sanitizedName.Replace(":", "_").Replace("?", "_").Replace("*", "_").Replace("\"", "_").Replace("<", "_").Replace(">", "_").Replace("|", "_").Replace("/", "_").Replace("\\", "_");
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
                ModelNameInput = model; CharacterNameInput = characterFullName;
                var newImageFiles = new ObservableCollection<ImageFileEntry>();
                if (_selectedProfile.SourceImagePaths != null)
                {
                    foreach (var path in _selectedProfile.SourceImagePaths)
                    {
                        if (File.Exists(path)) newImageFiles.Add(new ImageFileEntry { FilePath = path, FileName = Path.GetFileName(path) });
                        else SimpleFileLogger.LogWarning($"OSTRZEŻENIE (UpdateEditFields): Ścieżka '{path}' dla profilu '{_selectedProfile.CategoryName}' nie istnieje.");
                    }
                }
                ImageFiles = newImageFiles;
            }
            else { CurrentProfileNameForEdit = string.Empty; ModelNameInput = string.Empty; CharacterNameInput = string.Empty; ImageFiles = new ObservableCollection<ImageFileEntry>(); }
        }

        private void ClearModelSpecificSuggestionsCache()
        {
            SimpleFileLogger.LogHighLevelInfo("ClearModelSpecificSuggestionsCache: Czyszczenie cache sugestii.");
            _lastModelSpecificSuggestions.Clear(); _lastScannedModelNameForSuggestions = "__CACHE_CLEARED__";
            RefreshPendingSuggestionCountsFromCache();
        }

        private UserSettings GetCurrentSettings() => new UserSettings { LibraryRootPath = this.LibraryRootPath, SourceFolderNamesInput = this.SourceFolderNamesInput, SuggestionSimilarityThreshold = this.SuggestionSimilarityThreshold, EnableDebugLogging = this.EnableDebugLogging };

        public void ApplySettings(UserSettings settings)
        {
            if (settings == null) { SimpleFileLogger.LogWarning("ApplySettings: Otrzymano null jako ustawienia. Inicjalizacja wartości domyślnych dla VM."); LibraryRootPath = string.Empty; SourceFolderNamesInput = "Mix,Mieszane,Unsorted,Downloaded"; SuggestionSimilarityThreshold = 0.85; EnableDebugLogging = false; SimpleFileLogger.IsDebugLoggingEnabled = false; }
            else { LibraryRootPath = settings.LibraryRootPath; SourceFolderNamesInput = settings.SourceFolderNamesInput; SuggestionSimilarityThreshold = settings.SuggestionSimilarityThreshold; EnableDebugLogging = settings.EnableDebugLogging; SimpleFileLogger.IsDebugLoggingEnabled = this.EnableDebugLogging; }
            SimpleFileLogger.LogHighLevelInfo($"Zastosowano ustawienia w ViewModel. Debug logging: {(EnableDebugLogging ? "Enabled" : "Disabled")}.");
        }

        // Poprawiona metoda - oznaczona jako async Task
        private async Task ExecuteSaveAppSettingsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await _settingsService.SaveSettingsAsync(GetCurrentSettings());
            // StatusMessage będzie zarządzany przez RunLongOperation
            SimpleFileLogger.LogHighLevelInfo("Ustawienia aplikacji zapisane (na żądanie).");
        }

        public async Task InitializeAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            SimpleFileLogger.LogHighLevelInfo("ViewModel: InitializeAsync start.");
            progress.Report(new ProgressReport { OperationName = "Inicjalizacja", StatusMessage = "Wczytywanie ustawień...", IsIndeterminate = true });
            ApplySettings(await _settingsService.LoadSettingsAsync());
            token.ThrowIfCancellationRequested();

            await InternalExecuteLoadProfilesAsync(token, progress);

            if (string.IsNullOrEmpty(LibraryRootPath)) StatusMessage = "Gotowy. Wybierz folder biblioteki."; // Ten StatusMessage może być nadpisany przez ProgressStatusText w UI
            else if (!Directory.Exists(LibraryRootPath)) StatusMessage = $"Uwaga: Folder biblioteki '{LibraryRootPath}' nie istnieje.";
            else StatusMessage = "Gotowy.";
            SimpleFileLogger.LogHighLevelInfo("ViewModel: InitializeAsync koniec.");
        }

        public async Task OnAppClosingAsync()
        {
            SimpleFileLogger.LogHighLevelInfo("ViewModel: OnAppClosingAsync - Anulowanie operacji i zapis ustawień...");
            if (_activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested)
            {
                _activeLongOperationCts.Cancel();
                try { using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await Task.WhenAny(Task.Delay(Timeout.Infinite, _activeLongOperationCts.Token), Task.Delay(Timeout.Infinite, timeoutCts.Token)); }
                catch (OperationCanceledException) { /* Expected */ }
                catch (Exception ex) { SimpleFileLogger.LogWarning($"OnAppClosingAsync: Wyjątek podczas oczekiwania na anulowanie: {ex.Message}"); }
                _activeLongOperationCts?.Dispose(); _activeLongOperationCts = null;
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
        private bool CanExecuteCheckCharacterSuggestions(object? parameter) { if (IsBusy) return false; var p = (parameter as CategoryProfile) ?? SelectedProfile; return p != null && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) && !string.IsNullOrWhiteSpace(SourceFolderNamesInput) && p.CentroidEmbedding != null && p.PendingSuggestionsCount > 0; }
        private bool CanExecuteMatchModelSpecific(object? parameter) { if (IsBusy || !(parameter is ModelDisplayViewModel m)) return false; return !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) && m.HasCharacterProfiles && !string.IsNullOrWhiteSpace(SourceFolderNamesInput); }
        private bool CanExecuteRemoveModelTree(object? parameter) => !IsBusy && parameter is ModelDisplayViewModel;
        private bool CanExecuteSaveAppSettings(object? arg) => !IsBusy;
        private bool CanExecuteAddFilesToProfile(object? arg) => !IsBusy;
        private bool CanExecuteClearFilesFromProfile(object? arg) => !IsBusy && ImageFiles.Any();
        private bool CanExecuteCreateNewProfileSetup(object? arg) => !IsBusy;
        private bool CanExecuteSelectLibraryPath(object? arg) => !IsBusy;
        private bool CanExecuteAnalyzeModelForSplitting(object? parameter) => !IsBusy && parameter is ModelDisplayViewModel m && m.HasCharacterProfiles;
        private bool CanExecuteOpenSplitProfileDialog(object? parameter) => !IsBusy && parameter is CategoryProfile cp && cp.HasSplitSuggestion;
        private bool CanExecuteCancelCurrentOperation(object? parameter) => IsBusy && _activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested;
        private bool CanExecuteEnsureThumbnailsLoaded(object? parameter) => !IsBusy && parameter is IEnumerable<ImageFileEntry> images && images.Any();
        private bool CanExecuteRemoveDuplicatesInModel(object? parameter) { return parameter is ModelDisplayViewModel m && !IsBusy && m.HasCharacterProfiles; }
        private bool CanExecuteApplyAllMatchesForModel(object? parameter) { if (!(parameter is ModelDisplayViewModel m) || IsBusy) return false; bool hasRelevant = (_lastScannedModelNameForSuggestions == m.ModelName || string.IsNullOrEmpty(_lastScannedModelNameForSuggestions)) && _lastModelSpecificSuggestions.Any(s => s.Similarity >= SuggestionSimilarityThreshold && _profileService.GetModelNameFromCategory(s.TargetCategoryProfileName) == m.ModelName); return m.HasCharacterProfiles && hasRelevant; }

        private async Task InternalExecuteLoadProfilesAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            SimpleFileLogger.LogHighLevelInfo($"InternalExecuteLoadProfilesAsync. RefreshFlag: {_isRefreshingProfilesPostMove}. Token: {token.GetHashCode()}");
            progress.Report(new ProgressReport { OperationName = "Ładowanie Profili", StatusMessage = "Wczytywanie profili z dysku...", IsIndeterminate = true });

            token.ThrowIfCancellationRequested();
            string? prevSelectedName = SelectedProfile?.CategoryName;

            await _profileService.LoadProfilesAsync(token);
            token.ThrowIfCancellationRequested();

            var flatProfiles = _profileService.GetAllProfiles()?.OrderBy(p => p.CategoryName).ToList();
            int totalModels = 0; // Poprawka: Zadeklaruj totalModels przed blokiem if

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HierarchicalProfilesList.Clear();
                if (flatProfiles?.Any() == true)
                {
                    var grouped = flatProfiles.GroupBy(p => _profileService.GetModelNameFromCategory(p.CategoryName)).OrderBy(g => g.Key);
                    int modelsProcessed = 0;
                    totalModels = grouped.Count(); // Przypisz wartość tutaj
                    progress.Report(new ProgressReport { ProcessedItems = modelsProcessed, TotalItems = totalModels, StatusMessage = "Grupowanie profili..." });

                    foreach (var modelGroup in grouped)
                    {
                        token.ThrowIfCancellationRequested();
                        var modelVM = new ModelDisplayViewModel(modelGroup.Key);
                        foreach (var charProfile in modelGroup.OrderBy(p => _profileService.GetCharacterNameFromCategory(p.CategoryName)))
                        {
                            modelVM.AddCharacterProfile(charProfile);
                        }
                        HierarchicalProfilesList.Add(modelVM);
                        modelsProcessed++;
                        progress.Report(new ProgressReport { ProcessedItems = modelsProcessed, TotalItems = totalModels, StatusMessage = $"Przetwarzanie modelu {modelGroup.Key} ({modelsProcessed}/{totalModels})..." });
                    }
                }
                // StatusMessage jest teraz zarządzany przez RunLongOperation
                // StatusMessage = $"Załadowano {HierarchicalProfilesList.Sum(m => m.CharacterProfiles.Count)} profili dla {HierarchicalProfilesList.Count} modelek.";
                SimpleFileLogger.LogHighLevelInfo($"Wątek UI: Załadowano profile.");

                if (!string.IsNullOrEmpty(prevSelectedName)) SelectedProfile = flatProfiles?.FirstOrDefault(p => p.CategoryName.Equals(prevSelectedName, StringComparison.OrdinalIgnoreCase));
                else if (SelectedProfile != null && !(flatProfiles?.Any(p => p.CategoryName == SelectedProfile.CategoryName) ?? false)) SelectedProfile = null;

                OnPropertyChanged(nameof(AnyProfilesLoaded));
                if (_lastModelSpecificSuggestions.Any()) RefreshPendingSuggestionCountsFromCache();

                // Poprawione użycie totalModels
                progress.Report(new ProgressReport { ProcessedItems = totalModels, TotalItems = totalModels, StatusMessage = $"Załadowano {HierarchicalProfilesList.Sum(m => m.CharacterProfiles.Count)} profili." });
            });
        }

        private async Task ExecuteGenerateProfileAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            bool profilesActuallyRegenerated = false;
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) || string.IsNullOrWhiteSpace(ModelNameInput) || string.IsNullOrWhiteSpace(CharacterNameInput))
            { StatusMessage = "Błąd: Nazwa modelki i postaci oraz pełna nazwa profilu muszą być zdefiniowane."; MessageBox.Show(StatusMessage, "Błąd danych profilu", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            string catName = CurrentProfileNameForEdit;
            SimpleFileLogger.LogHighLevelInfo($"Generowanie profilu '{catName}' ({ImageFiles.Count} obr.). Token: {token.GetHashCode()}");
            progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = ImageFiles.Count, OperationName = $"Generowanie '{catName}'", StatusMessage = $"Przygotowywanie plików..." });

            List<ImageFileEntry> entriesToProcess = new List<ImageFileEntry>();
            int filesPrepared = 0;
            foreach (var file in ImageFiles)
            {
                token.ThrowIfCancellationRequested();
                if (file.FileSize == 0 || file.FileLastModifiedUtc == DateTime.MinValue)
                {
                    progress.Report(new ProgressReport { ProcessedItems = filesPrepared, TotalItems = ImageFiles.Count, StatusMessage = $"Metadane: {file.FileName}..." });
                    var updatedEntry = await _imageMetadataService.ExtractMetadataAsync(file.FilePath);
                    if (updatedEntry != null) entriesToProcess.Add(updatedEntry);
                    else SimpleFileLogger.LogWarning($"ExecuteGenerateProfileAsync: Nie udało się załadować metadanych dla {file.FilePath}, pomijam.");
                }
                else entriesToProcess.Add(file);
                filesPrepared++;
                progress.Report(new ProgressReport { ProcessedItems = filesPrepared, TotalItems = ImageFiles.Count, StatusMessage = $"Przygotowano {filesPrepared}/{ImageFiles.Count}..." });
            }
            token.ThrowIfCancellationRequested();
            if (!entriesToProcess.Any() && ImageFiles.Any()) { StatusMessage = "Błąd: Nie udało się przetworzyć żadnego z wybranych plików."; MessageBox.Show(StatusMessage, "Błąd plików", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            await _profileService.GenerateProfileAsync(catName, entriesToProcess, progress, token); // Przekazanie progress
            profilesActuallyRegenerated = true;
            token.ThrowIfCancellationRequested();

            if (profilesActuallyRegenerated)
            {
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token, progress);
                _isRefreshingProfilesPostMove = false;
                SelectedProfile = _profileService.GetProfile(catName);
            }
            // StatusMessage i ProgressStatusText zostaną ustawione przez RunLongOperation
        }

        private async Task ExecuteSaveAllProfilesAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            SimpleFileLogger.LogHighLevelInfo($"Zapis wszystkich profili. Token: {token.GetHashCode()}");
            var allProfiles = _profileService.GetAllProfiles();
            int totalToSave = allProfiles.Select(p => _profileService.GetModelNameFromCategory(p.CategoryName)).Distinct().Count();
            progress.Report(new ProgressReport { OperationName = "Zapis Profili", StatusMessage = "Rozpoczynanie zapisu...", TotalItems = totalToSave, ProcessedItems = 0 });

            // SaveAllProfilesAsync w ProfileService powinno teraz akceptować IProgress<T> lub raportować wewnętrznie.
            // Na razie zakładamy, że SaveAllProfilesAsync jest jedną operacją dla paska postępu.
            await _profileService.SaveAllProfilesAsync(token);
            token.ThrowIfCancellationRequested();

            progress.Report(new ProgressReport { ProcessedItems = totalToSave, TotalItems = totalToSave, StatusMessage = "Wszystkie profile zapisane." });
            MessageBox.Show("Wszystkie profile zapisane.", "Zapisano", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task ExecuteRemoveProfileAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            bool profileActuallyRemoved = false;
            var profileToRemove = (parameter as CategoryProfile) ?? SelectedProfile;
            if (profileToRemove == null) { MessageBox.Show("Wybierz profil do usunięcia.", "Brak wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            string profileName = profileToRemove.CategoryName;
            progress.Report(new ProgressReport { OperationName = $"Usuwanie Profilu", StatusMessage = $"Przygotowywanie '{profileName}'...", IsIndeterminate = true });
            token.ThrowIfCancellationRequested();

            if (MessageBox.Show($"Usunąć profil '{profileName}'?", "Potwierdź", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                SimpleFileLogger.LogHighLevelInfo($"Usuwanie profilu '{profileName}'.");
                progress.Report(new ProgressReport { StatusMessage = $"Usuwanie '{profileName}'..." });

                if (await _profileService.RemoveProfileAsync(profileName, token))
                {
                    if (SelectedProfile?.CategoryName == profileName) SelectedProfile = null;
                    profileActuallyRemoved = true;
                }
            }

            if (profileActuallyRemoved)
            {
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token, progress);
                _isRefreshingProfilesPostMove = false;
            }
            // StatusMessage i ProgressStatusText zostaną ustawione przez RunLongOperation
        }

        private async void ExecuteAddFilesToProfile(object? parameter = null)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "Obrazy|*.jpg;*.jpeg;*.png;*.webp|Wszystkie pliki|*.*", Title = "Wybierz obrazy", Multiselect = true };
            if (openFileDialog.ShowDialog() == true)
            {
                IsBusy = true;
                string originalStatus = ProgressStatusText; // Użyj ProgressStatusText
                ProgressStatusText = "Dodawanie plików i ładowanie metadanych...";
                CurrentProgress = 0; MaximumProgress = openFileDialog.FileNames.Length; IsProgressIndeterminate = false;
                int addedCount = 0;
                try
                {
                    for (int i = 0; i < openFileDialog.FileNames.Length; i++)
                    {
                        string filePath = openFileDialog.FileNames[i];
                        CurrentProgress = i + 1;
                        ProgressStatusText = $"Dodawanie: {Path.GetFileName(filePath)} ({CurrentProgress}/{MaximumProgress})...";
                        if (!ImageFiles.Any(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            var entry = await _imageMetadataService.ExtractMetadataAsync(filePath);
                            if (entry != null) { ImageFiles.Add(entry); addedCount++; }
                            else SimpleFileLogger.LogWarning($"ExecuteAddFilesToProfile: Błąd metadanych dla: {filePath}.");
                        }
                    }
                    ProgressStatusText = addedCount > 0 ? $"Dodano {addedCount} plików." : "Nie dodano nowych plików.";
                }
                catch (Exception ex) { SimpleFileLogger.LogError("Błąd dodawania plików", ex); ProgressStatusText = "Błąd dodawania plików."; MessageBox.Show($"Błąd: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error); }
                finally { IsBusy = false; if (ProgressStatusText.StartsWith("Dodawanie")) ProgressStatusText = originalStatus; CurrentProgress = 0; MaximumProgress = 100; IsProgressIndeterminate = true; /* Reset progress */ }
            }
        }

        private void ExecuteClearFilesFromProfile(object? parameter = null) => ImageFiles.Clear();
        private void ExecuteCreateNewProfileSetup(object? parameter = null) { SelectedProfile = null; ModelNameInput = string.Empty; CharacterNameInput = string.Empty; ImageFiles.Clear(); StatusMessage = "Gotowy do utworzenia nowego profilu."; ProgressStatusText = StatusMessage; }
        private void ExecuteSelectLibraryPath(object? parameter = null) { if (IsBusy) return; IsBusy = true; try { var d = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog { Description = "Wybierz folder biblioteki", UseDescriptionForTitle = true, ShowNewFolderButton = true }; if (!string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath)) d.SelectedPath = LibraryRootPath; else if (Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))) d.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); if (d.ShowDialog(Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive)) == true) { LibraryRootPath = d.SelectedPath; StatusMessage = $"Wybrano folder: {LibraryRootPath}"; ProgressStatusText = StatusMessage; } } catch (Exception ex) { SimpleFileLogger.LogError("Błąd wyboru folderu", ex); MessageBox.Show($"Błąd: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error); } finally { IsBusy = false; } }

        private async Task ExecuteAutoCreateProfilesAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            SimpleFileLogger.LogHighLevelInfo($"AutoCreateProfiles: Rozpoczęto: {LibraryRootPath}.");
            progress.Report(new ProgressReport { OperationName = "Auto Tworzenie", StatusMessage = "Skanowanie folderu biblioteki...", IsIndeterminate = true });
            var mixedFoldersToIgnore = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
            token.ThrowIfCancellationRequested();
            List<string> modelDirectories; try { modelDirectories = Directory.GetDirectories(LibraryRootPath).ToList(); } catch (Exception ex) { SimpleFileLogger.LogError($"Błąd pobierania folderów modelek z '{LibraryRootPath}'", ex); StatusMessage = $"Błąd dostępu: {ex.Message}"; ProgressStatusText = StatusMessage; MessageBox.Show(StatusMessage, "Błąd Biblioteki", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            token.ThrowIfCancellationRequested();

            var modelProcessingTasks = new List<Task<(int profilesChanged, bool anyDataChanged)>>();
            int totalModelsToProcess = modelDirectories.Count; int modelsProcessedCount = 0;
            progress.Report(new ProgressReport { ProcessedItems = modelsProcessedCount, TotalItems = totalModelsToProcess, StatusMessage = $"Przygotowywanie {totalModelsToProcess} folderów modelek..." });

            foreach (var modelDir in modelDirectories)
            {
                token.ThrowIfCancellationRequested(); string currentModelName = Path.GetFileName(modelDir);
                if (string.IsNullOrWhiteSpace(currentModelName) || mixedFoldersToIgnore.Contains(currentModelName)) { SimpleFileLogger.Log($"AutoCreateProfiles: Pomijanie: '{currentModelName}'."); modelsProcessedCount++; progress.Report(new ProgressReport { ProcessedItems = modelsProcessedCount, TotalItems = totalModelsToProcess, StatusMessage = $"Pomijanie {currentModelName}..." }); continue; }

                var modelSpecificProgress = new Progress<ProgressReport>(modelReport => {
                    progress.Report(new ProgressReport { ProcessedItems = modelsProcessedCount, TotalItems = totalModelsToProcess, StatusMessage = $"'{currentModelName}': {modelReport.StatusMessage} ({modelReport.ProcessedItems}/{modelReport.TotalItems})" });
                });
                modelProcessingTasks.Add(Task.Run(async () => { var result = await InternalProcessDirectoryForProfileCreationAsync(modelDir, currentModelName, new List<string>(), mixedFoldersToIgnore, modelSpecificProgress, token); Interlocked.Increment(ref modelsProcessedCount); progress.Report(new ProgressReport { ProcessedItems = modelsProcessedCount, TotalItems = totalModelsToProcess, StatusMessage = $"Model '{currentModelName}' zakończony." }); return result; }, token));
            }
            var results = await Task.WhenAll(modelProcessingTasks); token.ThrowIfCancellationRequested();
            int totalProfilesCreatedOrUpdated = results.Sum(r => r.profilesChanged); bool anyProfileDataChangedDuringOperation = results.Any(r => r.anyDataChanged);
            StatusMessage = $"Automatyczne tworzenie zakończone. Zmieniono: {totalProfilesCreatedOrUpdated} profili."; // Ogólny status
            progress.Report(new ProgressReport { ProcessedItems = totalModelsToProcess, TotalItems = totalModelsToProcess, StatusMessage = StatusMessage });
            if (anyProfileDataChangedDuringOperation) { _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token, progress); _isRefreshingProfilesPostMove = false; }
            MessageBox.Show(StatusMessage, "Skanowanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task<int> InternalProcessDirectoryForProfileCreationAsync(string currentDirectoryPath, string modelNameForProfile, List<string> parentCharacterPathParts, HashSet<string> mixedFoldersToIgnore, IProgress<ProgressReport> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); int profilesGeneratedOrUpdatedThisCall = 0; string currentSegmentName = Path.GetFileName(currentDirectoryPath);
            progress.Report(new ProgressReport { OperationName = $"Model '{modelNameForProfile}'", StatusMessage = $"Analiza folderu: {currentSegmentName}", IsIndeterminate = true });
            var currentCharacterPathSegments = new List<string>(parentCharacterPathParts);
            string modelRootForCurrentModel = Path.Combine(LibraryRootPath, modelNameForProfile);
            if (!currentDirectoryPath.Equals(modelRootForCurrentModel, StringComparison.OrdinalIgnoreCase) && !mixedFoldersToIgnore.Contains(currentSegmentName)) currentCharacterPathSegments.Add(currentSegmentName);
            string characterFullName = string.Join(" - ", currentCharacterPathSegments);
            string categoryName = string.IsNullOrWhiteSpace(characterFullName) ? $"{modelNameForProfile} - General" : $"{modelNameForProfile} - {characterFullName}";
            SimpleFileLogger.Log($"InternalProcessDir: Folder '{currentDirectoryPath}'. Kategoria: '{categoryName}'.");
            List<string> imagePathsInThisExactDirectory = new List<string>(); try { imagePathsInThisExactDirectory = Directory.GetFiles(currentDirectoryPath, "*.*", SearchOption.TopDirectoryOnly).Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f))).ToList(); } catch (Exception ex) { SimpleFileLogger.LogWarning($"InternalProcessDir: Błąd odczytu plików z '{currentDirectoryPath}': {ex.Message}"); }
            token.ThrowIfCancellationRequested();

            if (imagePathsInThisExactDirectory.Any())
            {
                progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = imagePathsInThisExactDirectory.Count, StatusMessage = $"Folder '{currentSegmentName}': {imagePathsInThisExactDirectory.Count} obrazów..." });
                var entriesForProfile = new ConcurrentBag<ImageFileEntry>(); var metadataTasks = new List<Task>(); int imagesProcessedForMeta = 0;
                foreach (var path in imagePathsInThisExactDirectory) { metadataTasks.Add(Task.Run(async () => { token.ThrowIfCancellationRequested(); var entry = await _imageMetadataService.ExtractMetadataAsync(path); if (entry != null) entriesForProfile.Add(entry); Interlocked.Increment(ref imagesProcessedForMeta); progress.Report(new ProgressReport { ProcessedItems = imagesProcessedForMeta, TotalItems = imagePathsInThisExactDirectory.Count, StatusMessage = $"Metadane: {Path.GetFileName(path)}..." }); }, token)); }
                await Task.WhenAll(metadataTasks); token.ThrowIfCancellationRequested();
                if (entriesForProfile.Any()) { await _profileService.GenerateProfileAsync(categoryName, entriesForProfile.ToList(), progress, token); profilesGeneratedOrUpdatedThisCall++; SimpleFileLogger.Log($"InternalProcessDir: Profil '{categoryName}' z {entriesForProfile.Count} obrazami z '{currentDirectoryPath}'."); }
            }
            else if (_profileService.GetProfile(categoryName) != null && (categoryName.Equals($"{modelNameForProfile} - General", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(characterFullName))) { if (!mixedFoldersToIgnore.Contains(Path.GetFileName(currentDirectoryPath))) { await _profileService.GenerateProfileAsync(categoryName, new List<ImageFileEntry>(), progress, token); SimpleFileLogger.Log($"InternalProcessDir: Profil '{categoryName}' pusty, wyczyszczony."); profilesGeneratedOrUpdatedThisCall++; } }
            token.ThrowIfCancellationRequested();
            try { var subDirectories = Directory.GetDirectories(currentDirectoryPath); var subDirProcessingTasks = new List<Task<int>>(); foreach (var subDirectoryPath in subDirectories) { token.ThrowIfCancellationRequested(); subDirProcessingTasks.Add(InternalProcessDirectoryForProfileCreationAsync(subDirectoryPath, modelNameForProfile, new List<string>(currentCharacterPathSegments), mixedFoldersToIgnore, progress, token)); } var subDirResults = await Task.WhenAll(subDirProcessingTasks); profilesGeneratedOrUpdatedThisCall += subDirResults.Sum(); } catch (OperationCanceledException) { throw; } catch (Exception ex) { SimpleFileLogger.LogError($"InternalProcessDir: Błąd podfolderów dla '{currentDirectoryPath}'", ex); }
            return profilesGeneratedOrUpdatedThisCall;
        }

        private bool IsImageBetter(ImageFileEntry entry1, ImageFileEntry entry2) { if (entry1 == null || entry2 == null) return false; long r1 = (long)entry1.Width * entry1.Height; long r2 = (long)entry2.Width * entry2.Height; if (r1 > r2) return true; if (r1 < r2) return false; return entry1.FileSize > entry2.FileSize; }

        private async Task<bool> HandleFileMovedOrDeletedUpdateProfilesAsync(string? oldPath, string? newPathIfMoved, string? targetCategoryNameIfMoved, CancellationToken token, IProgress<ProgressReport> progress)
        {
            SimpleFileLogger.Log($"[ProfileUpdate] Aktualizacja. Old='{oldPath}', New='{newPathIfMoved}', TargetCat='{targetCategoryNameIfMoved}'");
            progress.Report(new ProgressReport { OperationName = "Aktualizacja Profili", StatusMessage = "Aktualizacja definicji profili...", IsIndeterminate = true });
            var affectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool changed = false;
            var allProfiles = _profileService.GetAllProfiles().ToList();
            foreach (var p in allProfiles) { token.ThrowIfCancellationRequested(); bool currentNeedsRegen = false; if (!string.IsNullOrWhiteSpace(oldPath) && p.SourceImagePaths?.RemoveAll(pth => pth.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) > 0) { SimpleFileLogger.Log($"[ProfileUpdate] Usunięto '{oldPath}' z '{p.CategoryName}'."); currentNeedsRegen = true; } if (!string.IsNullOrWhiteSpace(newPathIfMoved) && !string.IsNullOrWhiteSpace(targetCategoryNameIfMoved) && p.CategoryName.Equals(targetCategoryNameIfMoved, StringComparison.OrdinalIgnoreCase)) { p.SourceImagePaths ??= new List<string>(); if (!p.SourceImagePaths.Any(pth => pth.Equals(newPathIfMoved, StringComparison.OrdinalIgnoreCase))) { p.SourceImagePaths.Add(newPathIfMoved); SimpleFileLogger.Log($"[ProfileUpdate] Dodano '{newPathIfMoved}' do '{p.CategoryName}'."); currentNeedsRegen = true; } } if (currentNeedsRegen) affectedNames.Add(p.CategoryName); }
            token.ThrowIfCancellationRequested();
            if (affectedNames.Any())
            {
                SimpleFileLogger.Log($"[ProfileUpdate] {affectedNames.Count} profili do regeneracji: {string.Join(", ", affectedNames)}");
                int regeneratedCount = 0; int totalToRegen = affectedNames.Count;
                progress.Report(new ProgressReport { ProcessedItems = regeneratedCount, TotalItems = totalToRegen, StatusMessage = $"Regenerowanie {totalToRegen} profili..." });
                foreach (var name in affectedNames) { token.ThrowIfCancellationRequested(); var affectedProfile = _profileService.GetProfile(name); if (affectedProfile == null) { SimpleFileLogger.LogWarning($"[ProfileUpdate] Profil '{name}' nie znaleziony."); regeneratedCount++; progress.Report(new ProgressReport { ProcessedItems = regeneratedCount, TotalItems = totalToRegen, StatusMessage = $"Nie znaleziono '{name}'." }); continue; } progress.Report(new ProgressReport { ProcessedItems = regeneratedCount, TotalItems = totalToRegen, StatusMessage = $"Regenerowanie '{name}'..." }); var entries = new List<ImageFileEntry>(); if (affectedProfile.SourceImagePaths != null) { foreach (var path in affectedProfile.SourceImagePaths) { token.ThrowIfCancellationRequested(); if (File.Exists(path)) { var entry = await _imageMetadataService.ExtractMetadataAsync(path); if (entry != null) entries.Add(entry); } else SimpleFileLogger.LogWarning($"[ProfileUpdate] Ścieżka '{path}' w '{name}' nie istnieje."); } } SimpleFileLogger.Log($"[ProfileUpdate] Regenerowanie '{name}' z {entries.Count} obrazami."); await _profileService.GenerateProfileAsync(name, entries, progress, token); changed = true; regeneratedCount++; progress.Report(new ProgressReport { ProcessedItems = regeneratedCount, TotalItems = totalToRegen, StatusMessage = $"Zregenerowano '{name}'." }); }
                SimpleFileLogger.Log($"[ProfileUpdate] Zakończono regenerację dla {affectedNames.Count} profili.");
            }
            else SimpleFileLogger.Log($"[ProfileUpdate] Brak profili do regeneracji dla operacji (oldPath: '{oldPath}').");
            return changed;
        }

        private async Task<(Models.ProposedMove? proposedMove, bool wasActionAutoHandled, bool profilesWereModified)> ProcessDuplicateOrSuggestNewAsync(ImageFileEntry sourceImageEntry, CategoryProfile targetProfile, double similarityToCentroid, string modelDirectoryPath, float[] sourceImageEmbedding, IProgress<ProgressReport> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); bool profilesModified = false;
            var (_, charFolder) = ParseCategoryName(targetProfile.CategoryName); string targetCharPath = Path.Combine(modelDirectoryPath, SanitizeFolderName(charFolder)); Directory.CreateDirectory(targetCharPath);
            List<string> filesInTarget; try { filesInTarget = Directory.EnumerateFiles(targetCharPath, "*.*", SearchOption.TopDirectoryOnly).Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f))).ToList(); } catch (Exception ex) { SimpleFileLogger.LogError($"Błąd odczytu plików z '{targetCharPath}'", ex); filesInTarget = new List<string>(); }
            foreach (string existingPath in filesInTarget) { token.ThrowIfCancellationRequested(); if (string.Equals(Path.GetFullPath(existingPath), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase)) continue; var existingEntry = await _imageMetadataService.ExtractMetadataAsync(existingPath); if (existingEntry == null) continue; float[]? existingEmbedding = existingEntry.FeatureVector ?? await _profileService.GetImageEmbeddingAsync(existingEntry, token); if (existingEmbedding == null) { SimpleFileLogger.LogWarning($"[ProcessDupOrSuggest] Brak embeddingu dla istniejącego: {existingEntry.FilePath}"); continue; } double simSourceExisting = Utils.MathUtils.CalculateCosineSimilarity(sourceImageEmbedding, existingEmbedding); if (simSourceExisting >= DUPLICATE_SIMILARITY_THRESHOLD) { bool sourceBetter = IsImageBetter(sourceImageEntry, existingEntry); if (sourceBetter) { SimpleFileLogger.Log($"[AutoReplace] Lepsza: '{sourceImageEntry.FilePath}' -> '{existingEntry.FilePath}'."); try { File.Copy(sourceImageEntry.FilePath, existingEntry.FilePath, true); string oldSrc = sourceImageEntry.FilePath; File.Delete(oldSrc); if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldSrc, null, null, token, progress)) profilesModified = true; } catch (Exception ex) { SimpleFileLogger.LogError($"[AutoReplace] Błąd", ex); } return (null, true, profilesModified); } else { SimpleFileLogger.Log($"[AutoDeleteSource] Istniejąca lepsza/równa. Usuwanie '{sourceImageEntry.FilePath}'."); try { string oldSrc = sourceImageEntry.FilePath; File.Delete(oldSrc); if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldSrc, null, null, token, progress)) profilesModified = true; } catch (Exception ex) { SimpleFileLogger.LogError($"[AutoDeleteSource] Błąd", ex); } return (null, true, profilesModified); } } }
            token.ThrowIfCancellationRequested();
            if (similarityToCentroid >= SuggestionSimilarityThreshold) { string proposedPath = Path.Combine(targetCharPath, sourceImageEntry.FileName); ProposedMoveActionType action; ImageFileEntry? displayTarget = null; if (File.Exists(proposedPath) && !string.Equals(Path.GetFullPath(proposedPath), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase)) { action = ProposedMoveActionType.ConflictKeepBoth; displayTarget = await _imageMetadataService.ExtractMetadataAsync(proposedPath); } else if (string.Equals(Path.GetFullPath(proposedPath), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase)) { SimpleFileLogger.Log($"[Suggest] Plik '{sourceImageEntry.FilePath}' już w celu."); return (null, false, profilesModified); } else action = ProposedMoveActionType.CopyNew; var move = new Models.ProposedMove(sourceImageEntry, displayTarget, proposedPath, similarityToCentroid, targetProfile.CategoryName, action, sourceImageEmbedding); SimpleFileLogger.Log($"[Suggest] Sugestia: {action} dla '{sourceImageEntry.FileName}' do '{targetProfile.CategoryName}', Sim: {similarityToCentroid:F4}"); return (move, false, profilesModified); }
            SimpleFileLogger.Log($"[Suggest] Plik '{sourceImageEntry.FileName}' (Sim: {similarityToCentroid:F4}) nie pasuje ({SuggestionSimilarityThreshold:F2}) do '{targetProfile.CategoryName}'."); return (null, false, profilesModified);
        }

        private async Task ExecuteMatchModelSpecificAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nie wybrano modelki."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            SimpleFileLogger.LogHighLevelInfo($"MatchModelSpecific dla '{modelVM.ModelName}'.");
            var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase); if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe."; MessageBox.Show(StatusMessage, "Brak Folderów", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var movesForSuggestionWindowConcurrent = new ConcurrentBag<Models.ProposedMove>(); string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName); if (!Directory.Exists(modelPath)) { StatusMessage = $"Błąd: Folder modelki '{modelVM.ModelName}' nie istnieje."; MessageBox.Show(StatusMessage, "Błąd Folderu", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            await Application.Current.Dispatcher.InvokeAsync(() => { modelVM.PendingSuggestionsCount = 0; foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; }); token.ThrowIfCancellationRequested();
            List<string> allImagePathsInMix = new List<string>(); foreach (var mixFolderName in mixedFolders) { string currentMixPath = Path.Combine(modelPath, mixFolderName); if (Directory.Exists(currentMixPath)) allImagePathsInMix.AddRange(await _fileScannerService.ScanDirectoryAsync(currentMixPath)); }
            long totalFilesToScanInMix = allImagePathsInMix.Count;
            progress.Report(new ProgressReport { OperationName = $"Match dla '{modelVM.ModelName}'", ProcessedItems = 0, TotalItems = (int)totalFilesToScanInMix, StatusMessage = $"Skanowanie {totalFilesToScanInMix} plików w Mix..." });
            long filesProcessedCount = 0; long filesWithEmbeddingsCount = 0; long autoActionsCount = 0; bool anyProfileDataChanged = false;
            var alreadySuggestedDuplicates = new ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)>(); var processingTasks = new List<Task<ProcessingResult>>();
            foreach (var imgPathFromMix in allImagePathsInMix) { token.ThrowIfCancellationRequested(); processingTasks.Add(Task.Run(async () => { var res = await ProcessSingleImageForModelSpecificScanAsync(imgPathFromMix, modelVM, modelPath, token, movesForSuggestionWindowConcurrent, alreadySuggestedDuplicates, progress); Interlocked.Increment(ref filesProcessedCount); progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref filesProcessedCount), TotalItems = (int)totalFilesToScanInMix, StatusMessage = $"Przetwarzanie {Path.GetFileName(imgPathFromMix)}..." }); return res; }, token)); }
            var taskResults = await Task.WhenAll(processingTasks); token.ThrowIfCancellationRequested();
            foreach (var r in taskResults) { filesWithEmbeddingsCount += r.FilesWithEmbeddingsIncrement; autoActionsCount += r.AutoActionsIncrement; if (r.ProfileDataChanged) anyProfileDataChanged = true; }
            var movesForSuggestionWindow = movesForSuggestionWindowConcurrent.ToList();
            SimpleFileLogger.LogHighLevelInfo($"MatchModelSpecific dla '{modelVM.ModelName}': Przeskanowano: {totalFilesToScanInMix}, Z embeddingami: {filesWithEmbeddingsCount}. Auto: {autoActionsCount}. Sugestie: {movesForSuggestionWindow.Count}. Profile zm.: {anyProfileDataChanged}");
            progress.Report(new ProgressReport { ProcessedItems = (int)totalFilesToScanInMix, TotalItems = (int)totalFilesToScanInMix, StatusMessage = $"Analiza dla '{modelVM.ModelName}' zakończona." });
            _lastModelSpecificSuggestions = new List<Models.ProposedMove>(movesForSuggestionWindow); _lastScannedModelNameForSuggestions = modelVM.ModelName;
            if (movesForSuggestionWindow.Any()) { bool? dialogOutcome = false; List<Models.ProposedMove> approvedMoves = new List<Models.ProposedMove>(); await Application.Current.Dispatcher.InvokeAsync(() => { var vm = new PreviewChangesViewModel(movesForSuggestionWindow, SuggestionSimilarityThreshold); var win = new PreviewChangesWindow { DataContext = vm, Owner = Application.Current.MainWindow }; win.SetViewModelCloseAction(vm); dialogOutcome = win.ShowDialog(); if (dialogOutcome == true) approvedMoves = vm.GetApprovedMoves(); }); token.ThrowIfCancellationRequested(); if (dialogOutcome == true && approvedMoves.Any()) { progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = approvedMoves.Count, StatusMessage = $"Stosowanie {approvedMoves.Count} zmian..." }); if (await InternalHandleApprovedMovesAsync(approvedMoves, modelVM, null, token, progress)) anyProfileDataChanged = true; _lastModelSpecificSuggestions.RemoveAll(s => approvedMoves.Any(ap => ap.SourceImage.FilePath == s.SourceImage.FilePath)); } else if (dialogOutcome == false) StatusMessage = $"Anulowano zmiany dla '{modelVM.ModelName}'."; }
            if (anyProfileDataChanged) { SimpleFileLogger.LogHighLevelInfo($"ExecuteMatchModelSpecificAsync: Zmiany w profilach dla '{modelVM.ModelName}'. Odświeżanie."); _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token, progress); _isRefreshingProfilesPostMove = false; }
            RefreshPendingSuggestionCountsFromCache(); StatusMessage = $"Dla '{modelVM.ModelName}': {autoActionsCount} auto, {modelVM.PendingSuggestionsCount} sugestii.";
            if (!movesForSuggestionWindow.Any() && autoActionsCount > 0 && !anyProfileDataChanged) MessageBox.Show($"Zakończono auto operacje dla '{modelVM.ModelName}'. {autoActionsCount} akcji. Brak sugestii.", "Operacje Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (!movesForSuggestionWindow.Any() && autoActionsCount == 0 && !anyProfileDataChanged && totalFilesToScanInMix > 0) MessageBox.Show($"Brak nowych sugestii/akcji dla '{modelVM.ModelName}'.", "Brak Zmian", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (totalFilesToScanInMix == 0) MessageBox.Show($"Nie znaleziono obrazów w Mix dla '{modelVM.ModelName}'.", "Brak Plików", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task<ProcessingResult> ProcessSingleImageForModelSpecificScanAsync(string imgPathFromMix, ModelDisplayViewModel modelVM, string modelPath, CancellationToken token, ConcurrentBag<Models.ProposedMove> movesForSuggestionWindowConcurrent, ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)> alreadySuggestedGraphicDuplicatesConcurrent, IProgress<ProgressReport> progress)
        {
            var result = new ProcessingResult(); if (token.IsCancellationRequested || !File.Exists(imgPathFromMix)) return result;
            // StatusMessage będzie aktualizowany przez pętlę w ExecuteMatchModelSpecificAsync
            var sourceEntry = await _imageMetadataService.ExtractMetadataAsync(imgPathFromMix); if (sourceEntry == null) { SimpleFileLogger.LogWarning($"MatchModelSpecific(AsyncItem): Błąd metadanych dla: {imgPathFromMix}."); return result; }
            await _embeddingSemaphore.WaitAsync(token); float[]? sourceEmbedding = null; try { if (token.IsCancellationRequested) return result; sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceEntry, token); } finally { _embeddingSemaphore.Release(); }
            if (sourceEmbedding == null) { SimpleFileLogger.LogWarning($"MatchModelSpecific(AsyncItem): Błąd embeddingu dla: {sourceEntry.FilePath}."); return result; }
            result.FilesWithEmbeddingsIncrement = 1; if (token.IsCancellationRequested) return result;
            var bestSuggestion = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName);
            if (bestSuggestion != null) { var (proposedMove, wasAuto, profilesMod) = await ProcessDuplicateOrSuggestNewAsync(sourceEntry, bestSuggestion.Item1, bestSuggestion.Item2, modelPath, sourceEmbedding, progress, token); if (profilesMod) result.ProfileDataChanged = true; if (wasAuto) result.AutoActionsIncrement = 1; else if (proposedMove != null) movesForSuggestionWindowConcurrent.Add(proposedMove); }
            else SimpleFileLogger.Log($"MatchModelSpecific(AsyncItem): Brak sugestii dla '{sourceEntry.FilePath}' (model: {modelVM.ModelName}).");
            return result;
        }

        private async Task ExecuteSuggestImagesAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            ClearModelSpecificSuggestionsCache(); token.ThrowIfCancellationRequested();
            var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase); if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery Mix."; MessageBox.Show(StatusMessage, "Brak Folderów", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var allCollectedSuggestionsGlobalConcurrent = new ConcurrentBag<Models.ProposedMove>(); var alreadySuggestedDuplicates = new ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)>();
            await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var mVM_ui in HierarchicalProfilesList) { mVM_ui.PendingSuggestionsCount = 0; foreach (var cp_ui in mVM_ui.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; } }); token.ThrowIfCancellationRequested();
            var allModelsCurrentlyInList = HierarchicalProfilesList.ToList(); List<string> allImagePathsToScanGlobal = new List<string>();
            foreach (var modelVM in allModelsCurrentlyInList) { string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName); if (!Directory.Exists(modelPath) || !modelVM.HasCharacterProfiles) continue; foreach (var mixFolderName in mixedFolders) { string currentMixPath = Path.Combine(modelPath, mixFolderName); if (Directory.Exists(currentMixPath)) allImagePathsToScanGlobal.AddRange(await _fileScannerService.ScanDirectoryAsync(currentMixPath)); } }
            allImagePathsToScanGlobal = allImagePathsToScanGlobal.Distinct().ToList();
            long totalFilesToProcess = allImagePathsToScanGlobal.Count;
            progress.Report(new ProgressReport { OperationName = "Globalne Sugestie", ProcessedItems = 0, TotalItems = (int)totalFilesToProcess, StatusMessage = $"Skanowanie {totalFilesToProcess} plików..." });
            long filesProcessedCount = 0; long filesWithEmbeddingsCount = 0; long autoActionsCount = 0; bool anyProfileDataChanged = false; var processingTasks = new List<Task<ProcessingResult>>();
            foreach (var imgPathFromMix in allImagePathsToScanGlobal) { token.ThrowIfCancellationRequested(); string? modelNameForThisMixFile = GetModelNameFromFilePathInLibrary(imgPathFromMix, mixedFolders); if (string.IsNullOrEmpty(modelNameForThisMixFile)) { Interlocked.Increment(ref filesProcessedCount); progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref filesProcessedCount), TotalItems = (int)totalFilesToProcess, StatusMessage = $"Pomijanie {Path.GetFileName(imgPathFromMix)} (nieznany model)..." }); continue; } var modelVM = allModelsCurrentlyInList.FirstOrDefault(m => m.ModelName.Equals(modelNameForThisMixFile, StringComparison.OrdinalIgnoreCase)); if (modelVM == null || !modelVM.HasCharacterProfiles) { Interlocked.Increment(ref filesProcessedCount); progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref filesProcessedCount), TotalItems = (int)totalFilesToProcess, StatusMessage = $"Pomijanie {Path.GetFileName(imgPathFromMix)} (brak VM dla {modelNameForThisMixFile})..." }); continue; } string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName); processingTasks.Add(Task.Run(async () => { var res = await ProcessSingleImageForGlobalSuggestionsAsync(imgPathFromMix, modelVM, modelPath, token, allCollectedSuggestionsGlobalConcurrent, alreadySuggestedDuplicates, progress); Interlocked.Increment(ref filesProcessedCount); progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref filesProcessedCount), TotalItems = (int)totalFilesToProcess, StatusMessage = $"Skan globalny: {Path.GetFileName(imgPathFromMix)}..." }); return res; }, token)); }
            var taskResults = await Task.WhenAll(processingTasks); token.ThrowIfCancellationRequested();
            foreach (var r in taskResults) { filesWithEmbeddingsCount += r.FilesWithEmbeddingsIncrement; autoActionsCount += r.AutoActionsIncrement; if (r.ProfileDataChanged) anyProfileDataChanged = true; }
            var allCollectedSuggestionsGlobal = allCollectedSuggestionsGlobalConcurrent.ToList();
            SimpleFileLogger.LogHighLevelInfo($"ExecuteSuggestImagesAsync: Globalnie - Przeskanowano: {totalFilesToProcess}, Z embeddingami: {filesWithEmbeddingsCount}, Auto: {autoActionsCount}, Sugestie: {allCollectedSuggestionsGlobal.Count}. Profile zm.: {anyProfileDataChanged}");
            progress.Report(new ProgressReport { ProcessedItems = (int)totalFilesToProcess, TotalItems = (int)totalFilesToProcess, StatusMessage = "Globalne wyszukiwanie zakończone." });
            _lastModelSpecificSuggestions = new List<Models.ProposedMove>(allCollectedSuggestionsGlobal); _lastScannedModelNameForSuggestions = null;
            StatusMessage = $"Globalne wyszukiwanie: {autoActionsCount} auto, {allCollectedSuggestionsGlobal.Count} sugestii.";
            if (anyProfileDataChanged) { SimpleFileLogger.LogHighLevelInfo($"ExecuteSuggestImagesAsync: Zmiany w profilach. Odświeżanie."); _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token, progress); _isRefreshingProfilesPostMove = false; }
            RefreshPendingSuggestionCountsFromCache(); string completionMessage = StatusMessage; if (allCollectedSuggestionsGlobal.Any()) completionMessage += " Użyj menu kontekstowego do przejrzenia."; else if (autoActionsCount == 0 && totalFilesToProcess > 0) completionMessage = "Globalne wyszukiwanie nie znalazło nic nowego."; else if (totalFilesToProcess == 0) completionMessage = "Nie znaleziono plików w Mix do skanowania globalnego."; MessageBox.Show(completionMessage, "Globalne Wyszukiwanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private string? GetModelNameFromFilePathInLibrary(string filePath, HashSet<string> mixedFolderNames) { if (string.IsNullOrWhiteSpace(filePath) || !filePath.StartsWith(LibraryRootPath, StringComparison.OrdinalIgnoreCase)) return null; string relativePath = filePath.Substring(LibraryRootPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); var pathParts = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries); if (pathParts.Length > 0) return pathParts[0]; return null; }

        private async Task<ProcessingResult> ProcessSingleImageForGlobalSuggestionsAsync(string imgPathFromMix, ModelDisplayViewModel modelVM, string modelPath, CancellationToken token, ConcurrentBag<Models.ProposedMove> allCollectedSuggestionsGlobalConcurrent, ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)> alreadySuggestedGraphicDuplicatesGlobalConcurrent, IProgress<ProgressReport> progress)
        {
            var result = new ProcessingResult(); if (token.IsCancellationRequested) return result;
            // StatusMessage będzie aktualizowany przez pętlę w ExecuteSuggestImagesAsync
            if (!File.Exists(imgPathFromMix)) { SimpleFileLogger.Log($"Pojedynczy dla Global: Plik {imgPathFromMix} nie istnieje."); return result; }
            var sourceEntry = await _imageMetadataService.ExtractMetadataAsync(imgPathFromMix); if (sourceEntry == null) return result;
            float[]? sourceEmbedding = null; await _embeddingSemaphore.WaitAsync(token); try { if (token.IsCancellationRequested) return result; sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceEntry, token); } catch (Exception ex) { SimpleFileLogger.LogError($"Błąd embeddingu dla {sourceEntry.FilePath} w PojedynczyDlaGlobal", ex); } finally { _embeddingSemaphore.Release(); }
            if (sourceEmbedding == null) return result; result.FilesWithEmbeddingsIncrement = 1; if (token.IsCancellationRequested) return result;
            var bestSuggestion = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName); // Nadal w kontekście modelVM, dla folderów Mix wewnątrz tej modelki
            if (bestSuggestion != null) { var (proposedMove, wasAuto, profilesMod) = await ProcessDuplicateOrSuggestNewAsync(sourceEntry, bestSuggestion.Item1, bestSuggestion.Item2, modelPath, sourceEmbedding, progress, token); if (profilesMod) result.ProfileDataChanged = true; if (wasAuto) result.AutoActionsIncrement = 1; else if (proposedMove != null) allCollectedSuggestionsGlobalConcurrent.Add(proposedMove); }
            return result;
        }

        private async Task ExecuteCheckCharacterSuggestionsAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            var charProfileForSuggestions = (parameter as CategoryProfile) ?? SelectedProfile; if (charProfileForSuggestions == null) { StatusMessage = "Błąd: Wybierz profil postaci."; MessageBox.Show(StatusMessage, "Brak Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            SimpleFileLogger.LogHighLevelInfo($"CheckCharacterSuggestions dla '{charProfileForSuggestions.CategoryName}'.");
            progress.Report(new ProgressReport { OperationName = $"Sugestie dla '{charProfileForSuggestions.CategoryName}'", StatusMessage = $"Sprawdzanie...", IsIndeterminate = true }); token.ThrowIfCancellationRequested();
            string modelName = _profileService.GetModelNameFromCategory(charProfileForSuggestions.CategoryName); var modelVM = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));
            var movesForThisCharacterWindow = new List<Models.ProposedMove>(); bool anyProfileDataChanged = false;
            if (_lastModelSpecificSuggestions.Any()) { if (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) || _lastScannedModelNameForSuggestions.Equals(modelName, StringComparison.OrdinalIgnoreCase)) { movesForThisCharacterWindow = _lastModelSpecificSuggestions.Where(m => m.TargetCategoryProfileName.Equals(charProfileForSuggestions.CategoryName, StringComparison.OrdinalIgnoreCase) && m.Similarity >= SuggestionSimilarityThreshold).ToList(); SimpleFileLogger.Log($"CheckCharacterSuggestions: Użyto cache ({_lastModelSpecificSuggestions.Count}). Dla '{charProfileForSuggestions.CategoryName}' znaleziono {movesForThisCharacterWindow.Count}."); } }
            progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = movesForThisCharacterWindow.Count, StatusMessage = $"Znaleziono {movesForThisCharacterWindow.Count} sugestii." });
            if (!movesForThisCharacterWindow.Any()) { StatusMessage = $"Brak sugestii dla '{charProfileForSuggestions.CategoryName}'."; MessageBox.Show(StatusMessage, "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Information); var uiProfile = modelVM?.CharacterProfiles.FirstOrDefault(cp => cp.CategoryName == charProfileForSuggestions.CategoryName); if (uiProfile != null) uiProfile.PendingSuggestionsCount = 0; progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Brak sugestii." }); return; }
            token.ThrowIfCancellationRequested();
            if (movesForThisCharacterWindow.Any()) { bool? outcome = false; List<Models.ProposedMove> approved = new List<Models.ProposedMove>(); await Application.Current.Dispatcher.InvokeAsync(() => { var vm = new PreviewChangesViewModel(movesForThisCharacterWindow, SuggestionSimilarityThreshold); var win = new PreviewChangesWindow { DataContext = vm, Owner = Application.Current.MainWindow }; win.SetViewModelCloseAction(vm); outcome = win.ShowDialog(); if (outcome == true) approved = vm.GetApprovedMoves(); }); token.ThrowIfCancellationRequested(); if (outcome == true && approved.Any()) { progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = approved.Count, StatusMessage = $"Stosowanie {approved.Count} zmian..." }); if (await InternalHandleApprovedMovesAsync(approved, modelVM, charProfileForSuggestions, token, progress)) anyProfileDataChanged = true; _lastModelSpecificSuggestions.RemoveAll(s => approved.Any(ap => ap.SourceImage.FilePath == s.SourceImage.FilePath)); } else if (outcome == false) StatusMessage = $"Anulowano sugestie dla '{charProfileForSuggestions.CategoryName}'."; }
            if (anyProfileDataChanged) { SimpleFileLogger.LogHighLevelInfo($"CheckCharacterSuggestionsAsync: Zmiany w profilach dla '{charProfileForSuggestions.CategoryName}'. Odświeżanie."); _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token, progress); _isRefreshingProfilesPostMove = false; }
            RefreshPendingSuggestionCountsFromCache(); progress.Report(new ProgressReport { ProcessedItems = movesForThisCharacterWindow.Count, TotalItems = movesForThisCharacterWindow.Count, StatusMessage = "Sprawdzanie sugestii zakończone." });
        }

        private void RefreshPendingSuggestionCountsFromCache() { Application.Current.Dispatcher.Invoke(() => { foreach (var mVM_iter in HierarchicalProfilesList) { mVM_iter.PendingSuggestionsCount = 0; foreach (var cp_iter in mVM_iter.CharacterProfiles) cp_iter.PendingSuggestionsCount = 0; } if (_lastModelSpecificSuggestions.Any()) { var relevantSuggestions = _lastModelSpecificSuggestions.Where(sugg => sugg.Similarity >= SuggestionSimilarityThreshold).ToList(); if (!string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) && _lastScannedModelNameForSuggestions != "__CACHE_CLEARED__") { var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(_lastScannedModelNameForSuggestions, StringComparison.OrdinalIgnoreCase)); if (modelToUpdate != null) { int totalForModel = 0; foreach (var cp_ui in modelToUpdate.CharacterProfiles) { cp_ui.PendingSuggestionsCount = relevantSuggestions.Count(sugg => sugg.TargetCategoryProfileName.Equals(cp_ui.CategoryName, StringComparison.OrdinalIgnoreCase)); totalForModel += cp_ui.PendingSuggestionsCount; } modelToUpdate.PendingSuggestionsCount = totalForModel; SimpleFileLogger.Log($"RefreshPendingCounts (Specific '{_lastScannedModelNameForSuggestions}'): Total: {totalForModel}."); } } else if (_lastScannedModelNameForSuggestions != "__CACHE_CLEARED__") { SimpleFileLogger.Log($"RefreshPendingCounts (Global): {relevantSuggestions.Count} sugestii."); var suggestionsByModel = relevantSuggestions.GroupBy(sugg => _profileService.GetModelNameFromCategory(sugg.TargetCategoryProfileName)); foreach (var group in suggestionsByModel) { var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(group.Key, StringComparison.OrdinalIgnoreCase)); if (modelToUpdate != null) { int totalForModel = 0; foreach (var cp_ui in modelToUpdate.CharacterProfiles) { cp_ui.PendingSuggestionsCount = group.Count(sugg => sugg.TargetCategoryProfileName.Equals(cp_ui.CategoryName, StringComparison.OrdinalIgnoreCase)); totalForModel += cp_ui.PendingSuggestionsCount; } modelToUpdate.PendingSuggestionsCount = totalForModel; SimpleFileLogger.Log($"RefreshPendingCounts (Global): Model '{modelToUpdate.ModelName}' updated: {totalForModel}."); } } } else SimpleFileLogger.Log("RefreshPendingCounts: Cache był czyszczony."); } else SimpleFileLogger.Log("RefreshPendingCounts: Brak sugestii w cache."); CommandManager.InvalidateRequerySuggested(); }); }

        private async Task<bool> InternalHandleApprovedMovesAsync(List<Models.ProposedMove> approvedMoves, ModelDisplayViewModel? specificModelVM, CategoryProfile? specificCharacterProfile, CancellationToken token, IProgress<ProgressReport> progress)
        {
            int successfulMoves = 0, copyErrors = 0, deleteErrors = 0, skippedQuality = 0, skippedOther = 0; bool anyProfileActuallyModified = false; var processedSourcePathsForThisBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalMovesToProcess = approvedMoves.Count;
            progress.Report(new ProgressReport { OperationName = "Stosowanie Zmian", ProcessedItems = 0, TotalItems = totalMovesToProcess, StatusMessage = $"Stosowanie {totalMovesToProcess} zmian..." });
            for (int i = 0; i < approvedMoves.Count; i++) { var move = approvedMoves[i]; token.ThrowIfCancellationRequested(); progress.Report(new ProgressReport { ProcessedItems = i + 1, TotalItems = totalMovesToProcess, StatusMessage = $"Przenoszenie: {Path.GetFileName(move.SourceImage.FilePath)} ({move.Action})..." }); string sourcePath = move.SourceImage.FilePath; string targetPath = move.ProposedTargetPath; string originalTarget = move.ProposedTargetPath; var actionType = move.Action; bool opSuccess = false; bool delSource = false; try { if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) { SimpleFileLogger.LogWarning($"[HandleApproved] Źródło nie istnieje: '{sourcePath}'."); skippedOther++; continue; } string targetDir = Path.GetDirectoryName(targetPath); if (string.IsNullOrEmpty(targetDir)) { SimpleFileLogger.LogWarning($"[HandleApproved] Błędny folder docelowy: '{targetPath}'."); skippedOther++; continue; } Directory.CreateDirectory(targetDir); switch (actionType) { case ProposedMoveActionType.CopyNew: if (File.Exists(targetPath) && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), SC.OrdinalIgnoreCase)) targetPath = GenerateUniqueTargetPath(targetDir, Path.GetFileName(sourcePath), "_new_approved"); else if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), SC.OrdinalIgnoreCase)) { delSource = true; opSuccess = true; break; } await Task.Run(() => File.Copy(sourcePath, targetPath, false), token); opSuccess = true; delSource = true; break; case ProposedMoveActionType.OverwriteExisting: if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), SC.OrdinalIgnoreCase)) { delSource = true; opSuccess = true; break; } await Task.Run(() => File.Copy(sourcePath, targetPath, true), token); opSuccess = true; delSource = true; break; case ProposedMoveActionType.KeepExistingDeleteSource: if (!File.Exists(targetPath) && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), SC.OrdinalIgnoreCase)) { skippedOther++; opSuccess = false; delSource = false; break; } if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), SC.OrdinalIgnoreCase)) { delSource = true; opSuccess = true; } else { opSuccess = true; skippedQuality++; } break; case ProposedMoveActionType.ConflictKeepBoth: string newConflictPath = GenerateUniqueTargetPath(targetDir, Path.GetFileName(sourcePath), "_conflict_approved"); await Task.Run(() => File.Copy(sourcePath, newConflictPath, false), token); targetPath = newConflictPath; opSuccess = true; delSource = true; break; default: skippedOther++; opSuccess = false; break; } token.ThrowIfCancellationRequested(); if (opSuccess) { successfulMoves++; processedSourcePathsForThisBatch.Add(sourcePath); string? oldP = null; string? newP = targetPath; if (delSource && File.Exists(sourcePath) && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), SC.OrdinalIgnoreCase)) { oldP = sourcePath; try { await Task.Run(() => File.Delete(sourcePath), token); } catch (Exception exDel) { deleteErrors++; SimpleFileLogger.LogError($"[HandleApproved] Błąd usuwania '{sourcePath}'.", exDel); } } else if (delSource && string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), SC.OrdinalIgnoreCase)) { oldP = sourcePath; newP = targetPath; } if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldP, newP, move.TargetCategoryProfileName, token, progress)) anyProfileActuallyModified = true; } } catch (OperationCanceledException) { throw; } catch (Exception exCopy) { copyErrors++; SimpleFileLogger.LogError($"[HandleApproved] Błąd '{sourcePath}' -> '{originalTarget}'. Akcja: {actionType}.", exCopy); } }
            token.ThrowIfCancellationRequested(); if (processedSourcePathsForThisBatch.Any()) { int removed = _lastModelSpecificSuggestions.RemoveAll(s => processedSourcePathsForThisBatch.Contains(s.SourceImage.FilePath)); SimpleFileLogger.Log($"[HandleApproved] Usunięto {removed} sugestii z cache."); }
            progress.Report(new ProgressReport { ProcessedItems = totalMovesToProcess, TotalItems = totalMovesToProcess, StatusMessage = $"Zakończono stosowanie zmian." });
            StatusMessage = $"Zakończono: {successfulMoves} ok, {skippedQuality} pom.(jakość), {skippedOther} pom.(inne), {copyErrors} bł.kop., {deleteErrors} bł.us."; if (successfulMoves > 0 || copyErrors > 0 || deleteErrors > 0 || skippedOther > 0) MessageBox.Show(StatusMessage, "Operacja Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
            return anyProfileActuallyModified;
        }

        private string GenerateUniqueTargetPath(string targetDirectory, string originalFileNameWithExtension, string suffixIfConflict = "_conflict") { string baseName = Path.GetFileNameWithoutExtension(originalFileNameWithExtension); string extension = Path.GetExtension(originalFileNameWithExtension); string finalPath = Path.Combine(targetDirectory, originalFileNameWithExtension); int counter = 1; while (File.Exists(finalPath)) { string newFileName = $"{baseName}{suffixIfConflict}{counter}{extension}"; finalPath = Path.Combine(targetDirectory, newFileName); counter++; if (counter > 9999) { newFileName = $"{baseName}_{Guid.NewGuid():N}{extension}"; finalPath = Path.Combine(targetDirectory, newFileName); SimpleFileLogger.LogWarning($"GenerateUniqueTargetPath: GUID po konfliktach: {finalPath}"); break; } } return finalPath; }

        private async Task ExecuteRemoveModelTreeAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            bool changed = false; if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Wybierz modelkę."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            progress.Report(new ProgressReport { OperationName = $"Usuwanie Modelki '{modelVM.ModelName}'", StatusMessage = $"Przygotowywanie...", IsIndeterminate = true }); token.ThrowIfCancellationRequested();
            if (MessageBox.Show($"Usunąć modelkę '{modelVM.ModelName}' i jej profile? (NIE usunie plików graficznych)", "Potwierdź", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            { progress.Report(new ProgressReport { StatusMessage = $"Usuwanie '{modelVM.ModelName}'..." }); if (await _profileService.RemoveAllProfilesForModelAsync(modelVM.ModelName, token)) { StatusMessage = $"Modelka '{modelVM.ModelName}' usunięta."; if (_lastScannedModelNameForSuggestions == modelVM.ModelName) ClearModelSpecificSuggestionsCache(); if (SelectedProfile != null && _profileService.GetModelNameFromCategory(SelectedProfile.CategoryName) == modelVM.ModelName) SelectedProfile = null; changed = true; } else { StatusMessage = $"Nie udało się usunąć '{modelVM.ModelName}'."; changed = true; } }
            if (changed) { _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token, progress); _isRefreshingProfilesPostMove = false; }
            progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Usuwanie modelki zakończone." });
        }

        private async Task ExecuteAnalyzeModelForSplittingAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Wybierz modelkę."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            token.ThrowIfCancellationRequested(); int marked = 0; await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.HasSplitSuggestion = false; });
            var profiles = modelVM.CharacterProfiles.ToList(); progress.Report(new ProgressReport { OperationName = $"Analiza Podziału '{modelVM.ModelName}'", ProcessedItems = 0, TotalItems = profiles.Count, StatusMessage = $"Analiza..." }); int processed = 0;
            foreach (var cp in profiles) { token.ThrowIfCancellationRequested(); const int minConsider = 10, minSuggest = 20; if (cp.SourceImagePaths == null || cp.SourceImagePaths.Count < minConsider) { processed++; progress.Report(new ProgressReport { ProcessedItems = processed, TotalItems = profiles.Count, StatusMessage = $"'{cp.CategoryName}' (pominięty)..." }); continue; } int validCount = 0; if (cp.SourceImagePaths != null) foreach (var p in cp.SourceImagePaths) if (File.Exists(p)) validCount++; token.ThrowIfCancellationRequested(); if (validCount < minConsider) { processed++; progress.Report(new ProgressReport { ProcessedItems = processed, TotalItems = profiles.Count, StatusMessage = $"'{cp.CategoryName}' (pominięty)..." }); continue; } bool suggest = validCount >= minSuggest; var uiCp = modelVM.CharacterProfiles.FirstOrDefault(p => p.CategoryName == cp.CategoryName); if (uiCp != null) { uiCp.HasSplitSuggestion = suggest; if (suggest) marked++; } SimpleFileLogger.Log($"AnalyzeModelForSplitting: Profil '{cp.CategoryName}', {validCount} obr., sugestia: {suggest}."); processed++; progress.Report(new ProgressReport { ProcessedItems = processed, TotalItems = profiles.Count, StatusMessage = $"'{cp.CategoryName}' (sugestia: {suggest})..." }); }
            token.ThrowIfCancellationRequested(); StatusMessage = $"Analiza podziału dla '{modelVM.ModelName}': {marked} kandydatów."; (marked > 0 ? (Action<string, string, MessageBoxButton, MessageBoxImage>)MessageBox.Show : (s1, s2, s3, s4) => MessageBox.Show(s1, s2, s3, s4))(StatusMessage, "Analiza Zakończona", MessageBoxButton.OK, marked > 0 ? MessageBoxImage.Information : MessageBoxImage.Information);
            progress.Report(new ProgressReport { ProcessedItems = profiles.Count, TotalItems = profiles.Count, StatusMessage = "Analiza podziału zakończona." });
        }

        private async Task ExecuteOpenSplitProfileDialogAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            if (!(parameter is CategoryProfile originalCP)) { StatusMessage = "Błąd: Wybierz profil."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            token.ThrowIfCancellationRequested(); bool changed = false;
            progress.Report(new ProgressReport { OperationName = $"Podział Profilu '{originalCP.CategoryName}'", StatusMessage = $"Przygotowywanie danych...", IsIndeterminate = true });
            var imagesInProfile = new List<ImageFileEntry>(); if (originalCP.SourceImagePaths != null) { int total = originalCP.SourceImagePaths.Count; int loaded = 0; progress.Report(new ProgressReport { ProcessedItems = loaded, TotalItems = total, StatusMessage = $"Ładowanie {total} obrazów..." }); foreach (var path in originalCP.SourceImagePaths) { token.ThrowIfCancellationRequested(); if (File.Exists(path)) { var entry = await _imageMetadataService.ExtractMetadataAsync(path); if (entry != null) imagesInProfile.Add(entry); } loaded++; progress.Report(new ProgressReport { ProcessedItems = loaded, TotalItems = total, StatusMessage = $"Załadowano {loaded}/{total}..." }); } }
            token.ThrowIfCancellationRequested(); if (!imagesInProfile.Any()) { StatusMessage = $"Profil '{originalCP.CategoryName}' pusty."; MessageBox.Show(StatusMessage, "Pusty Profil", MessageBoxButton.OK, MessageBoxImage.Warning); var uiP = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == originalCP.CategoryName); if (uiP != null) uiP.HasSplitSuggestion = false; progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Profil pusty." }); return; }
            var g1 = imagesInProfile.Take(imagesInProfile.Count / 2).ToList(); var g2 = imagesInProfile.Skip(imagesInProfile.Count / 2).ToList(); string modelName = _profileService.GetModelNameFromCategory(originalCP.CategoryName); string baseCharName = _profileService.GetCharacterNameFromCategory(originalCP.CategoryName); if (baseCharName.Equals("General", SC.OrdinalIgnoreCase) && (originalCP.CategoryName.Equals($"{modelName} - General", SC.OrdinalIgnoreCase) || originalCP.CategoryName.Equals(modelName, SC.OrdinalIgnoreCase))) baseCharName = modelName; string sName1 = $"{baseCharName} - Part 1"; string sName2 = $"{baseCharName} - Part 2"; bool? dialogResult = false; SplitProfileViewModel? splitVM = null;
            progress.Report(new ProgressReport { StatusMessage = "Oczekiwanie na decyzję (podział)...", IsIndeterminate = true });
            await Application.Current.Dispatcher.InvokeAsync(() => { splitVM = new SplitProfileViewModel(originalCP, g1, g2, sName1, sName2); var win = new SplitProfileWindow { DataContext = splitVM, Owner = Application.Current.MainWindow }; win.SetViewModelCloseAction(splitVM); dialogResult = win.ShowDialog(); }); token.ThrowIfCancellationRequested();
            if (dialogResult == true && splitVM != null)
            {
                StatusMessage = $"Dzielenie '{originalCP.CategoryName}'..."; SimpleFileLogger.LogHighLevelInfo($"SplitProfile: Potwierdzono dla '{originalCP.CategoryName}'. Nowe: '{splitVM.NewProfile1Name}', '{splitVM.NewProfile2Name}'."); string fullN1 = $"{modelName} - {splitVM.NewProfile1Name}"; string fullN2 = $"{modelName} - {splitVM.NewProfile2Name}"; var e1 = splitVM.Group1Images.Select(vmI => vmI.OriginalImageEntry).ToList(); var e2 = splitVM.Group2Images.Select(vmI => vmI.OriginalImageEntry).ToList(); string p1Path = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(splitVM.NewProfile1Name)); string p2Path = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(splitVM.NewProfile2Name)); Directory.CreateDirectory(p1Path); Directory.CreateDirectory(p2Path);
                int totalFilesToMove = e1.Count + e2.Count; int filesMoved = 0; progress.Report(new ProgressReport { ProcessedItems = filesMoved, TotalItems = totalFilesToMove, StatusMessage = $"Przenoszenie plików..." });
                foreach (var entry in e1) { token.ThrowIfCancellationRequested(); string newFP = Path.Combine(p1Path, entry.FileName); try { if (!File.Exists(newFP) && File.Exists(entry.FilePath)) File.Move(entry.FilePath, newFP); if (File.Exists(newFP)) entry.FilePath = newFP; } catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd przenoszenia '{entry.FilePath}' do '{newFP}'", ex); } filesMoved++; progress.Report(new ProgressReport { ProcessedItems = filesMoved, TotalItems = totalFilesToMove, StatusMessage = $"Przeniesiono {filesMoved}/{totalFilesToMove}..." }); }
                foreach (var entry in e2) { token.ThrowIfCancellationRequested(); string newFP = Path.Combine(p2Path, entry.FileName); try { if (!File.Exists(newFP) && File.Exists(entry.FilePath)) File.Move(entry.FilePath, newFP); if (File.Exists(newFP)) entry.FilePath = newFP; } catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd przenoszenia '{entry.FilePath}' do '{newFP}'", ex); } filesMoved++; progress.Report(new ProgressReport { ProcessedItems = filesMoved, TotalItems = totalFilesToMove, StatusMessage = $"Przeniesiono {filesMoved}/{totalFilesToMove}..." }); }
                SimpleFileLogger.Log($"SplitProfile: Przeniesiono pliki."); token.ThrowIfCancellationRequested();
                progress.Report(new ProgressReport { StatusMessage = $"Generowanie profilu '{fullN1}'...", IsIndeterminate = true }); await _profileService.GenerateProfileAsync(fullN1, e1, progress, token); token.ThrowIfCancellationRequested();
                progress.Report(new ProgressReport { StatusMessage = $"Generowanie profilu '{fullN2}'...", IsIndeterminate = true }); await _profileService.GenerateProfileAsync(fullN2, e2, progress, token); token.ThrowIfCancellationRequested();
                await _profileService.RemoveProfileAsync(originalCP.CategoryName, token); changed = true; StatusMessage = $"Profil '{originalCP.CategoryName}' podzielony."; var uiP = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == originalCP.CategoryName); if (uiP != null) uiP.HasSplitSuggestion = false;
            }
            else StatusMessage = $"Podział '{originalCP.CategoryName}' anulowany.";
            if (changed) { _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token, progress); _isRefreshingProfilesPostMove = false; }
            progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Podział profilu zakończony." });
        }

        private void ExecuteCancelCurrentOperation(object? parameter) { SimpleFileLogger.LogHighLevelInfo($"ExecuteCancelCurrentOperation. CTS: {_activeLongOperationCts != null}. Token: {_activeLongOperationCts?.Token.GetHashCode()}"); if (_activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested) { _activeLongOperationCts.Cancel(); StatusMessage = "Anulowanie..."; ProgressStatusText = "Anulowanie..."; SimpleFileLogger.LogHighLevelInfo("Sygnał anulowania wysłany."); } else SimpleFileLogger.Log("Brak operacji do anulowania lub już anulowano."); }

        private async Task ExecuteEnsureThumbnailsLoadedAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            var imagesToLoadThumbs = (parameter as IEnumerable<ImageFileEntry>)?.ToList() ?? ImageFiles.ToList(); if (!imagesToLoadThumbs.Any()) { StatusMessage = "Brak obrazów do ładowania miniaturek."; progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Brak obrazów." }); return; }
            SimpleFileLogger.LogHighLevelInfo($"EnsureThumbnailsLoaded: Dla {imagesToLoadThumbs.Count} obrazów.");
            progress.Report(new ProgressReport { OperationName = "Ładowanie Miniaturek", ProcessedItems = 0, TotalItems = imagesToLoadThumbs.Count, StatusMessage = $"Rozpoczynanie ładowania {imagesToLoadThumbs.Count} miniaturek..." });
            var tasks = new List<Task>(); using var thumbnailSemaphore = new SemaphoreSlim(10, 10); int thumbsLoadedCount = 0;
            foreach (var entry in imagesToLoadThumbs) { token.ThrowIfCancellationRequested(); if (entry.Thumbnail == null && !entry.IsLoadingThumbnail) { tasks.Add(Task.Run(async () => { await thumbnailSemaphore.WaitAsync(token); try { if (token.IsCancellationRequested) return; await entry.LoadThumbnailAsync(); Interlocked.Increment(ref thumbsLoadedCount); progress.Report(new ProgressReport { ProcessedItems = Interlocked.Read(ref thumbsLoadedCount), TotalItems = imagesToLoadThumbs.Count, StatusMessage = $"Miniaturki: {Path.GetFileName(entry.FilePath)} ({Interlocked.Read(ref thumbsLoadedCount)}/{imagesToLoadThumbs.Count})" }); } finally { thumbnailSemaphore.Release(); } }, token)); } else { Interlocked.Increment(ref thumbsLoadedCount); progress.Report(new ProgressReport { ProcessedItems = Interlocked.Read(ref thumbsLoadedCount), TotalItems = imagesToLoadThumbs.Count, StatusMessage = $"Miniaturka {Path.GetFileName(entry.FilePath)} już jest." }); } }
            await Task.WhenAll(tasks); token.ThrowIfCancellationRequested();
            int finalLoadedCount = imagesToLoadThumbs.Count(img => img.Thumbnail != null); StatusMessage = $"Załadowano {finalLoadedCount} z {imagesToLoadThumbs.Count} miniaturek.";
            progress.Report(new ProgressReport { ProcessedItems = finalLoadedCount, TotalItems = imagesToLoadThumbs.Count, StatusMessage = StatusMessage }); SimpleFileLogger.LogHighLevelInfo(StatusMessage);
        }

        private async Task ExecuteRemoveDuplicatesInModelAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nieprawidłowy parametr."; return; }
            string modelName = modelVM.ModelName;
            progress.Report(new ProgressReport { OperationName = $"Usuwanie Duplikatów '{modelName}'", StatusMessage = $"Przygotowywanie...", IsIndeterminate = true });
            if (MessageBox.Show($"Usunąć duplikaty dla '{modelName}' (pozostawiając najlepszą jakość)?\nSpowoduje to usunięcie plików z dysku.", "Potwierdź", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { StatusMessage = "Anulowano."; progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Anulowano." }); return; }
            SimpleFileLogger.LogHighLevelInfo($"[RemoveDuplicatesInModel] Rozpoczęto dla: {modelName}."); long totalDuplicatesRemoved = 0; bool changed = false; var profilesSnapshot = modelVM.CharacterProfiles.ToList(); int totalProfiles = profilesSnapshot.Count; int profilesProcessed = 0;
            progress.Report(new ProgressReport { ProcessedItems = profilesProcessed, TotalItems = totalProfiles, StatusMessage = $"Skanowanie profili '{modelName}'..." });
            foreach (var cp in profilesSnapshot) { token.ThrowIfCancellationRequested(); profilesProcessed++; progress.Report(new ProgressReport { ProcessedItems = profilesProcessed, TotalItems = totalProfiles, StatusMessage = $"Profil: {cp.CategoryName} ({profilesProcessed}/{totalProfiles})..." }); if (cp.SourceImagePaths == null || cp.SourceImagePaths.Count < 2) continue; var entriesInProfile = new ConcurrentBag<ImageFileEntry>(); bool hadMissing = false; int totalImagesInCP = cp.SourceImagePaths.Count; int imagesProcessedInCP = 0; var entryLoadingTasks = cp.SourceImagePaths.Select(imgPath => Task.Run(async () => { token.ThrowIfCancellationRequested(); if (!File.Exists(imgPath)) { SimpleFileLogger.LogWarning($"[RDIM] Plik '{imgPath}' z '{cp.CategoryName}' nie istnieje."); lock (_profileChangeLock) hadMissing = true; Interlocked.Increment(ref imagesProcessedInCP); return; } var entry = await _imageMetadataService.ExtractMetadataAsync(imgPath); if (entry != null) { await _embeddingSemaphore.WaitAsync(token); try { if (token.IsCancellationRequested) return; entry.FeatureVector = await _profileService.GetImageEmbeddingAsync(entry, token); } finally { _embeddingSemaphore.Release(); } if (entry.FeatureVector != null) entriesInProfile.Add(entry); else SimpleFileLogger.LogWarning($"[RDIM] Brak embeddingu dla '{entry.FilePath}'."); } Interlocked.Increment(ref imagesProcessedInCP); /* Progress for sub-task images can be too verbose */ }, token)).ToList(); await Task.WhenAll(entryLoadingTasks); token.ThrowIfCancellationRequested(); var validEntries = entriesInProfile.ToList(); if (validEntries.Count < 2) { if (hadMissing) { await _profileService.GenerateProfileAsync(cp.CategoryName, validEntries, progress, token); lock (_profileChangeLock) changed = true; } continue; } var filesToRemove = new HashSet<string>(SC.OrdinalIgnoreCase); var processedForDups = new HashSet<string>(SC.OrdinalIgnoreCase); for (int i = 0; i < validEntries.Count; i++) { token.ThrowIfCancellationRequested(); var currentImg = validEntries[i]; if (filesToRemove.Contains(currentImg.FilePath) || processedForDups.Contains(currentImg.FilePath)) continue; var dupGroup = new List<ImageFileEntry> { currentImg }; for (int j = i + 1; j < validEntries.Count; j++) { token.ThrowIfCancellationRequested(); var otherImg = validEntries[j]; if (filesToRemove.Contains(otherImg.FilePath) || processedForDups.Contains(otherImg.FilePath)) continue; if (currentImg.FeatureVector != null && otherImg.FeatureVector != null) { double sim = Utils.MathUtils.CalculateCosineSimilarity(currentImg.FeatureVector, otherImg.FeatureVector); if (sim >= DUPLICATE_SIMILARITY_THRESHOLD) dupGroup.Add(otherImg); } } if (dupGroup.Count > 1) { ImageFileEntry best = dupGroup.First(); foreach (var imgInGroup in dupGroup.Skip(1)) if (IsImageBetter(imgInGroup, best)) best = imgInGroup; foreach (var imgInGroup in dupGroup) { if (!imgInGroup.FilePath.Equals(best.FilePath, SC.OrdinalIgnoreCase)) { filesToRemove.Add(imgInGroup.FilePath); } processedForDups.Add(imgInGroup.FilePath); } } else processedForDups.Add(currentImg.FilePath); } token.ThrowIfCancellationRequested(); if (filesToRemove.Any()) { long removedThisProf = 0; foreach (var pathToRemove in filesToRemove) { token.ThrowIfCancellationRequested(); try { if (File.Exists(pathToRemove)) { File.Delete(pathToRemove); Interlocked.Increment(ref totalDuplicatesRemoved); removedThisProf++; } } catch (Exception ex) { SimpleFileLogger.LogError($"[RDIM] Błąd usuwania '{pathToRemove}'.", ex); } } var kept = validEntries.Where(e => !filesToRemove.Contains(e.FilePath)).ToList(); await _profileService.GenerateProfileAsync(cp.CategoryName, kept, progress, token); lock (_profileChangeLock) changed = true; SimpleFileLogger.LogHighLevelInfo($"[RDIM] Profil '{cp.CategoryName}' zaktualizowany. Usunięto {removedThisProf}."); } else if (hadMissing) { var validCurrentEntries = validEntries.Where(e => File.Exists(e.FilePath)).ToList(); await _profileService.GenerateProfileAsync(cp.CategoryName, validCurrentEntries, progress, token); lock (_profileChangeLock) changed = true; } }
            token.ThrowIfCancellationRequested();
            progress.Report(new ProgressReport { ProcessedItems = totalProfiles, TotalItems = totalProfiles, StatusMessage = $"Usuwanie duplikatów dla '{modelName}' zakończone." });
            if (Interlocked.Read(ref totalDuplicatesRemoved) > 0 || changed) { StatusMessage = $"Zakończono dla '{modelName}'. Usunięto: {Interlocked.Read(ref totalDuplicatesRemoved)}. Odświeżanie..."; _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token, progress); _isRefreshingProfilesPostMove = false; MessageBox.Show($"Usunięto {Interlocked.Read(ref totalDuplicatesRemoved)} duplikatów dla '{modelName}'.", "Zakończono", MessageBoxButton.OK, MessageBoxImage.Information); } else { StatusMessage = $"Brak duplikatów dla '{modelName}'."; MessageBox.Show(StatusMessage, "Zakończono", MessageBoxButton.OK, MessageBoxImage.Information); }
        }

        private async Task ExecuteApplyAllMatchesForModelAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nieprawidłowy parametr."; MessageBox.Show(StatusMessage, "Błąd", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            string modelName = modelVM.ModelName;
            progress.Report(new ProgressReport { OperationName = $"Stosowanie Dopasowań '{modelName}'", StatusMessage = $"Sprawdzanie sugestii...", IsIndeterminate = true });
            bool hasRelevant = (_lastScannedModelNameForSuggestions == modelVM.ModelName || string.IsNullOrEmpty(_lastScannedModelNameForSuggestions)) && _lastModelSpecificSuggestions.Any(m => _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelName && m.Similarity >= SuggestionSimilarityThreshold);
            if (!hasRelevant) { StatusMessage = $"Brak sugestii dla '{modelName}'."; MessageBox.Show(StatusMessage, "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Exclamation); progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Brak sugestii." }); return; }
            var movesToApply = _lastModelSpecificSuggestions.Where(m => m.Similarity >= SuggestionSimilarityThreshold && _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelName).ToList();
            if (!movesToApply.Any()) { StatusMessage = $"Brak sugestii (próg {SuggestionSimilarityThreshold:F2}) dla '{modelName}'."; MessageBox.Show(StatusMessage, "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Exclamation); progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Brak sugestii (próg)." }); return; }
            if (MessageBox.Show($"Zastosować {movesToApply.Count} dopasowań dla '{modelName}'?", "Potwierdź", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { StatusMessage = "Anulowano."; progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Anulowano." }); return; }
            SimpleFileLogger.LogHighLevelInfo($"[ApplyAllMatchesForModel] Rozpoczęto dla: {modelName}. Ruchy: {movesToApply.Count}.");
            bool changed = await InternalHandleApprovedMovesAsync(new List<Models.ProposedMove>(movesToApply), modelVM, null, token, progress); token.ThrowIfCancellationRequested();
            if (changed) { _isRefreshingProfilesPostMove = true; await InternalExecuteLoadProfilesAsync(token, progress); _isRefreshingProfilesPostMove = false; }
            RefreshPendingSuggestionCountsFromCache();
            MessageBox.Show($"Zastosowano {movesToApply.Count} dopasowań dla '{modelName}'.", "Zakończono", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Skrót dla StringComparison, aby linie nie były za długie
        private const StringComparison SC = StringComparison.OrdinalIgnoreCase;

        private class ImageFileEntryPathComparer : IEqualityComparer<ImageFileEntry> { public bool Equals(ImageFileEntry? x, ImageFileEntry? y) { if (ReferenceEquals(x, y)) return true; if (x is null || y is null) return false; return x.FilePath.Equals(y.FilePath, SC); } public int GetHashCode(ImageFileEntry obj) { return obj.FilePath?.GetHashCode(SC) ?? 0; } }
    }
}