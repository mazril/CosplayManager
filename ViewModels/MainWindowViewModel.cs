// Plik: ViewModels/MainWindowViewModel.cs
using CosplayManager.Models;
using CosplayManager.Services;
using CosplayManager.ViewModels.Base;
using CosplayManager.Views; // Potrzebne dla ManageProfileSuggestionsWindow
using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics; // Dla Stopwatch
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

// Jeśli używasz Ookii Dialogs
using Ookii.Dialogs.Wpf;

namespace CosplayManager.ViewModels
{
    public class MainWindowViewModel : ObservableObject
    {
        private readonly ProfileService _profileService;
        private readonly FileScannerService _fileScannerService;
        private readonly ImageMetadataService _imageMetadataService;
        private readonly SettingsService _settingsService;
        // Prawdopodobnie masz też ClipServiceHttpClient i AIService
        // private readonly ClipServiceHttpClient _clipService;
        // private readonly AIService _aiService;


        private List<Models.ProposedMove> _lastModelSpecificSuggestions = new List<Models.ProposedMove>();
        private string? _lastScannedModelNameForSuggestions;
        private bool _isRefreshingProfilesPostMove = false;

        private const double DUPLICATE_SIMILARITY_THRESHOLD = 0.98;
        private CancellationTokenSource? _activeLongOperationCts;

        private const int MAX_CONCURRENT_EMBEDDING_REQUESTS = 4; // Używane przez _embeddingSemaphore
        private readonly SemaphoreSlim _embeddingSemaphore = new SemaphoreSlim(MAX_CONCURRENT_EMBEDDING_REQUESTS, MAX_CONCURRENT_EMBEDDING_REQUESTS);
        private readonly object _profileChangeLock = new object(); // Do synchronizacji zmian w profilach

        private class ProcessingResult // Klasa pomocnicza do zwracania wyników z zadań asynchronicznych
        {
            public int FilesWithEmbeddingsIncrement { get; set; } = 0;
            public long AutoActionsIncrement { get; set; } = 0; // Zmienione na long
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
                    SimpleFileLogger.IsDebugLoggingEnabled = value; // Ustawienie flagi w loggerze
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
                    CommandManager.InvalidateRequerySuggested(); // Odśwież CanExecute komend
                }
                // Jeśli profil został odznaczony i nie istnieje już na liście (np. po usunięciu)
                if (_selectedProfile == null && oldSelectedProfileName != null &&
                    !_profileService.GetAllProfiles().Any(p => p.CategoryName == oldSelectedProfileName))
                {
                    UpdateEditFieldsFromSelectedProfile(); // Wyczyść pola edycji
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
                    CommandManager.InvalidateRequerySuggested(); // Odśwież CanExecute dla GenerateProfileCommand
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
                    // Można dodać logikę, jeśli potrzebna przy zmianie kolekcji
                    CommandManager.InvalidateRequerySuggested(); // Odśwież CanExecute dla komend zależnych od ImageFiles
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
                    ClearModelSpecificSuggestionsCache(); // Wyczyść cache, jeśli ścieżka się zmienia
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
                    // Jeśli próg się zmienia, odśwież liczniki sugestii
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
                    CommandManager.InvalidateRequerySuggested(); // Ważne, aby (od)blokować przyciski
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
        private long _itemsProcessedForSpeedReport = 0; // Używane do obliczania prędkości
        private DateTime _lastSpeedReportTime = DateTime.MinValue;

        // Deklaracje komend
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
        public ICommand CheckCharacterSuggestionsCommand { get; } // Otwiera okno PreviewChanges dla konkretnego profilu (może być przestarzałe)
        public ICommand RemoveModelTreeCommand { get; } // Usuwa całą modelkę
        public ICommand AnalyzeModelForSplittingCommand { get; } // Analizuje profile pod kątem podziału
        public ICommand OpenSplitProfileDialogCommand { get; } // Otwiera okno podziału profilu
        public ICommand CancelCurrentOperationCommand { get; }
        public ICommand EnsureThumbnailsLoadedCommand { get; }
        public ICommand RemoveDuplicatesInModelCommand { get; } // Usuwa duplikaty dla całej modelki
        public ICommand ApplyAllMatchesForModelCommand { get; } // Automatycznie stosuje wszystkie sugestie dla modelki

        // *** NOWA KOMENDA ***
        public ICommand OpenManageProfileSuggestionsCommand { get; }


        // Konstruktor
        public MainWindowViewModel(
            ProfileService profileService,
            FileScannerService fileScannerService,
            ImageMetadataService imageMetadataService,
            SettingsService settingsService
            // AIService aiService, // Jeśli używasz
            // ClipServiceHttpClient clipService // Jeśli używasz
            )
        {
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _fileScannerService = fileScannerService ?? throw new ArgumentNullException(nameof(fileScannerService));
            _imageMetadataService = imageMetadataService ?? throw new ArgumentNullException(nameof(imageMetadataService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            // _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
            // _clipService = clipService ?? throw new ArgumentNullException(nameof(clipService));

            HierarchicalProfilesList = new ObservableCollection<ModelDisplayViewModel>();
            ImageFiles = new ObservableCollection<ImageFileEntry>();

            // Inicjalizacja komend asynchronicznych (z RunLongOperation)
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
            SaveAppSettingsCommand = new AsyncRelayCommand(param => RunLongOperation(
                 (token, progress) => ExecuteSaveAppSettingsAsync(token), "Zapisywanie ustawień aplikacji"), CanExecuteSaveAppSettings);

            // Inicjalizacja komend synchronicznych (RelayCommand)
            AddFilesToProfileCommand = new RelayCommand(ExecuteAddFilesToProfile, CanExecuteAddFilesToProfile);
            ClearFilesFromProfileCommand = new RelayCommand(ExecuteClearFilesFromProfile, CanExecuteClearFilesFromProfile);
            CreateNewProfileSetupCommand = new RelayCommand(ExecuteCreateNewProfileSetup, CanExecuteCreateNewProfileSetup);
            SelectLibraryPathCommand = new RelayCommand(ExecuteSelectLibraryPath, CanExecuteSelectLibraryPath);
            CancelCurrentOperationCommand = new RelayCommand(ExecuteCancelCurrentOperation, CanExecuteCancelCurrentOperation);

            // *** NOWA KOMENDA - INICJALIZACJA ***
            OpenManageProfileSuggestionsCommand = new RelayCommand(ExecuteOpenManageProfileSuggestions, CanExecuteOpenManageProfileSuggestions);
        }

        // Metoda do raportowania postępu (już istnieje w Twoim pliku)
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
                else if (report.IsIndeterminate || report.TotalItems == 0) // Stan nieokreślony lub brak itemów
                {
                    CurrentProgress = 0;
                    MaximumProgress = 100; // Domyślne dla stanu nieokreślonego
                    IsProgressIndeterminate = true;
                }
                else // ProcessedItems > TotalItems (np. tylko przetwarzanie, bez ustalonego max)
                {
                    CurrentProgress = report.ProcessedItems;
                    MaximumProgress = Math.Max(report.ProcessedItems, report.TotalItems); // Aby pasek nie był "pełny" przedwcześnie
                    IsProgressIndeterminate = false;
                }

                ProgressStatusText = report.StatusMessage ?? string.Empty;

                // Obliczanie i wyświetlanie prędkości przetwarzania
                if (_operationStopwatch.IsRunning)
                {
                    var elapsedSeconds = _operationStopwatch.Elapsed.TotalSeconds;
                    // Używajmy long dla currentReportedProcessedItems, jeśli ProcessedItems może być duże
                    long currentReportedProcessedItems = report.IsIndeterminate ? _itemsProcessedForSpeedReport + 1 : (long)report.ProcessedItems;


                    if (elapsedSeconds > 0.2 && currentReportedProcessedItems > _itemsProcessedForSpeedReport) // Minimalny czas i zmiana itemów do raportu
                    {
                        double itemsSinceLastReport = currentReportedProcessedItems - _itemsProcessedForSpeedReport;
                        double timeSinceLastReport = (DateTime.UtcNow - _lastSpeedReportTime).TotalSeconds;

                        if (timeSinceLastReport > 0.1 && itemsSinceLastReport > 0) // Minimalny interwał i zmiana itemów
                        {
                            double speed = itemsSinceLastReport / timeSinceLastReport;
                            ProcessingSpeedText = $"Prędkość: {speed:F1} jedn./s";
                            _itemsProcessedForSpeedReport = currentReportedProcessedItems;
                            _lastSpeedReportTime = DateTime.UtcNow;
                        }
                        else if (elapsedSeconds > 1.0 && !report.IsIndeterminate && report.TotalItems > 0) // Fallback na średnią prędkość
                        {
                            double overallSpeed = report.ProcessedItems / elapsedSeconds;
                            ProcessingSpeedText = $"Prędkość: {overallSpeed:F1} jedn./s (średnia)";
                        }
                    }
                    else if (report.IsIndeterminate && elapsedSeconds > 1.0) // Dla operacji nieokreślonych, pokaż czas
                    {
                        ProcessingSpeedText = $"Czas: {elapsedSeconds:F1}s";
                    }
                }
            });
        }

        // Główna metoda do uruchamiania długich operacji (już istnieje w Twoim pliku)
        public async Task RunLongOperation(Func<CancellationToken, IProgress<ProgressReport>, Task> operationWithProgress, string statusMessagePrefix)
        {
            CancellationTokenSource? previousCts = _activeLongOperationCts;
            _activeLongOperationCts = new CancellationTokenSource();
            var token = _activeLongOperationCts.Token;

            // Anuluj poprzednią operację, jeśli istnieje
            if (previousCts != null)
            {
                SimpleFileLogger.Log($"RunLongOperation: Anulowanie poprzedniej operacji (token: {previousCts.Token.GetHashCode()}). Nowy token: {token.GetHashCode()}");
                previousCts.Cancel();
                previousCts.Dispose(); // Zwolnij zasoby poprzedniego CTS
            }
            else
            {
                SimpleFileLogger.Log($"RunLongOperation: Brak poprzedniej operacji. Nowy token: {token.GetHashCode()}");
            }

            IsBusy = true;
            StatusMessage = $"{statusMessagePrefix}... (Można anulować)";
            ProgressStatusText = $"{statusMessagePrefix}...";
            IsProgressIndeterminate = true; // Domyślnie nieokreślony, dopóki operacja nie zaraportuje inaczej
            CurrentProgress = 0;
            MaximumProgress = 100; // Domyślne max
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
                    ProgressStatusText = "Zakończono."; // Upewnij się, że status postępu jest aktualizowany
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

                // Ustaw pasek postępu na 0% lub 100% po zakończeniu/anulowaniu
                if (token.IsCancellationRequested)
                {
                    CurrentProgress = 0;
                    MaximumProgress = 100; // lub 0, w zależności od preferencji
                    IsProgressIndeterminate = false;
                }
                else if (MaximumProgress > 0) // Jeśli operacja ustawiła MaximumProgress
                {
                    CurrentProgress = MaximumProgress; // Pokaż 100%
                    IsProgressIndeterminate = false;
                }
                else // Jeśli operacja nie ustawiła MaximumProgress (np. była tylko IsIndeterminate)
                {
                    CurrentProgress = 0; // Reset
                    MaximumProgress = 100;
                    IsProgressIndeterminate = false;
                }


                // Bezpieczne usunięcie CTS
                if (_activeLongOperationCts != null && _activeLongOperationCts.Token == token)
                {
                    _activeLongOperationCts.Dispose();
                    _activeLongOperationCts = null;
                    SimpleFileLogger.Log($"RunLongOperation: CTS dla tokenu {token.GetHashCode()} został usunięty.");
                }
                else if (_activeLongOperationCts != null)
                {
                    // To nie powinno się zdarzyć, jeśli logika jest poprawna
                    SimpleFileLogger.Log($"RunLongOperation: CTS dla tokenu {token.GetHashCode()} NIE został usunięty, aktywny CTS ma token {_activeLongOperationCts.Token.GetHashCode()}.");
                }
                else
                {
                    SimpleFileLogger.Log($"RunLongOperation: CTS dla tokenu {token.GetHashCode()} NIE został usunięty, _activeLongOperationCts jest null.");
                }

                // Upewnij się, że komunikaty statusu są spójne po zakończeniu
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

                ProcessingSpeedText = string.Empty; // Wyczyść prędkość po operacji
                SimpleFileLogger.LogHighLevelInfo($"RunLongOperation: Zakończono (finally) dla '{statusMessagePrefix}' (token: {token.GetHashCode()}). Aktualny StatusMessage: {StatusMessage}, ProgressStatusText: {ProgressStatusText}");

            }
        }

        // Przeciążenie RunLongOperation dla operacji bez szczegółowego raportowania postępu
        public Task RunLongOperation(Func<CancellationToken, Task> operation, string statusMessagePrefix)
        {
            return RunLongOperation(async (token, progress) =>
            {
                // Zaraportuj początkowy stan jako nieokreślony
                progress.Report(new ProgressReport { OperationName = statusMessagePrefix, StatusMessage = statusMessagePrefix + "...", IsIndeterminate = true });
                await operation(token);
                // Zaraportuj zakończenie
                progress.Report(new ProgressReport { OperationName = statusMessagePrefix, StatusMessage = statusMessagePrefix + " - Zakończono.", ProcessedItems = 1, TotalItems = 1 });
            }, statusMessagePrefix);
        }


        private void UpdateCurrentProfileNameForEdit()
        {
            if (!string.IsNullOrWhiteSpace(ModelNameInput) && !string.IsNullOrWhiteSpace(CharacterNameInput))
            {
                CurrentProfileNameForEdit = $"{ModelNameInput} - {CharacterNameInput}";
            }
            else if (!string.IsNullOrWhiteSpace(ModelNameInput))
            {
                CurrentProfileNameForEdit = $"{ModelNameInput} - General"; // Domyślna nazwa postaci
            }
            else
            {
                CurrentProfileNameForEdit = string.Empty;
            }
        }

        private (string model, string character) ParseCategoryName(string? categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName))
            {
                return ("UnknownModel", "UnknownCharacter"); // Domyślne wartości, jeśli nazwa jest pusta
            }

            var parts = categoryName.Split(new[] { " - " }, 2, StringSplitOptions.None);
            string model = parts.Length > 0 ? parts[0].Trim() : categoryName.Trim();
            string character = parts.Length > 1 ? parts[1].Trim() : "General"; // Domyślnie "General", jeśli brak drugiej części

            if (string.IsNullOrWhiteSpace(model)) model = "UnknownModel"; // Zabezpieczenie przed pustym modelem
            if (string.IsNullOrWhiteSpace(character)) character = "General"; // Zabezpieczenie przed pustą postacią

            return (model, character);
        }


        private string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "_"; // Zwróć podkreślenie dla pustej nazwy

            string invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            string sanitizedName = name;

            foreach (char invalidChar in invalidChars.Distinct()) // Usuń duplikaty z listy nieprawidłowych znaków
            {
                sanitizedName = sanitizedName.Replace(invalidChar.ToString(), "_");
            }
            // Dodatkowe czyszczenie dla znaków, które mogą być problematyczne, a nie są w GetInvalidPathChars
            sanitizedName = sanitizedName.Replace(":", "_").Replace("?", "_").Replace("*", "_").Replace("\"", "_")
                                       .Replace("<", "_").Replace(">", "_").Replace("|", "_")
                                       .Replace("/", "_").Replace("\\", "_"); // Upewnij się, że separatory też są zamieniane

            sanitizedName = sanitizedName.Trim().TrimStart('.').TrimEnd('.'); // Usuń kropki z początku/końca i białe znaki

            if (string.IsNullOrWhiteSpace(sanitizedName)) return "_"; // Jeśli po czyszczeniu nic nie zostało

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
                        if (File.Exists(path)) // Sprawdź, czy plik nadal istnieje
                        {
                            newImageFiles.Add(new ImageFileEntry { FilePath = path, FileName = Path.GetFileName(path) });
                        }
                        else
                        {
                            SimpleFileLogger.LogWarning($"OSTRZEŻENIE (UpdateEditFields): Ścieżka '{path}' dla profilu '{_selectedProfile.CategoryName}' nie istnieje.");
                        }
                    }
                }
                ImageFiles = newImageFiles; // Przypisz nową kolekcję
            }
            else // Jeśli żaden profil nie jest wybrany
            {
                CurrentProfileNameForEdit = string.Empty;
                ModelNameInput = string.Empty;
                CharacterNameInput = string.Empty;
                ImageFiles = new ObservableCollection<ImageFileEntry>(); // Wyczyść listę plików
            }
        }

        private void ClearModelSpecificSuggestionsCache()
        {
            SimpleFileLogger.LogHighLevelInfo("ClearModelSpecificSuggestionsCache: Czyszczenie cache sugestii.");
            _lastModelSpecificSuggestions.Clear();
            _lastScannedModelNameForSuggestions = "__CACHE_CLEARED__"; // Specjalny marker
            RefreshPendingSuggestionCountsFromCache(); // Odśwież UI
        }

        private UserSettings GetCurrentSettings()
        {
            return new UserSettings
            {
                LibraryRootPath = this.LibraryRootPath,
                SourceFolderNamesInput = this.SourceFolderNamesInput,
                SuggestionSimilarityThreshold = this.SuggestionSimilarityThreshold,
                EnableDebugLogging = this.EnableDebugLogging
                // Dodaj inne ustawienia, jeśli są
            };
        }

        public void ApplySettings(UserSettings settings)
        {
            if (settings == null)
            {
                SimpleFileLogger.LogWarning("ApplySettings: Otrzymano null jako ustawienia. Inicjalizacja wartości domyślnych dla VM.");
                // Ustaw domyślne wartości, jeśli settings są null
                LibraryRootPath = string.Empty;
                SourceFolderNamesInput = "Mix,Mieszane,Unsorted,Downloaded"; // Domyślne foldery
                SuggestionSimilarityThreshold = 0.85; // Domyślny próg
                EnableDebugLogging = false; // Domyślnie wyłączone
                SimpleFileLogger.IsDebugLoggingEnabled = false;
            }
            else
            {
                LibraryRootPath = settings.LibraryRootPath;
                SourceFolderNamesInput = settings.SourceFolderNamesInput;
                SuggestionSimilarityThreshold = settings.SuggestionSimilarityThreshold;
                EnableDebugLogging = settings.EnableDebugLogging;
                SimpleFileLogger.IsDebugLoggingEnabled = this.EnableDebugLogging; // Synchronizuj logger
            }
            SimpleFileLogger.LogHighLevelInfo($"Zastosowano ustawienia w ViewModel. Debug logging: {(EnableDebugLogging ? "Enabled" : "Disabled")}.");
        }


        private async Task ExecuteSaveAppSettingsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); // Sprawdź anulowanie
            await _settingsService.SaveSettingsAsync(GetCurrentSettings());
            // StatusMessage = "Ustawienia aplikacji zapisane."; // Opcjonalny komunikat
            SimpleFileLogger.LogHighLevelInfo("Ustawienia aplikacji zapisane (na żądanie).");
        }

        public async Task InitializeAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            SimpleFileLogger.LogHighLevelInfo("ViewModel: InitializeAsync start.");
            progress.Report(new ProgressReport { OperationName = "Inicjalizacja", StatusMessage = "Wczytywanie ustawień...", IsIndeterminate = true });
            ApplySettings(await _settingsService.LoadSettingsAsync());
            token.ThrowIfCancellationRequested(); // Sprawdź anulowanie po wczytaniu ustawień

            // Wczytaj profile po zastosowaniu ustawień (np. ścieżki biblioteki)
            await InternalExecuteLoadProfilesAsync(token, progress); // Przekaż token i progress
            token.ThrowIfCancellationRequested();

            // Ustaw status po inicjalizacji
            if (string.IsNullOrEmpty(LibraryRootPath))
            {
                StatusMessage = "Gotowy. Wybierz folder biblioteki.";
            }
            else if (!Directory.Exists(LibraryRootPath))
            {
                StatusMessage = $"Uwaga: Folder biblioteki '{LibraryRootPath}' nie istnieje.";
            }
            else
            {
                StatusMessage = "Gotowy.";
            }
            SimpleFileLogger.LogHighLevelInfo("ViewModel: InitializeAsync koniec.");
        }

        public async Task OnAppClosingAsync()
        {
            SimpleFileLogger.LogHighLevelInfo("ViewModel: OnAppClosingAsync - Anulowanie operacji i zapis ustawień...");
            // Anuluj aktywne długie operacje
            if (_activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested)
            {
                _activeLongOperationCts.Cancel();
                // Daj krótki czas na zakończenie anulowania, ale nie blokuj zamknięcia na długo
                try { using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await Task.WhenAny(Task.Delay(Timeout.Infinite, _activeLongOperationCts.Token), Task.Delay(Timeout.Infinite, timeoutCts.Token)); }
                catch (OperationCanceledException) { /* Oczekiwane */ }
                catch (Exception ex) { SimpleFileLogger.LogWarning($"OnAppClosingAsync: Wyjątek podczas oczekiwania na anulowanie: {ex.Message}"); }
                _activeLongOperationCts?.Dispose();
                _activeLongOperationCts = null;
            }
            // Zapisz ustawienia
            await _settingsService.SaveSettingsAsync(GetCurrentSettings());
            SimpleFileLogger.LogHighLevelInfo("ViewModel: OnAppClosingAsync - Ustawienia zapisane.");
        }

        // *** METODY POMOCNICZE DLA NOWEGO OKNA ***
        public List<Models.ProposedMove> GetLastModelSpecificSuggestions() => _lastModelSpecificSuggestions?.ToList() ?? new List<Models.ProposedMove>();

        public async Task RefreshProfilesAfterChangeAsync()
        {
            _isRefreshingProfilesPostMove = true; // Ustaw flagę
            // Użyj istniejącej metody RunLongOperation do opakowania odświeżania profili
            await RunLongOperation(
                (token, progress) => InternalExecuteLoadProfilesAsync(token, progress),
                "Odświeżanie profili po zmianach"
            );
            _isRefreshingProfilesPostMove = false; // Zresetuj flagę
            RefreshPendingSuggestionCountsFromCache(); // Odśwież także liczniki sugestii
        }

        // NOWA METODA do czyszczenia obsłużonych sugestii
        public void ClearHandledSuggestionsForProfile(List<string> handledSourceImagePaths, string targetProfileName)
        {
            if (handledSourceImagePaths == null || !handledSourceImagePaths.Any() || string.IsNullOrEmpty(targetProfileName))
            {
                SimpleFileLogger.Log($"ClearHandledSuggestionsForProfile: Nieprawidłowe argumenty. Ścieżki: {handledSourceImagePaths?.Count ?? 0}, Profil: '{targetProfileName}'.");
                // Mimo to odświeżamy, bo mogło coś innego wpłynąć na stan
                RefreshPendingSuggestionCountsFromCache();
                return;
            }

            int removedCount = 0;
            lock (_lastModelSpecificSuggestions) // Synchronizacja dostępu do listy
            {
                removedCount = _lastModelSpecificSuggestions.RemoveAll(sugg =>
                    sugg.TargetCategoryProfileName.Equals(targetProfileName, StringComparison.OrdinalIgnoreCase) &&
                    handledSourceImagePaths.Contains(sugg.SourceImage.FilePath, StringComparer.OrdinalIgnoreCase)
                );
            }

            if (removedCount > 0)
            {
                SimpleFileLogger.Log($"MainWindowViewModel.ClearHandledSuggestionsForProfile: Usunięto {removedCount} obsłużonych sugestii dla profilu '{targetProfileName}' z pamięci podręcznej.");
            }
            else
            {
                SimpleFileLogger.Log($"MainWindowViewModel.ClearHandledSuggestionsForProfile: Nie usunięto żadnych sugestii dla profilu '{targetProfileName}' (mogły nie istnieć lub nie pasowały ścieżki).");
            }
            RefreshPendingSuggestionCountsFromCache(); // Odśwież liczniki natychmiast
        }


        // *** NOWA KOMENDA - METODY ***
        private bool CanExecuteOpenManageProfileSuggestions(object? parameter)
        {
            var profile = (parameter as CategoryProfile) ?? SelectedProfile;
            if (IsBusy || profile == null) return false;

            string modelName = _profileService.GetModelNameFromCategory(profile.CategoryName);
            // Sprawdź, czy istnieją sugestie w cache dla tego konkretnego profilu modelki
            bool hasCachedSuggestionsForModelProfile = !string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) &&
                                               _lastScannedModelNameForSuggestions.Equals(modelName, StringComparison.OrdinalIgnoreCase) &&
                                               _lastModelSpecificSuggestions.Any(s => s.TargetCategoryProfileName.Equals(profile.CategoryName, StringComparison.OrdinalIgnoreCase));

            // Sprawdź, czy istnieją globalne sugestie (gdy _lastScannedModelNameForSuggestions jest null) dla tego profilu
            bool hasGlobalSuggestionsForModelProfile = string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) &&
                                                _lastScannedModelNameForSuggestions != "__CACHE_CLEARED__" && // Upewnij się, że cache nie był explicite wyczyszczony
                                                _lastModelSpecificSuggestions.Any(s => s.TargetCategoryProfileName.Equals(profile.CategoryName, StringComparison.OrdinalIgnoreCase));

            bool profileHasImages = profile.SourceImagePaths?.Any() ?? false;

            // Okno można otworzyć, jeśli profil ma jakiekolwiek obrazy LUB są dla niego jakiekolwiek sugestie (specyficzne dla modelu lub globalne)
            return (profileHasImages || hasCachedSuggestionsForModelProfile || hasGlobalSuggestionsForModelProfile);
        }


        private void ExecuteOpenManageProfileSuggestions(object? parameter)
        {
            var characterProfile = (parameter as CategoryProfile) ?? SelectedProfile;
            if (characterProfile == null)
            {
                MessageBox.Show("Nie wybrano profilu postaci.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string modelName = _profileService.GetModelNameFromCategory(characterProfile.CategoryName);
            var modelVm = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));

            if (modelVm == null) // Powinno się zdarzyć tylko w nietypowych sytuacjach
            {
                MessageBox.Show($"Nie znaleziono ViewModelu dla modelki '{modelName}'.", "Błąd wewnętrzny", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // Przekazujemy 'this' (MainWindowViewModel) do nowego ViewModelu
                var manageVM = new ManageProfileSuggestionsViewModel(characterProfile, modelVm, this, _profileService, _fileScannerService, _imageMetadataService);
                var window = new ManageProfileSuggestionsWindow
                {
                    DataContext = manageVM,
                    Owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive) ?? Application.Current.MainWindow
                };
                window.SetViewModelCloseAction(manageVM); // Ustawienie akcji zamknięcia
                window.ShowDialog(); // Otwórz jako modalne
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Błąd podczas otwierania okna ManageProfileSuggestionsWindow: {ex.Message}", ex);
                MessageBox.Show($"Wystąpił błąd podczas otwierania okna zarządzania sugestiami: {ex.Message}", "Błąd krytyczny", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        // Metody CanExecute dla komend (już istnieją w Twoim pliku, zweryfikuj)
        private bool CanExecuteLoadProfiles(object? arg) => !IsBusy;
        private bool CanExecuteSaveAllProfiles(object? arg) => !IsBusy && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles);
        private bool CanExecuteAutoCreateProfiles(object? arg) => !IsBusy && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath);
        private bool CanExecuteGenerateProfile(object? parameter = null) => !IsBusy && !string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) && !string.IsNullOrWhiteSpace(ModelNameInput) && !string.IsNullOrWhiteSpace(CharacterNameInput) && ImageFiles.Any();
        private bool CanExecuteSuggestImages(object? parameter = null) => !IsBusy && !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) && HierarchicalProfilesList.Any(m => m.HasCharacterProfiles) && !string.IsNullOrWhiteSpace(SourceFolderNamesInput);
        private bool CanExecuteRemoveProfile(object? parameter) => !IsBusy && (parameter is CategoryProfile || SelectedProfile != null);

        // CanExecuteCheckCharacterSuggestions - ta komenda może być teraz zbędna lub jej funkcjonalność zostanie wchłonięta przez nowe okno
        // Ale zostawiam ją, jeśli jest używana gdzieś indziej
        private bool CanExecuteCheckCharacterSuggestions(object? parameter)
        {
            if (IsBusy) return false;
            var p = (parameter as CategoryProfile) ?? SelectedProfile;
            if (p == null) return false;
            // Sprawdź, czy są jakiekolwiek sugestie dla tego profilu w _lastModelSpecificSuggestions LUB czy UI pokazuje >0
            bool hasRelevantSuggestionsInCache = _lastModelSpecificSuggestions.Any(s => s.TargetCategoryProfileName.Equals(p.CategoryName, StringComparison.OrdinalIgnoreCase) && s.Similarity >= SuggestionSimilarityThreshold);
            return p.PendingSuggestionsCount > 0 || hasRelevantSuggestionsInCache;
        }
        private bool CanExecuteMatchModelSpecific(object? parameter) { if (IsBusy || !(parameter is ModelDisplayViewModel m)) return false; return !string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath) && m.HasCharacterProfiles && !string.IsNullOrWhiteSpace(SourceFolderNamesInput); }
        private bool CanExecuteRemoveModelTree(object? parameter) => !IsBusy && parameter is ModelDisplayViewModel;
        private bool CanExecuteSaveAppSettings(object? arg) => !IsBusy;
        private bool CanExecuteAddFilesToProfile(object? arg) => !IsBusy; // Zawsze można próbować dodać pliki
        private bool CanExecuteClearFilesFromProfile(object? arg) => !IsBusy && ImageFiles.Any();
        private bool CanExecuteCreateNewProfileSetup(object? arg) => !IsBusy;
        private bool CanExecuteSelectLibraryPath(object? arg) => !IsBusy;
        private bool CanExecuteAnalyzeModelForSplitting(object? parameter) => !IsBusy && parameter is ModelDisplayViewModel m && m.HasCharacterProfiles;
        private bool CanExecuteOpenSplitProfileDialog(object? parameter) => !IsBusy && parameter is CategoryProfile cp && cp.HasSplitSuggestion;
        private bool CanExecuteCancelCurrentOperation(object? parameter) => IsBusy && _activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested;
        private bool CanExecuteEnsureThumbnailsLoaded(object? parameter) => !IsBusy && parameter is IEnumerable<ImageFileEntry> images && images.Any();
        private bool CanExecuteRemoveDuplicatesInModel(object? parameter) { return parameter is ModelDisplayViewModel m && !IsBusy && m.HasCharacterProfiles; }
        private bool CanExecuteApplyAllMatchesForModel(object? parameter) { if (!(parameter is ModelDisplayViewModel m) || IsBusy) return false; bool hasRelevant = (_lastScannedModelNameForSuggestions == m.ModelName || string.IsNullOrEmpty(_lastScannedModelNameForSuggestions)) && _lastModelSpecificSuggestions.Any(s => s.Similarity >= SuggestionSimilarityThreshold && _profileService.GetModelNameFromCategory(s.TargetCategoryProfileName) == m.ModelName); return m.HasCharacterProfiles && hasRelevant; }

        // Metody Execute dla komend (już istnieją w Twoim pliku, zweryfikuj i dostosuj, jeśli potrzebne)
        // Poniżej znajdują się tylko szkielety niektórych metod, które już masz,
        // ale musisz upewnić się, że przekazujesz token i progress do RunLongOperation
        // oraz że logika wewnątrz jest poprawna.

        private async Task InternalExecuteLoadProfilesAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            SimpleFileLogger.LogHighLevelInfo($"InternalExecuteLoadProfilesAsync. RefreshFlag: {_isRefreshingProfilesPostMove}. Token: {token.GetHashCode()}");
            progress.Report(new ProgressReport { OperationName = "Ładowanie Profili", StatusMessage = "Wczytywanie profili z dysku...", IsIndeterminate = true });

            token.ThrowIfCancellationRequested();
            string? prevSelectedName = SelectedProfile?.CategoryName; // Zapamiętaj poprzednio wybrany profil

            await _profileService.LoadProfilesAsync(token); // Załaduj profile z serwisu
            token.ThrowIfCancellationRequested();

            var flatProfiles = _profileService.GetAllProfiles()?.OrderBy(p => p.CategoryName).ToList();
            int totalModels = 0; // Licznik modeli do przetworzenia

            // Operacje na UI muszą być wykonane w wątku UI
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HierarchicalProfilesList.Clear(); // Wyczyść obecną listę
                if (flatProfiles?.Any() == true)
                {
                    var grouped = flatProfiles.GroupBy(p => _profileService.GetModelNameFromCategory(p.CategoryName)).OrderBy(g => g.Key);
                    int modelsProcessed = 0;
                    totalModels = grouped.Count();
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

                SimpleFileLogger.LogHighLevelInfo($"Wątek UI: Załadowano profile. Modele: {HierarchicalProfilesList.Count}, Profile łącznie: {HierarchicalProfilesList.Sum(m => m.CharacterProfiles.Count)}");

                // Przywróć zaznaczenie, jeśli to możliwe
                if (!string.IsNullOrEmpty(prevSelectedName))
                {
                    SelectedProfile = flatProfiles?.FirstOrDefault(p => p.CategoryName.Equals(prevSelectedName, StringComparison.OrdinalIgnoreCase));
                }
                else if (SelectedProfile != null && !(flatProfiles?.Any(p => p.CategoryName == SelectedProfile.CategoryName) ?? false))
                {
                    // Jeśli poprzednio wybrany profil już nie istnieje, odznacz
                    SelectedProfile = null;
                }

                OnPropertyChanged(nameof(AnyProfilesLoaded)); // Zaktualizuj flagę
                if (_lastModelSpecificSuggestions.Any() || _lastScannedModelNameForSuggestions == "__CACHE_CLEARED__" || string.IsNullOrEmpty(_lastScannedModelNameForSuggestions))
                { // Odśwież zawsze po załadowaniu profili jeśli cache był używany lub mógł być
                    RefreshPendingSuggestionCountsFromCache();
                }


                progress.Report(new ProgressReport { ProcessedItems = totalModels, TotalItems = totalModels, StatusMessage = $"Załadowano {HierarchicalProfilesList.Sum(m => m.CharacterProfiles.Count)} profili." });
            });
        }

        private async Task ExecuteGenerateProfileAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            bool profilesActuallyRegenerated = false; // Flaga do śledzenia, czy profil został faktycznie zmieniony
            token.ThrowIfCancellationRequested(); // Sprawdź anulowanie na początku
            if (string.IsNullOrWhiteSpace(CurrentProfileNameForEdit) || string.IsNullOrWhiteSpace(ModelNameInput) || string.IsNullOrWhiteSpace(CharacterNameInput))
            { StatusMessage = "Błąd: Nazwa modelki i postaci oraz pełna nazwa profilu muszą być zdefiniowane."; MessageBox.Show(StatusMessage, "Błąd danych profilu", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            string catName = CurrentProfileNameForEdit;
            SimpleFileLogger.LogHighLevelInfo($"Generowanie profilu '{catName}' ({ImageFiles.Count} obr.). Token: {token.GetHashCode()}");
            progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = ImageFiles.Count, OperationName = $"Generowanie '{catName}'", StatusMessage = $"Przygotowywanie plików..." });

            List<ImageFileEntry> entriesToProcess = new List<ImageFileEntry>();
            int filesPrepared = 0;
            foreach (var file in ImageFiles) // Użyj kopii listy, jeśli ImageFiles może być modyfikowane w trakcie
            {
                token.ThrowIfCancellationRequested();
                // Załaduj pełne metadane, jeśli są niekompletne (np. tylko ścieżka)
                if (file.FileSize == 0 || file.FileLastModifiedUtc == DateTime.MinValue) // Prosty warunek na niekompletne metadane
                {
                    progress.Report(new ProgressReport { ProcessedItems = filesPrepared, TotalItems = ImageFiles.Count, StatusMessage = $"Metadane: {file.FileName}..." });
                    var updatedEntry = await _imageMetadataService.ExtractMetadataAsync(file.FilePath);
                    if (updatedEntry != null) entriesToProcess.Add(updatedEntry);
                    else SimpleFileLogger.LogWarning($"ExecuteGenerateProfileAsync: Nie udało się załadować metadanych dla {file.FilePath}, pomijam.");
                }
                else
                {
                    entriesToProcess.Add(file); // Zakładamy, że metadane są już kompletne
                }
                filesPrepared++;
                progress.Report(new ProgressReport { ProcessedItems = filesPrepared, TotalItems = ImageFiles.Count, StatusMessage = $"Przygotowano {filesPrepared}/{ImageFiles.Count}..." });
            }
            token.ThrowIfCancellationRequested(); // Sprawdź ponownie przed kosztowną operacją
            if (!entriesToProcess.Any() && ImageFiles.Any()) { StatusMessage = "Błąd: Nie udało się przetworzyć żadnego z wybranych plików."; MessageBox.Show(StatusMessage, "Błąd plików", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            await _profileService.GenerateProfileAsync(catName, entriesToProcess, progress, token);
            profilesActuallyRegenerated = true; // Zakładamy, że GenerateProfileAsync zawsze coś zmienia, jeśli nie rzuci wyjątku
            token.ThrowIfCancellationRequested();

            if (profilesActuallyRegenerated)
            {
                _isRefreshingProfilesPostMove = true; // Ustaw flagę, że zmiana wymaga odświeżenia
                await InternalExecuteLoadProfilesAsync(token, progress); // Odśwież listę profili
                _isRefreshingProfilesPostMove = false; // Zresetuj flagę
                SelectedProfile = _profileService.GetProfile(catName); // Spróbuj ponownie wybrać nowo utworzony/zaktualizowany profil
            }
            // StatusMessage ustawiany przez RunLongOperation
        }

        private async Task ExecuteSaveAllProfilesAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            SimpleFileLogger.LogHighLevelInfo($"Zapis wszystkich profili. Token: {token.GetHashCode()}");
            var allProfiles = _profileService.GetAllProfiles();
            // Liczba operacji to liczba unikalnych folderów modelek (bo zapisujemy per modelka)
            int totalToSave = allProfiles.Select(p => _profileService.GetModelNameFromCategory(p.CategoryName)).Distinct().Count();
            progress.Report(new ProgressReport { OperationName = "Zapis Profili", StatusMessage = "Rozpoczynanie zapisu...", TotalItems = totalToSave, ProcessedItems = 0 });

            await _profileService.SaveAllProfilesAsync(token); // Przekaż token
            token.ThrowIfCancellationRequested(); // Sprawdź anulowanie po operacji

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
                SimpleFileLogger.LogHighLevelInfo($"Usuwanie profilu '{profileName}'. Token: {token.GetHashCode()}");
                progress.Report(new ProgressReport { StatusMessage = $"Usuwanie '{profileName}'..." });

                if (await _profileService.RemoveProfileAsync(profileName, token))
                {
                    if (SelectedProfile?.CategoryName == profileName) SelectedProfile = null; // Odznacz, jeśli był wybrany
                    profileActuallyRemoved = true;
                }
            }

            if (profileActuallyRemoved)
            {
                _isRefreshingProfilesPostMove = true; // Ustaw flagę, że zmiana wymaga odświeżenia
                await InternalExecuteLoadProfilesAsync(token, progress); // Odśwież listę profili
                _isRefreshingProfilesPostMove = false; // Zresetuj flagę
            }
            // StatusMessage i ProgressStatusText będą ustawione przez RunLongOperation
        }

        private async void ExecuteAddFilesToProfile(object? parameter = null)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Obrazy|*.jpg;*.jpeg;*.png;*.webp|Wszystkie pliki|*.*",
                Title = "Wybierz obrazy",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                IsBusy = true; // Ustaw IsBusy na początku
                string originalStatus = ProgressStatusText; // Zapamiętaj oryginalny status
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

                        // Sprawdź, czy plik już nie istnieje na liście (porównanie ścieżek)
                        if (!ImageFiles.Any(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            var entry = await _imageMetadataService.ExtractMetadataAsync(filePath); // Asynchroniczne ładowanie metadanych
                            if (entry != null)
                            {
                                ImageFiles.Add(entry);
                                addedCount++;
                            }
                            else
                            {
                                SimpleFileLogger.LogWarning($"ExecuteAddFilesToProfile: Błąd ładowania metadanych dla: {filePath}. Pomijanie.");
                            }
                        }
                    }
                    ProgressStatusText = addedCount > 0 ? $"Dodano {addedCount} nowych plików." : "Nie dodano nowych plików (mogły już istnieć na liście).";
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError("Błąd podczas dodawania plików do profilu", ex);
                    ProgressStatusText = "Błąd dodawania plików.";
                    MessageBox.Show($"Wystąpił błąd podczas dodawania plików: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    IsBusy = false; // Zresetuj IsBusy na końcu
                    if (ProgressStatusText.StartsWith("Dodawanie:")) ProgressStatusText = originalStatus; // Przywróć status, jeśli nie został zmieniony
                    // Resetuj pasek postępu po zakończeniu
                    CurrentProgress = 0; MaximumProgress = 100; IsProgressIndeterminate = true;
                }
            }
        }

        private void ExecuteClearFilesFromProfile(object? parameter = null) => ImageFiles.Clear();
        private void ExecuteCreateNewProfileSetup(object? parameter = null) { SelectedProfile = null; ModelNameInput = string.Empty; CharacterNameInput = string.Empty; ImageFiles.Clear(); StatusMessage = "Gotowy do utworzenia nowego profilu."; ProgressStatusText = StatusMessage; }

        private void ExecuteSelectLibraryPath(object? parameter = null)
        {
            if (IsBusy) return; // Nie pozwól na zmianę ścieżki podczas innej operacji
            IsBusy = true; // Zablokuj UI na czas wyboru folderu
            try
            {
                var dialog = new VistaFolderBrowserDialog
                {
                    Description = "Wybierz główny folder biblioteki Cosplay",
                    UseDescriptionForTitle = true, // Użyj opisu jako tytułu okna dialogowego
                    ShowNewFolderButton = true // Pokaż przycisk do tworzenia nowego folderu
                };

                // Ustaw początkową ścieżkę, jeśli już istnieje
                if (!string.IsNullOrWhiteSpace(LibraryRootPath) && Directory.Exists(LibraryRootPath))
                {
                    dialog.SelectedPath = LibraryRootPath;
                }
                else if (Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)))
                {
                    // Domyślnie otwórz w folderze Moje obrazy, jeśli ścieżka biblioteki nie jest ustawiona
                    dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                }

                // Pokaż dialog jako modalny względem aktywnego okna lub głównego okna
                if (dialog.ShowDialog(Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive) ?? Application.Current.MainWindow) == true)
                {
                    LibraryRootPath = dialog.SelectedPath;
                    StatusMessage = $"Wybrano folder biblioteki: {LibraryRootPath}";
                    ProgressStatusText = StatusMessage; // Zaktualizuj również tekst postępu
                }
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError("Błąd podczas wybierania folderu biblioteki", ex);
                MessageBox.Show($"Wystąpił błąd podczas wybierania folderu: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false; // Odblokuj UI
            }
        }

        private async Task ExecuteAutoCreateProfilesAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            SimpleFileLogger.LogHighLevelInfo($"AutoCreateProfiles: Rozpoczęto skanowanie biblioteki: {LibraryRootPath}. Token: {token.GetHashCode()}");
            progress.Report(new ProgressReport { OperationName = "Auto Tworzenie Profili", StatusMessage = "Skanowanie folderu biblioteki...", IsIndeterminate = true });

            var mixedFoldersToIgnore = new HashSet<string>(
                SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(n => n.Trim()),
                StringComparer.OrdinalIgnoreCase);
            token.ThrowIfCancellationRequested();

            List<string> modelDirectories;
            try
            {
                modelDirectories = Directory.GetDirectories(LibraryRootPath).ToList();
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Błąd pobierania folderów modelek z '{LibraryRootPath}'", ex);
                StatusMessage = $"Błąd dostępu do folderu biblioteki: {ex.Message}";
                ProgressStatusText = StatusMessage;
                MessageBox.Show(StatusMessage, "Błąd Biblioteki", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            token.ThrowIfCancellationRequested();

            var modelProcessingTasks = new List<Task<(int profilesChanged, bool anyDataChanged)>>();
            int totalModelsToProcess = modelDirectories.Count; // Całkowita liczba folderów na pierwszym poziomie
            long modelsProcessedCount = 0; // Użyj long dla Interlocked
            progress.Report(new ProgressReport { ProcessedItems = (int)modelsProcessedCount, TotalItems = totalModelsToProcess, StatusMessage = $"Przygotowywanie {totalModelsToProcess} folderów modelek..." });

            foreach (var modelDir in modelDirectories)
            {
                token.ThrowIfCancellationRequested();
                string currentModelName = Path.GetFileName(modelDir);

                // Pomijanie folderów zdefiniowanych jako "Mix" oraz pustych nazw
                if (string.IsNullOrWhiteSpace(currentModelName) || mixedFoldersToIgnore.Contains(currentModelName))
                {
                    SimpleFileLogger.Log($"AutoCreateProfiles: Pomijanie folderu źródłowego/pustego: '{currentModelName}'.");
                    Interlocked.Increment(ref modelsProcessedCount); // Zwiększ licznik przetworzonych
                    progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref modelsProcessedCount), TotalItems = totalModelsToProcess, StatusMessage = $"Pomijanie {currentModelName}..." });
                    continue;
                }

                // Stwórz dedykowany IProgress dla każdego zadania przetwarzania modelu, aby raportować postęp specyficzny dla modelu
                var modelSpecificProgress = new Progress<ProgressReport>(modelReport => {
                    // Aktualizuj główny pasek postępu z informacją o przetwarzanym modelu
                    progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref modelsProcessedCount), TotalItems = totalModelsToProcess, StatusMessage = $"'{currentModelName}': {modelReport.StatusMessage} ({modelReport.ProcessedItems}/{modelReport.TotalItems})" });
                });

                // Uruchom przetwarzanie każdego folderu modelki jako osobne zadanie
                modelProcessingTasks.Add(Task.Run(async () =>
                {
                    var result = await InternalProcessDirectoryForProfileCreationAsync(modelDir, currentModelName, new List<string>(), mixedFoldersToIgnore, modelSpecificProgress, token);
                    Interlocked.Increment(ref modelsProcessedCount); // Zwiększ licznik po zakończeniu przetwarzania modelu
                    // Aktualizuj główny pasek postępu po zakończeniu modelu
                    progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref modelsProcessedCount), TotalItems = totalModelsToProcess, StatusMessage = $"Model '{currentModelName}' zakończony." });
                    return result;
                }, token));
            }

            var results = await Task.WhenAll(modelProcessingTasks); // Poczekaj na zakończenie wszystkich zadań
            token.ThrowIfCancellationRequested();

            int totalProfilesCreatedOrUpdated = results.Sum(r => r.profilesChanged);
            bool anyProfileDataChangedDuringOperation = results.Any(r => r.anyDataChanged);

            StatusMessage = $"Automatyczne tworzenie profili zakończone. Utworzono/zaktualizowano: {totalProfilesCreatedOrUpdated} profili.";
            progress.Report(new ProgressReport { ProcessedItems = totalModelsToProcess, TotalItems = totalModelsToProcess, StatusMessage = StatusMessage });

            if (anyProfileDataChangedDuringOperation)
            {
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token, progress); // Odśwież listę profili, jeśli coś się zmieniło
                _isRefreshingProfilesPostMove = false;
            }
            MessageBox.Show(StatusMessage, "Skanowanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task<(int profilesChanged, bool anyDataChanged)> InternalProcessDirectoryForProfileCreationAsync(
            string currentDirectoryPath,
            string modelNameForProfile, // Nazwa modelki (górny folder)
            List<string> parentCharacterPathParts, // Ścieżka do postaci budowana rekurencyjnie
            HashSet<string> mixedFoldersToIgnore, // Foldery "Mix" do ignorowania
            IProgress<ProgressReport> progress, // Do raportowania postępu dla tego konkretnego zadania
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int profilesGeneratedOrUpdatedThisCall = 0;
            bool anyDataChangedThisCall = false;
            string currentSegmentName = Path.GetFileName(currentDirectoryPath); // Nazwa bieżącego segmentu (folderu)

            progress.Report(new ProgressReport { OperationName = $"Model '{modelNameForProfile}'", StatusMessage = $"Analiza folderu: {currentSegmentName}", IsIndeterminate = true });

            var currentCharacterPathSegments = new List<string>(parentCharacterPathParts);
            string modelRootForCurrentModel = Path.Combine(LibraryRootPath, modelNameForProfile); // Pełna ścieżka do głównego folderu modelki
            bool isAtModelRootLevel = currentDirectoryPath.Equals(modelRootForCurrentModel, StringComparison.OrdinalIgnoreCase);

            // Dodaj bieżący segment do ścieżki postaci, jeśli nie jesteśmy w głównym folderze modelki
            // i jeśli bieżący folder nie jest folderem "Mix"
            if (!isAtModelRootLevel && !mixedFoldersToIgnore.Contains(currentSegmentName))
            {
                currentCharacterPathSegments.Add(currentSegmentName);
            }

            // Zbuduj pełną nazwę postaci (np. "Outfit1 - RedDress")
            string characterFullName = string.Join(" - ", currentCharacterPathSegments);
            // Zbuduj pełną nazwę kategorii profilu (np. "ModelName - Outfit1 - RedDress")
            string categoryName = string.IsNullOrWhiteSpace(characterFullName)
                ? $"{modelNameForProfile} - General" // Jeśli brak segmentów postaci, to profil "General" dla modelki
                : $"{modelNameForProfile} - {characterFullName}";

            SimpleFileLogger.Log($"InternalProcessDir: Przetwarzanie folderu '{currentDirectoryPath}'. Model: '{modelNameForProfile}', Segmenty postaci: '{characterFullName}', Wynikowa kategoria: '{categoryName}'.");

            // Pobierz pliki obrazów tylko z bieżącego folderu (bez podfolderów)
            List<string> imagePathsInThisExactDirectory = new List<string>();
            try
            {
                imagePathsInThisExactDirectory = Directory.GetFiles(currentDirectoryPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f)))
                    .ToList();
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogWarning($"InternalProcessDir: Błąd odczytu plików z '{currentDirectoryPath}': {ex.Message}");
                // Kontynuuj, aby przetworzyć podfoldery
            }
            token.ThrowIfCancellationRequested();

            // Ustal, czy dla tego folderu (na podstawie ścieżki postaci) powinien być generowany profil
            bool shouldProcessImagesForProfile = false;
            if (!string.IsNullOrWhiteSpace(characterFullName)) // Jeśli mamy nazwę postaci (czyli nie jesteśmy w głównym folderze modelki, który sam w sobie jest ignorowany)
            {
                shouldProcessImagesForProfile = true;
            }
            // Jeśli jesteśmy w głównym folderze modelki (np. "ModelName/") i zawiera on obrazy,
            // te obrazy utworzą profil "ModelName - General".
            else if (isAtModelRootLevel)
            {
                // characterFullName będzie pusty, więc categoryName będzie "ModelName - General"
                shouldProcessImagesForProfile = true;
            }


            if (shouldProcessImagesForProfile && imagePathsInThisExactDirectory.Any())
            {
                SimpleFileLogger.Log($"InternalProcessDir: Przetwarzanie {imagePathsInThisExactDirectory.Count} obrazów z folderu '{currentDirectoryPath}' dla kategorii '{categoryName}'.");
                progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = imagePathsInThisExactDirectory.Count, StatusMessage = $"Folder '{currentSegmentName}': Przetwarzanie {imagePathsInThisExactDirectory.Count} obrazów dla profilu '{categoryName}'..." });

                var entriesForProfile = new ConcurrentBag<ImageFileEntry>(); // Bezpieczne dla wątków
                var metadataTasks = new List<Task>();
                long imagesProcessedForMetaLong = 0; // Użyj long dla Interlocked

                foreach (var path in imagePathsInThisExactDirectory)
                {
                    metadataTasks.Add(Task.Run(async () =>
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                        if (entry != null) entriesForProfile.Add(entry);
                        Interlocked.Increment(ref imagesProcessedForMetaLong); // Używaj Interlocked dla zmiennych współdzielonych między zadaniami
                        progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref imagesProcessedForMetaLong), TotalItems = imagePathsInThisExactDirectory.Count, StatusMessage = $"Metadane: {Path.GetFileName(path)}..." });
                    }, token));
                }
                await Task.WhenAll(metadataTasks); // Poczekaj na załadowanie wszystkich metadanych
                token.ThrowIfCancellationRequested();

                if (entriesForProfile.Any())
                {
                    await _profileService.GenerateProfileAsync(categoryName, entriesForProfile.ToList(), progress, token);
                    profilesGeneratedOrUpdatedThisCall++;
                    anyDataChangedThisCall = true;
                    SimpleFileLogger.Log($"InternalProcessDir: Profil '{categoryName}' utworzony/zaktualizowany z {entriesForProfile.Count} obrazami z folderu '{currentDirectoryPath}'.");
                }
            }
            else if (shouldProcessImagesForProfile && !imagePathsInThisExactDirectory.Any()) // Jeśli powinien być profil, ale folder jest pusty
            {
                // Jeśli profil już istnieje (np. z poprzedniego skanowania), a folder jest teraz pusty,
                // zaktualizuj profil na pusty, aby usunąć stare ścieżki.
                if (_profileService.GetProfile(categoryName) != null && !mixedFoldersToIgnore.Contains(currentSegmentName)) // Nie rób tego dla folderów Mix
                {
                    SimpleFileLogger.Log($"InternalProcessDir: Folder '{currentDirectoryPath}' dla kategorii '{categoryName}' jest pusty. Aktualizacja/czyszczenie istniejącego profilu.");
                    await _profileService.GenerateProfileAsync(categoryName, new List<ImageFileEntry>(), progress, token); // Generuj z pustą listą
                    profilesGeneratedOrUpdatedThisCall++; // Liczymy to jako zmianę
                    anyDataChangedThisCall = true;
                }
            }
            else if (!shouldProcessImagesForProfile)
            {
                SimpleFileLogger.Log($"InternalProcessDir: Pomijanie tworzenia profilu z obrazów w '{currentDirectoryPath}', ponieważ jest to folder ignorowany (np. Mix) niebędący głównym folderem modelki.");
            }


            // Rekurencyjne przetwarzanie podfolderów
            token.ThrowIfCancellationRequested();
            try
            {
                var subDirectories = Directory.GetDirectories(currentDirectoryPath);
                var subDirProcessingTasks = new List<Task<(int, bool)>>();
                foreach (var subDirectoryPath in subDirectories)
                {
                    token.ThrowIfCancellationRequested();
                    // Przekaż dalej zbudowaną ścieżkę postaci
                    subDirProcessingTasks.Add(InternalProcessDirectoryForProfileCreationAsync(subDirectoryPath, modelNameForProfile, new List<string>(currentCharacterPathSegments), mixedFoldersToIgnore, progress, token));
                }
                var subDirResults = await Task.WhenAll(subDirProcessingTasks);
                profilesGeneratedOrUpdatedThisCall += subDirResults.Sum(r => r.Item1);
                if (subDirResults.Any(r => r.Item2)) anyDataChangedThisCall = true;
            }
            catch (OperationCanceledException) { throw; } // Przekaż dalej wyjątek anulowania
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"InternalProcessDir: Błąd przetwarzania podfolderów dla '{currentDirectoryPath}'", ex);
                // Można rozważyć, czy błąd w jednym podfolderze powinien zatrzymać całą operację
            }

            return (profilesGeneratedOrUpdatedThisCall, anyDataChangedThisCall);
        }


        // Metoda do porównywania jakości obrazów (już istnieje w Twoim pliku)
        private bool IsImageBetter(ImageFileEntry entry1, ImageFileEntry entry2) { if (entry1 == null || entry2 == null) return false; long r1 = (long)entry1.Width * entry1.Height; long r2 = (long)entry2.Width * entry2.Height; if (r1 > r2) return true; if (r1 < r2) return false; return entry1.FileSize > entry2.FileSize; }

        // Metoda do aktualizacji profili po przeniesieniu/usunięciu pliku (już istnieje w Twoim pliku)
        private async Task<bool> HandleFileMovedOrDeletedUpdateProfilesAsync(string? oldPath, string? newPathIfMoved, string? targetCategoryNameIfMoved, CancellationToken token, IProgress<ProgressReport> progress)
        {
            SimpleFileLogger.Log($"[ProfileUpdate] Aktualizacja profili po operacji na pliku. Stara ścieżka='{oldPath}', Nowa ścieżka='{newPathIfMoved}', Kategoria docelowa='{targetCategoryNameIfMoved}'. Token: {token.GetHashCode()}");
            progress.Report(new ProgressReport { OperationName = "Aktualizacja Profili", StatusMessage = "Aktualizacja definicji profili po zmianach plików...", IsIndeterminate = true });

            var affectedProfileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyProfileDataActuallyChanged = false;

            var allProfiles = _profileService.GetAllProfiles().ToList(); // Pracuj na kopii listy
            foreach (var profile in allProfiles)
            {
                token.ThrowIfCancellationRequested();
                bool currentProfileNeedsRegeneration = false;

                // Jeśli plik został usunięty (oldPath podany, newPathIfMoved jest null) lub przeniesiony (oba podane)
                if (!string.IsNullOrWhiteSpace(oldPath))
                {
                    // Usuń starą ścieżkę z listy obrazów profilu, jeśli tam była
                    if (profile.SourceImagePaths?.RemoveAll(p => p.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) > 0)
                    {
                        SimpleFileLogger.Log($"[ProfileUpdate] Usunięto ścieżkę '{oldPath}' z profilu '{profile.CategoryName}'.");
                        currentProfileNeedsRegeneration = true;
                    }
                }

                // Jeśli plik został przeniesiony do tego profilu (newPathIfMoved i targetCategoryNameIfMoved podane)
                if (!string.IsNullOrWhiteSpace(newPathIfMoved) &&
                    !string.IsNullOrWhiteSpace(targetCategoryNameIfMoved) &&
                    profile.CategoryName.Equals(targetCategoryNameIfMoved, StringComparison.OrdinalIgnoreCase))
                {
                    profile.SourceImagePaths ??= new List<string>(); // Upewnij się, że lista istnieje
                    // Dodaj nową ścieżkę, jeśli jej jeszcze nie ma
                    if (!profile.SourceImagePaths.Any(p => p.Equals(newPathIfMoved, StringComparison.OrdinalIgnoreCase)))
                    {
                        profile.SourceImagePaths.Add(newPathIfMoved);
                        SimpleFileLogger.Log($"[ProfileUpdate] Dodano ścieżkę '{newPathIfMoved}' do profilu '{profile.CategoryName}'.");
                        currentProfileNeedsRegeneration = true;
                    }
                }

                if (currentProfileNeedsRegeneration)
                {
                    affectedProfileNames.Add(profile.CategoryName);
                    anyProfileDataActuallyChanged = true; // Zaznacz, że dane profili się zmieniły
                }
            }
            token.ThrowIfCancellationRequested();

            if (affectedProfileNames.Any())
            {
                SimpleFileLogger.Log($"[ProfileUpdate] {affectedProfileNames.Count} profili wymaga regeneracji: {string.Join(", ", affectedProfileNames)}");
                int regeneratedCount = 0;
                int totalToRegen = affectedProfileNames.Count;
                progress.Report(new ProgressReport { ProcessedItems = regeneratedCount, TotalItems = totalToRegen, StatusMessage = $"Regenerowanie {totalToRegen} zmienionych profili..." });

                foreach (var profileName in affectedProfileNames)
                {
                    token.ThrowIfCancellationRequested();
                    var affectedProfile = _profileService.GetProfile(profileName);
                    if (affectedProfile == null)
                    {
                        SimpleFileLogger.LogWarning($"[ProfileUpdate] Profil '{profileName}' nie został znaleziony podczas próby regeneracji.");
                        regeneratedCount++;
                        progress.Report(new ProgressReport { ProcessedItems = regeneratedCount, TotalItems = totalToRegen, StatusMessage = $"Nie znaleziono profilu '{profileName}'." });
                        continue;
                    }

                    progress.Report(new ProgressReport { ProcessedItems = regeneratedCount, TotalItems = totalToRegen, StatusMessage = $"Regenerowanie profilu '{profileName}'..." });
                    var entriesForRegeneration = new List<ImageFileEntry>();
                    if (affectedProfile.SourceImagePaths != null)
                    {
                        foreach (var path in affectedProfile.SourceImagePaths.ToList()) // Pracuj na aktualnej liście ścieżek .ToList()
                        {
                            token.ThrowIfCancellationRequested();
                            if (File.Exists(path)) // Sprawdź, czy plik nadal istnieje
                            {
                                var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                                if (entry != null) entriesForRegeneration.Add(entry);
                            }
                            else
                            {
                                SimpleFileLogger.LogWarning($"[ProfileUpdate] Ścieżka '{path}' w profilu '{profileName}' nie istnieje podczas regeneracji.");
                                // Usuń nieistniejącą ścieżkę z affectedProfile.SourceImagePaths
                                affectedProfile.SourceImagePaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
                            }
                        }
                    }
                    SimpleFileLogger.Log($"[ProfileUpdate] Regenerowanie profilu '{profileName}' z {entriesForRegeneration.Count} obrazami.");
                    await _profileService.GenerateProfileAsync(profileName, entriesForRegeneration, progress, token); // Regeneruj z aktualną listą obrazów
                    regeneratedCount++;
                    progress.Report(new ProgressReport { ProcessedItems = regeneratedCount, TotalItems = totalToRegen, StatusMessage = $"Zregenerowano profil '{profileName}'." });
                }
                SimpleFileLogger.Log($"[ProfileUpdate] Zakończono regenerację dla {affectedProfileNames.Count} profili.");
            }
            else
            {
                SimpleFileLogger.Log($"[ProfileUpdate] Brak profili do regeneracji dla operacji na pliku (stara ścieżka: '{oldPath}').");
            }
            return anyProfileDataActuallyChanged;
        }


        // Metoda do obsługi duplikatów lub sugerowania nowych (już istnieje w Twoim pliku)
        private async Task<(Models.ProposedMove? proposedMove, bool wasActionAutoHandled, bool profilesWereModified)> ProcessDuplicateOrSuggestNewAsync(
            ImageFileEntry sourceImageEntry,
            CategoryProfile targetProfileForSuggestion, // Zmieniono nazwę parametru, aby uniknąć konfliktu
            double similarityToCentroid,
            string modelDirectoryPath, // Główny folder modelki
            float[] sourceImageEmbedding, // Embedding obrazu źródłowego
            IProgress<ProgressReport> progress,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            bool profilesModifiedThisCall = false;

            var (_, characterFolderName) = ParseCategoryName(targetProfileForSuggestion.CategoryName);
            string targetCharacterPath = Path.Combine(modelDirectoryPath, SanitizeFolderName(characterFolderName)); // Ścieżka do folderu postaci
            Directory.CreateDirectory(targetCharacterPath); // Upewnij się, że folder istnieje

            List<string> filesInTargetCharacterFolder;
            try
            {
                filesInTargetCharacterFolder = Directory.EnumerateFiles(targetCharacterPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => _fileScannerService.IsExtensionSupported(Path.GetExtension(f)))
                    .ToList();
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Błąd odczytu plików z folderu docelowego '{targetCharacterPath}'", ex);
                filesInTargetCharacterFolder = new List<string>(); // Kontynuuj z pustą listą
            }

            // Sprawdź duplikaty w folderze docelowym
            foreach (string existingFilePathInTarget in filesInTargetCharacterFolder)
            {
                token.ThrowIfCancellationRequested();
                // Pomijanie, jeśli porównujemy plik sam ze sobą (jeśli jakimś cudem źródłowy jest już w docelowym)
                if (string.Equals(Path.GetFullPath(existingFilePathInTarget), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase)) continue;

                var existingEntryInTarget = await _imageMetadataService.ExtractMetadataAsync(existingFilePathInTarget);
                if (existingEntryInTarget == null) continue; // Nie udało się załadować metadanych

                float[]? existingEmbedding = existingEntryInTarget.FeatureVector ?? await _profileService.GetImageEmbeddingAsync(existingEntryInTarget, token); // Pobierz embedding, jeśli nie ma
                if (existingEmbedding == null) { SimpleFileLogger.LogWarning($"[ProcessDupOrSuggest] Brak embeddingu dla istniejącego pliku w folderze docelowym: {existingEntryInTarget.FilePath}"); continue; }

                double similaritySourceToExisting = Utils.MathUtils.CalculateCosineSimilarity(sourceImageEmbedding, existingEmbedding);

                if (similaritySourceToExisting >= DUPLICATE_SIMILARITY_THRESHOLD) // Znaleziono duplikat graficzny
                {
                    bool sourceIsBetter = IsImageBetter(sourceImageEntry, existingEntryInTarget);
                    if (sourceIsBetter)
                    {
                        SimpleFileLogger.Log($"[AutoReplace] Obraz źródłowy '{sourceImageEntry.FilePath}' jest lepszy niż istniejący duplikat '{existingEntryInTarget.FilePath}'. Zastępowanie.");
                        try
                        {
                            // Zastąp istniejący plik nowym (lepszym)
                            File.Copy(sourceImageEntry.FilePath, existingEntryInTarget.FilePath, true); // Nadpisz
                            string oldSourcePathToDelete = sourceImageEntry.FilePath; // Zapamiętaj ścieżkę do usunięcia
                            // Po skopiowaniu usuń oryginalny plik źródłowy (bo został "przeniesiony" jako lepsza wersja)
                            File.Delete(oldSourcePathToDelete);
                            // Zaktualizuj profile, informując o usunięciu starego pliku źródłowego
                            // Nowy plik (ten zastąpiony) już powinien być w profilu docelowym (jeśli był), lub zostanie dodany przez główną logikę sugestii
                            if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldSourcePathToDelete, null, null, token, progress)) profilesModifiedThisCall = true;
                        }
                        catch (Exception ex) { SimpleFileLogger.LogError($"[AutoReplace] Błąd podczas zastępowania duplikatu: {ex.Message}", ex); }
                        return (null, true, profilesModifiedThisCall); // Akcja auto-obsłużona, brak sugestii
                    }
                    else
                    {
                        SimpleFileLogger.Log($"[AutoDeleteSource] Istniejący duplikat '{existingEntryInTarget.FilePath}' jest lepszy lub równy. Usuwanie obrazu źródłowego '{sourceImageEntry.FilePath}'.");
                        try
                        {
                            string oldSourcePathToDelete = sourceImageEntry.FilePath;
                            File.Delete(oldSourcePathToDelete); // Usuń gorszy plik źródłowy
                            if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldSourcePathToDelete, null, null, token, progress)) profilesModifiedThisCall = true;
                        }
                        catch (Exception ex) { SimpleFileLogger.LogError($"[AutoDeleteSource] Błąd podczas usuwania gorszego źródła: {ex.Message}", ex); }
                        return (null, true, profilesModifiedThisCall); // Akcja auto-obsłużona
                    }
                }
            }
            token.ThrowIfCancellationRequested();

            // Jeśli nie znaleziono duplikatu, a podobieństwo do centroidu jest wystarczające, zaproponuj przeniesienie
            if (similarityToCentroid >= SuggestionSimilarityThreshold)
            {
                string proposedTargetPath = Path.Combine(targetCharacterPath, sourceImageEntry.FileName); // Proponowana ścieżka docelowa
                ProposedMoveActionType actionType;
                ImageFileEntry? displayTargetImageForConflict = null; // Dla podglądu konfliktu

                // Sprawdź konflikt nazw plików (ale nie ten sam plik)
                if (File.Exists(proposedTargetPath) && !string.Equals(Path.GetFullPath(proposedTargetPath), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    actionType = ProposedMoveActionType.ConflictKeepBoth; // Domyślnie zachowaj oba przy konflikcie
                    displayTargetImageForConflict = await _imageMetadataService.ExtractMetadataAsync(proposedTargetPath); // Załaduj dane obrazu powodującego konflikt
                }
                else if (string.Equals(Path.GetFullPath(proposedTargetPath), Path.GetFullPath(sourceImageEntry.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    // Plik źródłowy jest już w folderze docelowym pod tą samą nazwą - nie rób nic, to nie jest sugestia przeniesienia
                    SimpleFileLogger.Log($"[Suggest] Plik '{sourceImageEntry.FilePath}' już znajduje się w folderze docelowym '{targetCharacterPath}'. Pomijanie sugestii.");
                    return (null, false, profilesModifiedThisCall); // Nie jest to sugestia, ani auto-akcja
                }
                else
                {
                    actionType = ProposedMoveActionType.CopyNew; // Domyślna akcja to kopiowanie jako nowy
                }

                var proposedMove = new Models.ProposedMove(
                    sourceImageEntry,
                    displayTargetImageForConflict, // Obraz powodujący konflikt (jeśli jest)
                    proposedTargetPath,
                    similarityToCentroid,
                    targetProfileForSuggestion.CategoryName,
                    actionType,
                    sourceImageEmbedding);

                SimpleFileLogger.Log($"[Suggest] Sugestia: Akcja '{actionType}' dla pliku '{sourceImageEntry.FileName}' do profilu '{targetProfileForSuggestion.CategoryName}', Podobieństwo: {similarityToCentroid:F4}");
                return (proposedMove, false, profilesModifiedThisCall); // Zwróć sugestię
            }

            // Jeśli podobieństwo jest zbyt niskie, nie rób nic
            SimpleFileLogger.Log($"[Suggest] Plik '{sourceImageEntry.FileName}' (Podobieństwo: {similarityToCentroid:F4}) nie pasuje wystarczająco ({SuggestionSimilarityThreshold:F2}) do profilu '{targetProfileForSuggestion.CategoryName}'.");
            return (null, false, profilesModifiedThisCall); // Brak sugestii, brak auto-akcji
        }


        // Metoda do dopasowywania obrazów dla konkretnej modelki (już istnieje w Twoim pliku)
        private async Task ExecuteMatchModelSpecificAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nie wybrano modelki do dopasowania."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            SimpleFileLogger.LogHighLevelInfo($"MatchModelSpecific: Rozpoczęto dopasowywanie dla modelki '{modelVM.ModelName}'. Token: {token.GetHashCode()}");

            var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
            if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe ('Mix') w ustawieniach."; MessageBox.Show(StatusMessage, "Brak Folderów Mix", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var movesForSuggestionWindowConcurrent = new ConcurrentBag<Models.ProposedMove>(); // Bezpieczne dla wątków
            string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName); // Główny folder modelki
            if (!Directory.Exists(modelPath)) { StatusMessage = $"Błąd: Folder modelki '{modelVM.ModelName}' nie istnieje w '{LibraryRootPath}'."; MessageBox.Show(StatusMessage, "Błąd Folderu Modelki", MessageBoxButton.OK, MessageBoxImage.Error); return; }

            // Wyzeruj liczniki sugestii w UI przed rozpoczęciem
            await Application.Current.Dispatcher.InvokeAsync(() => { modelVM.PendingSuggestionsCount = 0; foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; });
            token.ThrowIfCancellationRequested();

            List<string> allImagePathsInMix = new List<string>();
            foreach (var mixFolderName in mixedFolders)
            {
                string currentMixPath = Path.Combine(modelPath, mixFolderName); // Np. Library/ModelName/Mix
                if (Directory.Exists(currentMixPath))
                {
                    allImagePathsInMix.AddRange(await _fileScannerService.ScanDirectoryAsync(currentMixPath));
                }
            }
            allImagePathsInMix = allImagePathsInMix.Distinct().ToList(); // Usuń duplikaty ścieżek, jeśli są

            long totalFilesToScanInMix = allImagePathsInMix.Count;
            if (totalFilesToScanInMix == 0)
            {
                StatusMessage = $"Brak plików w folderach Mix dla modelki '{modelVM.ModelName}'.";
                MessageBox.Show(StatusMessage, "Brak Plików", MessageBoxButton.OK, MessageBoxImage.Information);
                progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = StatusMessage }); // Zakończ postęp
                return;
            }

            progress.Report(new ProgressReport { OperationName = $"Match dla '{modelVM.ModelName}'", ProcessedItems = 0, TotalItems = (int)totalFilesToScanInMix, StatusMessage = $"Skanowanie {totalFilesToScanInMix} plików w folderach Mix dla '{modelVM.ModelName}'..." });

            long filesProcessedCount = 0; // Używaj long dla Interlocked
            long filesWithEmbeddingsCount = 0; // Używaj long
            long autoActionsCount = 0; // Używaj long
            bool anyProfileDataChanged = false;

            var alreadySuggestedDuplicates = new ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)>(); // Do śledzenia już obsłużonych duplikatów graficznych
            var processingTasks = new List<Task<ProcessingResult>>();

            foreach (var imgPathFromMix in allImagePathsInMix)
            {
                token.ThrowIfCancellationRequested();
                processingTasks.Add(Task.Run(async () =>
                {
                    var res = await ProcessSingleImageForModelSpecificScanAsync(imgPathFromMix, modelVM, modelPath, token, movesForSuggestionWindowConcurrent, alreadySuggestedDuplicates, progress);
                    Interlocked.Increment(ref filesProcessedCount);
                    progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref filesProcessedCount), TotalItems = (int)totalFilesToScanInMix, StatusMessage = $"Przetwarzanie {Path.GetFileName(imgPathFromMix)} ({Interlocked.Read(ref filesProcessedCount)}/{totalFilesToScanInMix})..." });
                    return res;
                }, token));
            }

            var taskResults = await Task.WhenAll(processingTasks); // Poczekaj na wszystkie zadania
            token.ThrowIfCancellationRequested();

            // Zbierz wyniki
            foreach (var r in taskResults)
            {
                filesWithEmbeddingsCount += r.FilesWithEmbeddingsIncrement;
                autoActionsCount += r.AutoActionsIncrement;
                if (r.ProfileDataChanged) anyProfileDataChanged = true;
            }

            var movesForSuggestionWindow = movesForSuggestionWindowConcurrent.ToList();
            SimpleFileLogger.LogHighLevelInfo($"MatchModelSpecific dla '{modelVM.ModelName}': Przeskanowano: {totalFilesToScanInMix}, Z embeddingami: {filesWithEmbeddingsCount}. Auto-akcje: {autoActionsCount}. Sugestie: {movesForSuggestionWindow.Count}. Profile zmodyfikowane: {anyProfileDataChanged}. Token: {token.GetHashCode()}");
            progress.Report(new ProgressReport { ProcessedItems = (int)totalFilesToScanInMix, TotalItems = (int)totalFilesToScanInMix, StatusMessage = $"Analiza dla '{modelVM.ModelName}' zakończona." });

            // Zapisz sugestie do cache (dla tej konkretnej modelki)
            _lastModelSpecificSuggestions = new List<Models.ProposedMove>(movesForSuggestionWindow);
            _lastScannedModelNameForSuggestions = modelVM.ModelName; // Zaznacz, że cache dotyczy tej modelki

            // Pokaż okno PreviewChanges, jeśli są jakieś sugestie
            if (movesForSuggestionWindow.Any())
            {
                bool? dialogOutcome = false;
                List<Models.ProposedMove> approvedMoves = new List<Models.ProposedMove>();
                await Application.Current.Dispatcher.InvokeAsync(() => // Operacje na UI w wątku UI
                {
                    var vm = new PreviewChangesViewModel(movesForSuggestionWindow, SuggestionSimilarityThreshold);
                    var win = new PreviewChangesWindow { DataContext = vm, Owner = Application.Current.MainWindow };
                    win.SetViewModelCloseAction(vm); // Umożliwia VM zamknięcie okna
                    dialogOutcome = win.ShowDialog();
                    if (dialogOutcome == true)
                    {
                        approvedMoves = vm.GetApprovedMoves(); // Pobierz zatwierdzone ruchy
                    }
                });
                token.ThrowIfCancellationRequested(); // Sprawdź po zamknięciu dialogu

                if (dialogOutcome == true && approvedMoves.Any())
                {
                    progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = approvedMoves.Count, StatusMessage = $"Stosowanie {approvedMoves.Count} zatwierdzonych zmian..." });
                    if (await InternalHandleApprovedMovesAsync(approvedMoves, modelVM, null, token, progress)) anyProfileDataChanged = true;
                    // Usuń zastosowane sugestie z głównej listy cache
                    _lastModelSpecificSuggestions.RemoveAll(s => approvedMoves.Any(ap => ap.SourceImage.FilePath == s.SourceImage.FilePath));
                }
                else if (dialogOutcome == false) // Użytkownik anulował
                {
                    StatusMessage = $"Anulowano stosowanie sugestii dla '{modelVM.ModelName}'.";
                }
            }

            if (anyProfileDataChanged)
            {
                SimpleFileLogger.LogHighLevelInfo($"ExecuteMatchModelSpecificAsync: Zmiany w profilach dla '{modelVM.ModelName}'. Odświeżanie listy profili.");
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token, progress); // Odśwież listę profili
                _isRefreshingProfilesPostMove = false;
            }

            RefreshPendingSuggestionCountsFromCache(); // Odśwież liczniki w UI
            StatusMessage = $"Dla '{modelVM.ModelName}': {autoActionsCount} auto-akcji, {modelVM.PendingSuggestionsCount} oczekujących sugestii.";

            // Komunikaty końcowe
            if (!movesForSuggestionWindow.Any() && autoActionsCount > 0 && !anyProfileDataChanged) MessageBox.Show($"Zakończono automatyczne operacje dla '{modelVM.ModelName}'. Wykonano {autoActionsCount} akcji. Brak nowych sugestii.", "Operacje Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (!movesForSuggestionWindow.Any() && autoActionsCount == 0 && !anyProfileDataChanged && totalFilesToScanInMix > 0) MessageBox.Show($"Brak nowych sugestii lub automatycznych akcji dla '{modelVM.ModelName}'.", "Brak Zmian", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (totalFilesToScanInMix == 0 && !anyProfileDataChanged) MessageBox.Show($"Nie znaleziono obrazów w folderach Mix dla '{modelVM.ModelName}'.", "Brak Plików", MessageBoxButton.OK, MessageBoxImage.Information);
            // Jeśli były sugestie, okno PreviewChanges już poinformowało użytkownika
        }

        // Metoda do przetwarzania pojedynczego obrazu dla MatchModelSpecific (już istnieje w Twoim pliku)
        private async Task<ProcessingResult> ProcessSingleImageForModelSpecificScanAsync(
            string imgPathFromMix,
            ModelDisplayViewModel modelVM,
            string modelPath, // Ścieżka do głównego folderu modelki
            CancellationToken token,
            ConcurrentBag<Models.ProposedMove> movesForSuggestionWindowConcurrent,
            ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)> alreadySuggestedGraphicDuplicatesConcurrent, // Do śledzenia duplikatów
            IProgress<ProgressReport> progress) // Przekaż progress do ProcessDuplicateOrSuggestNewAsync
        {
            var result = new ProcessingResult();
            if (token.IsCancellationRequested || !File.Exists(imgPathFromMix)) return result;

            var sourceEntry = await _imageMetadataService.ExtractMetadataAsync(imgPathFromMix);
            if (sourceEntry == null) { SimpleFileLogger.LogWarning($"MatchModelSpecific(AsyncItem): Błąd ładowania metadanych dla: {imgPathFromMix}."); return result; }

            // Pobierz embedding, używając semafora do ograniczenia współbieżności
            await _embeddingSemaphore.WaitAsync(token);
            float[]? sourceEmbedding = null;
            try
            {
                if (token.IsCancellationRequested) return result;
                sourceEmbedding = await _profileService.GetImageEmbeddingAsync(sourceEntry, token);
            }
            finally
            {
                _embeddingSemaphore.Release();
            }

            if (sourceEmbedding == null) { SimpleFileLogger.LogWarning($"MatchModelSpecific(AsyncItem): Błąd uzyskania embeddingu dla: {sourceEntry.FilePath}."); return result; }
            result.FilesWithEmbeddingsIncrement = 1; // Zlicz plik z embeddingiem
            if (token.IsCancellationRequested) return result;

            // Zasugeruj kategorię tylko w obrębie tej modelki
            var bestSuggestionForModel = _profileService.SuggestCategory(sourceEmbedding, SuggestionSimilarityThreshold, modelVM.ModelName);

            if (bestSuggestionForModel != null)
            {
                // POPRAWKA DEKONSTRUKCJI
                CategoryProfile targetProfile = bestSuggestionForModel.Item1;
                double similarity = bestSuggestionForModel.Item2;

                var (proposedMove, wasAutoHandled, profilesWereModified) = await ProcessDuplicateOrSuggestNewAsync(sourceEntry, targetProfile, similarity, modelPath, sourceEmbedding, progress, token);

                if (profilesWereModified) result.ProfileDataChanged = true;
                if (wasAutoHandled) result.AutoActionsIncrement = 1;
                else if (proposedMove != null) movesForSuggestionWindowConcurrent.Add(proposedMove);
            }
            else
            {
                SimpleFileLogger.Log($"MatchModelSpecific(AsyncItem): Brak wystarczająco dobrej sugestii dla pliku '{sourceEntry.FilePath}' w obrębie modelki '{modelVM.ModelName}'.");
            }
            return result;
        }

        // Metoda do globalnego sugerowania obrazów (już istnieje w Twoim pliku)
        private async Task ExecuteSuggestImagesAsync(CancellationToken token, IProgress<ProgressReport> progress)
        {
            ClearModelSpecificSuggestionsCache(); // Wyczyść poprzednie sugestie (bo to skan globalny)
            token.ThrowIfCancellationRequested();

            var mixedFolders = new HashSet<string>(SourceFolderNamesInput.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
            if (!mixedFolders.Any()) { StatusMessage = "Błąd: Zdefiniuj foldery źródłowe ('Mix') w ustawieniach."; MessageBox.Show(StatusMessage, "Brak Folderów Mix", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var allCollectedSuggestionsGlobalConcurrent = new ConcurrentBag<Models.ProposedMove>();
            var alreadySuggestedDuplicates = new ConcurrentBag<(float[] embedding, string targetCategoryName, string sourceFilePath)>(); // Do śledzenia duplikatów

            // Wyzeruj liczniki sugestii w UI
            await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var mVM_ui in HierarchicalProfilesList) { mVM_ui.PendingSuggestionsCount = 0; foreach (var cp_ui in mVM_ui.CharacterProfiles) cp_ui.PendingSuggestionsCount = 0; } });
            token.ThrowIfCancellationRequested();

            var allModelsCurrentlyInList = HierarchicalProfilesList.ToList(); // Pracuj na kopii listy modeli
            List<string> allImagePathsToScanGlobal = new List<string>();

            // Zbierz wszystkie pliki ze wszystkich folderów "Mix" wszystkich modelek
            foreach (var modelVM in allModelsCurrentlyInList)
            {
                string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName);
                if (!Directory.Exists(modelPath) || !modelVM.HasCharacterProfiles) continue; // Pomijaj modelki bez profili lub folderu

                foreach (var mixFolderName in mixedFolders)
                {
                    string currentMixPath = Path.Combine(modelPath, mixFolderName);
                    if (Directory.Exists(currentMixPath))
                    {
                        allImagePathsToScanGlobal.AddRange(await _fileScannerService.ScanDirectoryAsync(currentMixPath));
                    }
                }
            }
            allImagePathsToScanGlobal = allImagePathsToScanGlobal.Distinct().ToList(); // Unikalne ścieżki

            long totalFilesToProcess = allImagePathsToScanGlobal.Count;
            if (totalFilesToProcess == 0)
            {
                StatusMessage = "Brak plików w folderach Mix do globalnego skanowania.";
                MessageBox.Show(StatusMessage, "Brak Plików", MessageBoxButton.OK, MessageBoxImage.Information);
                progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = StatusMessage });
                return;
            }

            progress.Report(new ProgressReport { OperationName = "Globalne Sugestie", ProcessedItems = 0, TotalItems = (int)totalFilesToProcess, StatusMessage = $"Skanowanie {totalFilesToProcess} plików ze wszystkich folderów Mix..." });

            long filesProcessedCount = 0; // Użyj long
            long filesWithEmbeddingsCount = 0; // Użyj long
            long autoActionsCount = 0; // Użyj long
            bool anyProfileDataChanged = false;
            var processingTasks = new List<Task<ProcessingResult>>();

            foreach (var imgPathFromMix in allImagePathsToScanGlobal)
            {
                token.ThrowIfCancellationRequested();
                // Znajdź, do której modelki należy ten plik z folderu Mix
                string? modelNameForThisMixFile = GetModelNameFromFilePathInLibrary(imgPathFromMix, mixedFolders);
                if (string.IsNullOrEmpty(modelNameForThisMixFile))
                {
                    Interlocked.Increment(ref filesProcessedCount);
                    progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref filesProcessedCount), TotalItems = (int)totalFilesToProcess, StatusMessage = $"Pomijanie {Path.GetFileName(imgPathFromMix)} (nie można ustalić modelki)..." });
                    continue;
                }

                var modelVM = allModelsCurrentlyInList.FirstOrDefault(m => m.ModelName.Equals(modelNameForThisMixFile, StringComparison.OrdinalIgnoreCase));
                if (modelVM == null || !modelVM.HasCharacterProfiles) // Jeśli modelka nie ma profili, nie ma do czego sugerować
                {
                    Interlocked.Increment(ref filesProcessedCount);
                    progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref filesProcessedCount), TotalItems = (int)totalFilesToProcess, StatusMessage = $"Pomijanie {Path.GetFileName(imgPathFromMix)} (brak profili dla modelki {modelNameForThisMixFile})..." });
                    continue;
                }
                string modelPath = Path.Combine(LibraryRootPath, modelVM.ModelName); // Ścieżka do folderu tej modelki

                processingTasks.Add(Task.Run(async () =>
                {
                    // Użyj ProcessSingleImageForModelSpecificScanAsync, bo logika jest ta sama, tylko kontekst wywołania inny
                    var res = await ProcessSingleImageForModelSpecificScanAsync(imgPathFromMix, modelVM, modelPath, token, allCollectedSuggestionsGlobalConcurrent, alreadySuggestedDuplicates, progress);
                    Interlocked.Increment(ref filesProcessedCount);
                    progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref filesProcessedCount), TotalItems = (int)totalFilesToProcess, StatusMessage = $"Skan globalny: {Path.GetFileName(imgPathFromMix)} ({Interlocked.Read(ref filesProcessedCount)}/{totalFilesToProcess})..." });
                    return res;
                }, token));
            }

            var taskResults = await Task.WhenAll(processingTasks);
            token.ThrowIfCancellationRequested();

            foreach (var r in taskResults) { filesWithEmbeddingsCount += r.FilesWithEmbeddingsIncrement; autoActionsCount += r.AutoActionsIncrement; if (r.ProfileDataChanged) anyProfileDataChanged = true; }

            var allCollectedSuggestionsGlobal = allCollectedSuggestionsGlobalConcurrent.ToList();
            SimpleFileLogger.LogHighLevelInfo($"ExecuteSuggestImagesAsync: Globalne skanowanie zakończone. Przeskanowano: {totalFilesToProcess}, Z embeddingami: {filesWithEmbeddingsCount}, Auto-akcje: {autoActionsCount}, Sugestie: {allCollectedSuggestionsGlobal.Count}. Profile zmodyfikowane: {anyProfileDataChanged}. Token: {token.GetHashCode()}");
            progress.Report(new ProgressReport { ProcessedItems = (int)totalFilesToProcess, TotalItems = (int)totalFilesToProcess, StatusMessage = "Globalne wyszukiwanie sugestii zakończone." });

            // Zapisz wszystkie zebrane sugestie jako _lastModelSpecificSuggestions, ale zaznacz, że to skan globalny
            _lastModelSpecificSuggestions = new List<Models.ProposedMove>(allCollectedSuggestionsGlobal);
            _lastScannedModelNameForSuggestions = null; // null oznacza, że cache zawiera sugestie globalne

            StatusMessage = $"Globalne wyszukiwanie zakończone: {autoActionsCount} auto-akcji, {allCollectedSuggestionsGlobal.Count} wszystkich sugestii.";

            if (anyProfileDataChanged)
            {
                SimpleFileLogger.LogHighLevelInfo($"ExecuteSuggestImagesAsync: Zmiany w profilach podczas globalnego skanowania. Odświeżanie listy profili.");
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token, progress);
                _isRefreshingProfilesPostMove = false;
            }
            RefreshPendingSuggestionCountsFromCache(); // Odśwież UI z nowymi licznikami

            string completionMessage = StatusMessage;
            if (allCollectedSuggestionsGlobal.Any()) completionMessage += " Użyj menu kontekstowego na profilach postaci, aby przejrzeć i zastosować sugestie.";
            else if (autoActionsCount == 0 && totalFilesToProcess > 0) completionMessage = "Globalne wyszukiwanie nie znalazło żadnych nowych sugestii ani nie wykonało automatycznych akcji.";
            else if (totalFilesToProcess == 0) completionMessage = "Nie znaleziono żadnych plików w folderach Mix do globalnego skanowania.";
            MessageBox.Show(completionMessage, "Globalne Wyszukiwanie Sugestii Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Pomocnicza metoda do ustalenia nazwy modelki na podstawie ścieżki pliku w folderze Mix
        private string? GetModelNameFromFilePathInLibrary(string filePath, HashSet<string> mixedFolderNames)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(LibraryRootPath) || !filePath.StartsWith(LibraryRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return null; // Poza biblioteką
            }

            // Ścieżka relatywna do LibraryRootPath
            string relativePath = filePath.Substring(LibraryRootPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Podziel ścieżkę na segmenty
            var pathParts = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            if (pathParts.Length >= 2) // Oczekujemy co najmniej ModelName/MixFolderName/plik.jpg
            {
                // Pierwszy segment to nazwa modelki
                string modelNameCandidate = pathParts[0];
                // Drugi segment to nazwa folderu wewnątrz folderu modelki
                string folderInsideModel = pathParts[1];

                // Sprawdź, czy ten drugi segment jest jednym ze zdefiniowanych folderów "Mix"
                if (mixedFolderNames.Contains(folderInsideModel))
                {
                    return modelNameCandidate; // Zwróć nazwę modelki
                }
            }
            // Jeśli plik jest bezpośrednio w folderze modelki, ale to nie jest folder Mix, to nie jest źródłem sugestii w tej logice
            return null;
        }


        // Metoda do sprawdzania sugestii dla konkretnej postaci (już istnieje w Twoim pliku)
        // Ta metoda może być teraz mniej potrzebna, jeśli nowe okno "Zarządzaj sugestiami" przejmuje jej rolę.
        private async Task ExecuteCheckCharacterSuggestionsAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            var charProfileForSuggestions = (parameter as CategoryProfile) ?? SelectedProfile;
            if (charProfileForSuggestions == null) { StatusMessage = "Błąd: Wybierz profil postaci do sprawdzenia sugestii."; MessageBox.Show(StatusMessage, "Brak Wyboru Profilu", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            SimpleFileLogger.LogHighLevelInfo($"CheckCharacterSuggestions: Rozpoczęto sprawdzanie dla profilu '{charProfileForSuggestions.CategoryName}'. Token: {token.GetHashCode()}");
            progress.Report(new ProgressReport { OperationName = $"Sugestie dla '{charProfileForSuggestions.CategoryName}'", StatusMessage = $"Sprawdzanie dostępnych sugestii...", IsIndeterminate = true });
            token.ThrowIfCancellationRequested();

            string modelName = _profileService.GetModelNameFromCategory(charProfileForSuggestions.CategoryName);
            var modelVM = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));

            var movesForThisCharacterWindow = new List<Models.ProposedMove>();
            bool anyProfileDataChanged = false;

            // Filtruj sugestie z cache (_lastModelSpecificSuggestions)
            // Sprawdź, czy cache dotyczy tej modelki (jeśli _lastScannedModelNameForSuggestions jest ustawione) LUB czy jest to cache globalny (null)
            if (_lastModelSpecificSuggestions.Any() &&
                (string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) || _lastScannedModelNameForSuggestions.Equals(modelName, StringComparison.OrdinalIgnoreCase) || _lastScannedModelNameForSuggestions == "__CACHE_CLEARED__"))
            {
                movesForThisCharacterWindow = _lastModelSpecificSuggestions
                    .Where(m => m.TargetCategoryProfileName.Equals(charProfileForSuggestions.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                                m.Similarity >= SuggestionSimilarityThreshold) // Uwzględnij próg
                    .ToList();
                SimpleFileLogger.Log($"CheckCharacterSuggestions: Użyto cache sugestii ({_lastModelSpecificSuggestions.Count} wszystkich). Dla profilu '{charProfileForSuggestions.CategoryName}' znaleziono {movesForThisCharacterWindow.Count} pasujących sugestii (próg {SuggestionSimilarityThreshold:P0}).");
            }
            else
            {
                SimpleFileLogger.Log($"CheckCharacterSuggestions: Brak pasującego cache sugestii dla modelki '{modelName}' lub cache jest pusty.");
            }

            progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = movesForThisCharacterWindow.Count, StatusMessage = $"Znaleziono {movesForThisCharacterWindow.Count} sugestii dla '{charProfileForSuggestions.CategoryName}'." });

            if (!movesForThisCharacterWindow.Any())
            {
                StatusMessage = $"Brak nowych sugestii (spełniających próg {SuggestionSimilarityThreshold:P0}) dla profilu '{charProfileForSuggestions.CategoryName}'.";
                MessageBox.Show(StatusMessage, "Brak Sugestii", MessageBoxButton.OK, MessageBoxImage.Information);
                var uiProfile = modelVM?.CharacterProfiles.FirstOrDefault(cp => cp.CategoryName == charProfileForSuggestions.CategoryName);
                if (uiProfile != null) uiProfile.PendingSuggestionsCount = 0; // Zaktualizuj UI
                progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Brak sugestii." }); // Zakończ postęp
                return;
            }
            token.ThrowIfCancellationRequested();

            // Pokaż okno PreviewChanges (to jest "stare" okno podglądu)
            bool? outcome = false;
            List<Models.ProposedMove> approved = new List<Models.ProposedMove>();
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
                progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = approved.Count, StatusMessage = $"Stosowanie {approved.Count} zatwierdzonych zmian..." });
                if (await InternalHandleApprovedMovesAsync(approved, modelVM, charProfileForSuggestions, token, progress)) anyProfileDataChanged = true;
                // Usuń zastosowane sugestie z głównej listy cache
                _lastModelSpecificSuggestions.RemoveAll(s => approved.Any(ap => ap.SourceImage.FilePath == s.SourceImage.FilePath));
            }
            else if (outcome == false) // Użytkownik anulował
            {
                StatusMessage = $"Anulowano stosowanie sugestii dla '{charProfileForSuggestions.CategoryName}'.";
            }

            if (anyProfileDataChanged)
            {
                SimpleFileLogger.LogHighLevelInfo($"CheckCharacterSuggestionsAsync: Zmiany w profilach dla '{charProfileForSuggestions.CategoryName}'. Odświeżanie listy profili.");
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token, progress);
                _isRefreshingProfilesPostMove = false;
            }
            RefreshPendingSuggestionCountsFromCache(); // Odśwież liczniki w UI
            progress.Report(new ProgressReport { ProcessedItems = movesForThisCharacterWindow.Count, TotalItems = movesForThisCharacterWindow.Count, StatusMessage = "Sprawdzanie sugestii zakończone." });
        }

        // Metoda do odświeżania liczników sugestii w UI (już istnieje w Twoim pliku)
        private void RefreshPendingSuggestionCountsFromCache()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Najpierw wyzeruj wszystkie liczniki
                foreach (var mVM_iter in HierarchicalProfilesList)
                {
                    mVM_iter.PendingSuggestionsCount = 0;
                    foreach (var cp_iter in mVM_iter.CharacterProfiles)
                    {
                        cp_iter.PendingSuggestionsCount = 0;
                    }
                }

                List<Models.ProposedMove> relevantSuggestions;
                lock (_lastModelSpecificSuggestions) // Synchronizacja dostępu do listy
                {
                    relevantSuggestions = _lastModelSpecificSuggestions
                       .Where(sugg => sugg.Similarity >= SuggestionSimilarityThreshold)
                       .ToList();
                }


                if (relevantSuggestions.Any())
                {
                    if (!string.IsNullOrEmpty(_lastScannedModelNameForSuggestions) && _lastScannedModelNameForSuggestions != "__CACHE_CLEARED__")
                    {
                        // Cache dotyczy konkretnej modelki
                        var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(_lastScannedModelNameForSuggestions, StringComparison.OrdinalIgnoreCase));
                        if (modelToUpdate != null)
                        {
                            int totalForModel = 0;
                            foreach (var cp_ui in modelToUpdate.CharacterProfiles)
                            {
                                cp_ui.PendingSuggestionsCount = relevantSuggestions.Count(sugg => sugg.TargetCategoryProfileName.Equals(cp_ui.CategoryName, StringComparison.OrdinalIgnoreCase));
                                totalForModel += cp_ui.PendingSuggestionsCount;
                            }
                            modelToUpdate.PendingSuggestionsCount = totalForModel;
                            SimpleFileLogger.Log($"RefreshPendingCounts (Specific model '{_lastScannedModelNameForSuggestions}'): Total pending for model: {totalForModel}. Relevant suggestions in cache for this model: {relevantSuggestions.Count(sugg => _profileService.GetModelNameFromCategory(sugg.TargetCategoryProfileName) == modelToUpdate.ModelName)}");
                        }
                    }
                    else if (_lastScannedModelNameForSuggestions != "__CACHE_CLEARED__") // Cache jest globalny (null) lub był wyczyszczony i ponownie zapełniony globalnie
                    {
                        SimpleFileLogger.Log($"RefreshPendingCounts (Global cache): Processing {relevantSuggestions.Count} relevant suggestions.");
                        var suggestionsByModel = relevantSuggestions.GroupBy(sugg => _profileService.GetModelNameFromCategory(sugg.TargetCategoryProfileName));
                        foreach (var group in suggestionsByModel)
                        {
                            var modelToUpdate = HierarchicalProfilesList.FirstOrDefault(m => m.ModelName.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
                            if (modelToUpdate != null)
                            {
                                int totalForModel = 0;
                                foreach (var cp_ui in modelToUpdate.CharacterProfiles)
                                {
                                    cp_ui.PendingSuggestionsCount = group.Count(sugg => sugg.TargetCategoryProfileName.Equals(cp_ui.CategoryName, StringComparison.OrdinalIgnoreCase));
                                    totalForModel += cp_ui.PendingSuggestionsCount;
                                }
                                modelToUpdate.PendingSuggestionsCount = totalForModel;
                                SimpleFileLogger.Log($"RefreshPendingCounts (Global cache): Model '{modelToUpdate.ModelName}' updated with {totalForModel} pending suggestions.");
                            }
                        }
                    }
                }
                else // Brak relevantSuggestions
                {
                    SimpleFileLogger.Log("RefreshPendingCounts: Brak istotnych sugestii w cache (po filtracji progiem), wszystkie liczniki pozostają 0.");
                }
                CommandManager.InvalidateRequerySuggested(); // Odśwież CanExecute dla komend
            });
        }

        // Metoda do obsługi zatwierdzonych ruchów (już istnieje w Twoim pliku)
        private async Task<bool> InternalHandleApprovedMovesAsync(List<Models.ProposedMove> approvedMoves, ModelDisplayViewModel? specificModelVM, CategoryProfile? specificCharacterProfile, CancellationToken token, IProgress<ProgressReport> progress)
        {
            int successfulMoves = 0, copyErrors = 0, deleteErrors = 0, skippedQuality = 0, skippedOther = 0;
            bool anyProfileActuallyModified = false;
            var processedSourcePathsForThisBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Śledź przetworzone pliki źródłowe w tej partii

            int totalMovesToProcess = approvedMoves.Count;
            progress.Report(new ProgressReport { OperationName = "Stosowanie Zmian", ProcessedItems = 0, TotalItems = totalMovesToProcess, StatusMessage = $"Stosowanie {totalMovesToProcess} zatwierdzonych zmian..." });

            for (int i = 0; i < approvedMoves.Count; i++)
            {
                var move = approvedMoves[i];
                token.ThrowIfCancellationRequested();
                progress.Report(new ProgressReport { ProcessedItems = i + 1, TotalItems = totalMovesToProcess, StatusMessage = $"Przenoszenie: {Path.GetFileName(move.SourceImage.FilePath)} (Akcja: {move.Action})..." });

                string sourcePath = move.SourceImage.FilePath;
                string targetPath = move.ProposedTargetPath; // To jest proponowana ścieżka (może wymagać unikalnej nazwy)
                string originalTargetDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty; // Folder docelowy
                var actionType = move.Action;
                bool operationSuccess = false;
                bool shouldDeleteSourceAfterCopy = false; // Flaga, czy źródło powinno być usunięte
                string finalTargetPath = targetPath; // Ścieżka, pod którą plik faktycznie zostanie zapisany


                try
                {
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    { SimpleFileLogger.LogWarning($"[HandleApproved] Plik źródłowy nie istnieje: '{sourcePath}'. Pomijanie."); skippedOther++; continue; }
                    if (string.IsNullOrEmpty(originalTargetDirectory))
                    { SimpleFileLogger.LogWarning($"[HandleApproved] Błędny folder docelowy dla '{targetPath}'. Pomijanie."); skippedOther++; continue; }

                    Directory.CreateDirectory(originalTargetDirectory); // Upewnij się, że folder docelowy istnieje


                    switch (actionType)
                    {
                        case ProposedMoveActionType.CopyNew:
                        case ProposedMoveActionType.ConflictKeepBoth: // Traktujemy podobnie, generując unikalną nazwę
                            // Jeśli plik docelowy już istnieje (i nie jest tym samym plikiem), wygeneruj unikalną nazwę
                            if (File.Exists(finalTargetPath) && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(finalTargetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                finalTargetPath = GenerateUniqueTargetPath(originalTargetDirectory, Path.GetFileName(sourcePath), actionType == ProposedMoveActionType.ConflictKeepBoth ? "_conflict_approved" : "_new_approved");
                            }
                            else if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(finalTargetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                // Plik źródłowy jest już w miejscu docelowym. Nic nie rób z plikiem.
                                operationSuccess = true;
                                shouldDeleteSourceAfterCopy = false; // Nie usuwaj, bo to ten sam plik
                                SimpleFileLogger.Log($"[HandleApproved] Plik '{sourcePath}' jest już w miejscu docelowym. Akcja {actionType} pominięta dla pliku.");
                                break;
                            }
                            await Task.Run(() => File.Copy(sourcePath, finalTargetPath, false), token); // Kopiuj bez nadpisywania (bo nazwa jest już unikalna lub nowa)
                            operationSuccess = true;
                            shouldDeleteSourceAfterCopy = true; // Po skopiowaniu, oryginał (z Mix) powinien być usunięty
                            break;

                        case ProposedMoveActionType.OverwriteExisting:
                            // finalTargetPath pozostaje move.ProposedTargetPath
                            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(finalTargetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                operationSuccess = true; shouldDeleteSourceAfterCopy = false; // Ten sam plik, nic do zrobienia
                                SimpleFileLogger.Log($"[HandleApproved] Plik '{sourcePath}' jest już w miejscu docelowym. Akcja {actionType} pominięta.");
                                break;
                            }
                            await Task.Run(() => File.Copy(sourcePath, finalTargetPath, true), token); // Nadpisz istniejący
                            operationSuccess = true;
                            shouldDeleteSourceAfterCopy = true;
                            break;

                        case ProposedMoveActionType.KeepExistingDeleteSource: // Użytkownik wybrał, aby zachować istniejący, a usunąć źródłowy
                            // Sprawdź, czy plik docelowy faktycznie istnieje
                            if (!File.Exists(finalTargetPath) && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(finalTargetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                // Jeśli plik docelowy nie istnieje (a powinien być zachowany), to jest to błąd logiki lub danych
                                SimpleFileLogger.LogWarning($"[HandleApproved] Akcja KeepExisting, ale plik docelowy '{finalTargetPath}' nie istnieje. Pomijam usuwanie źródła.");
                                skippedOther++; operationSuccess = false; shouldDeleteSourceAfterCopy = false;
                                break;
                            }
                            // Jeśli plik źródłowy i docelowy to ten sam plik, nic nie rób (już jest "zachowany")
                            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(finalTargetPath), StringComparison.OrdinalIgnoreCase))
                            {
                                operationSuccess = true; shouldDeleteSourceAfterCopy = false;
                                SimpleFileLogger.Log($"[HandleApproved] Akcja KeepExisting, plik źródłowy i docelowy to ten sam plik. Nic do zrobienia.");
                            }
                            else // Pliki są różne, więc usuń źródłowy
                            {
                                operationSuccess = true; // Operacja "zachowania istniejącego" powiodła się
                                shouldDeleteSourceAfterCopy = true; // Usuń źródłowy
                                SimpleFileLogger.Log($"[HandleApproved] Akcja KeepExisting. Zachowano '{finalTargetPath}', źródło '{sourcePath}' zostanie usunięte.");
                            }
                            // Jeśli jakość była powodem, można to inaczej logować
                            // if (move.Reason == "Quality") skippedQuality++; else skippedOther++;
                            break;

                        default:
                            SimpleFileLogger.LogWarning($"[HandleApproved] Nieznana lub nieobsługiwana akcja: {actionType} dla pliku '{sourcePath}'.");
                            skippedOther++; operationSuccess = false;
                            break;
                    }
                    token.ThrowIfCancellationRequested();

                    if (operationSuccess)
                    {
                        successfulMoves++;
                        processedSourcePathsForThisBatch.Add(sourcePath); // Dodaj oryginalną ścieżkę źródłową do przetworzonych

                        string? oldPathForProfileUpdate = null;
                        string? newPathForProfileUpdate = finalTargetPath; // Domyślnie nowa ścieżka to miejsce docelowe kopii

                        if (shouldDeleteSourceAfterCopy && File.Exists(sourcePath) &&
                            !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(finalTargetPath), StringComparison.OrdinalIgnoreCase))
                        {
                            oldPathForProfileUpdate = sourcePath; // Oryginalne źródło zostanie usunięte
                            try
                            {
                                await Task.Run(() => File.Delete(sourcePath), token);
                                SimpleFileLogger.Log($"[HandleApproved] Usunięto plik źródłowy: '{sourcePath}'.");
                            }
                            catch (Exception exDel)
                            {
                                deleteErrors++;
                                SimpleFileLogger.LogError($"[HandleApproved] Błąd podczas usuwania pliku źródłowego '{sourcePath}'.", exDel);
                            }
                        }
                        else if (shouldDeleteSourceAfterCopy && string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(finalTargetPath), StringComparison.OrdinalIgnoreCase))
                        {
                            // Jeśli źródło i cel to ten sam plik, a mieliśmy go "usunąć po skopiowaniu",
                            // oznacza to, że plik po prostu zostaje w profilu.
                            // Nie ma tu oldPath do usunięcia z innych profili, a newPath to ten sam plik.
                            oldPathForProfileUpdate = null;
                            newPathForProfileUpdate = sourcePath; // Plik pozostaje pod tą ścieżką w profilu docelowym
                        }
                        else if (!shouldDeleteSourceAfterCopy && string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(finalTargetPath), StringComparison.OrdinalIgnoreCase))
                        {
                            // Przypadek, gdy plik źródłowy był już w docelowym folderze, np. akcja CopyNew, ale plik już tam był.
                            // Wtedy oldPath i newPath wskazują na ten sam plik, który ma być w profilu docelowym.
                            oldPathForProfileUpdate = null; // Nic nie jest usuwane z innych profili
                            newPathForProfileUpdate = sourcePath; // Ta ścieżka ma być w profilu docelowym
                        }


                        // Zaktualizuj profile: usuń oldPath (jeśli jest), dodaj newPath do profilu docelowego
                        if (await HandleFileMovedOrDeletedUpdateProfilesAsync(oldPathForProfileUpdate, newPathForProfileUpdate, move.TargetCategoryProfileName, token, progress))
                        {
                            anyProfileActuallyModified = true;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; } // Przekaż dalej
                catch (Exception exCopy)
                {
                    copyErrors++;
                    SimpleFileLogger.LogError($"[HandleApproved] Błąd operacji na pliku '{sourcePath}' -> '{finalTargetPath}'. Akcja: {actionType}.", exCopy); // POPRAWKA: originalTarget -> finalTargetPath
                }
            }
            token.ThrowIfCancellationRequested();

            // Usuń przetworzone sugestie z głównej listy cache, jeśli trzeba
            if (processedSourcePathsForThisBatch.Any())
            {
                lock (_lastModelSpecificSuggestions) // Synchronizacja dostępu do listy
                {
                    int removedCount = _lastModelSpecificSuggestions.RemoveAll(s => processedSourcePathsForThisBatch.Contains(s.SourceImage.FilePath));
                    SimpleFileLogger.Log($"[HandleApproved] Usunięto {removedCount} zastosowanych sugestii z pamięci podręcznej.");
                }
            }

            progress.Report(new ProgressReport { ProcessedItems = totalMovesToProcess, TotalItems = totalMovesToProcess, StatusMessage = $"Zakończono stosowanie {totalMovesToProcess} zmian." });
            string summaryMessage = $"Zakończono operację: {successfulMoves} udanych ruchów, {skippedQuality} pominiętych (jakość), {skippedOther} pominiętych (inne), {copyErrors} błędów kopiowania, {deleteErrors} błędów usuwania.";
            StatusMessage = summaryMessage;
            // Pokaż podsumowanie, jeśli były jakieś błędy lub znaczące pominięcia, lub jeśli użytkownik tego oczekuje
            if (successfulMoves > 0 || copyErrors > 0 || deleteErrors > 0 || skippedOther > 0 || skippedQuality > 0)
            {
                MessageBox.Show(summaryMessage, "Operacja Zastosowania Sugestii Zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return anyProfileActuallyModified;
        }

        // Metoda do generowania unikalnej ścieżki (już istnieje w Twoim pliku)
        private string GenerateUniqueTargetPath(string targetDirectory, string originalFileNameWithExtension, string suffixIfConflict = "_conflict")
        {
            string baseName = Path.GetFileNameWithoutExtension(originalFileNameWithExtension);
            string extension = Path.GetExtension(originalFileNameWithExtension);
            string finalPath = Path.Combine(targetDirectory, originalFileNameWithExtension);
            int counter = 1;
            // Dopóki plik o proponowanej nazwie istnieje, próbuj dodać licznik
            while (File.Exists(finalPath))
            {
                string newFileName = $"{baseName}{suffixIfConflict}{counter}{extension}";
                finalPath = Path.Combine(targetDirectory, newFileName);
                counter++;
                if (counter > 9999) // Zabezpieczenie przed nieskończoną pętlą (mało prawdopodobne)
                {
                    // Jeśli licznik jest zbyt duży, użyj GUID, aby zagwarantować unikalność
                    newFileName = $"{baseName}_{Guid.NewGuid():N}{extension}";
                    finalPath = Path.Combine(targetDirectory, newFileName);
                    SimpleFileLogger.LogWarning($"GenerateUniqueTargetPath: Osiągnięto limit liczników dla '{originalFileNameWithExtension}', użyto GUID: {finalPath}");
                    break;
                }
            }
            return finalPath;
        }

        // Metoda do usuwania całej modelki (już istnieje w Twoim pliku)
        private async Task ExecuteRemoveModelTreeAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            bool changed = false;
            if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Wybierz modelkę do usunięcia."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            progress.Report(new ProgressReport { OperationName = $"Usuwanie Modelki '{modelVM.ModelName}'", StatusMessage = $"Przygotowywanie do usunięcia wszystkich profili modelki '{modelVM.ModelName}'...", IsIndeterminate = true });
            token.ThrowIfCancellationRequested();

            if (MessageBox.Show($"Czy na pewno chcesz usunąć wszystkie profile dla modelki '{modelVM.ModelName}'?\nTa operacja NIE usunie plików graficznych z dysku, jedynie definicje profili.", "Potwierdź Usunięcie Modelki", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                progress.Report(new ProgressReport { StatusMessage = $"Usuwanie wszystkich profili dla '{modelVM.ModelName}'..." });
                if (await _profileService.RemoveAllProfilesForModelAsync(modelVM.ModelName, token))
                {
                    StatusMessage = $"Wszystkie profile dla modelki '{modelVM.ModelName}' zostały usunięte.";
                    // Wyczyść cache sugestii, jeśli dotyczył tej modelki
                    if (_lastScannedModelNameForSuggestions == modelVM.ModelName) ClearModelSpecificSuggestionsCache();
                    // Odznacz profil, jeśli należał do usuniętej modelki
                    if (SelectedProfile != null && _profileService.GetModelNameFromCategory(SelectedProfile.CategoryName) == modelVM.ModelName) SelectedProfile = null;
                    changed = true;
                }
                else
                {
                    StatusMessage = $"Nie udało się usunąć wszystkich profili dla '{modelVM.ModelName}'. Sprawdź logi.";
                    // changed pozostaje false, chyba że część operacji się powiodła i zmieniła stan
                }
            }
            else
            {
                StatusMessage = $"Anulowano usuwanie modelki '{modelVM.ModelName}'.";
            }

            if (changed) // Jeśli dokonano zmian, odśwież listę profili
            {
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token, progress);
                _isRefreshingProfilesPostMove = false;
            }
            progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Operacja usuwania modelki zakończona." });
        }

        // Metoda do analizy profili pod kątem podziału (już istnieje w Twoim pliku)
        private async Task ExecuteAnalyzeModelForSplittingAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Wybierz modelkę do analizy."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            token.ThrowIfCancellationRequested();
            int profilesMarkedForSplitSuggestion = 0;
            // Wyzeruj flagi sugestii podziału przed analizą
            await Application.Current.Dispatcher.InvokeAsync(() => { foreach (var cp_ui in modelVM.CharacterProfiles) cp_ui.HasSplitSuggestion = false; });

            var profilesToAnalyze = modelVM.CharacterProfiles.ToList(); // Pracuj na kopii
            progress.Report(new ProgressReport { OperationName = $"Analiza Podziału dla '{modelVM.ModelName}'", ProcessedItems = 0, TotalItems = profilesToAnalyze.Count, StatusMessage = $"Rozpoczynanie analizy {profilesToAnalyze.Count} profili..." });
            int profilesProcessedCount = 0;

            foreach (var characterProfile in profilesToAnalyze)
            {
                token.ThrowIfCancellationRequested();
                const int minImagesToConsiderSplitting = 10; // Minimalna liczba obrazów, aby w ogóle rozważać
                const int minImagesToActivelySuggestSplitting = 20; // Liczba obrazów, powyżej której aktywnie sugerujemy podział

                if (characterProfile.SourceImagePaths == null || characterProfile.SourceImagePaths.Count < minImagesToConsiderSplitting)
                {
                    profilesProcessedCount++;
                    progress.Report(new ProgressReport { ProcessedItems = profilesProcessedCount, TotalItems = profilesToAnalyze.Count, StatusMessage = $"Profil '{characterProfile.CategoryName}' pominięty (za mało obrazów)..." });
                    continue;
                }

                // Sprawdź, ile z tych ścieżek faktycznie istnieje (na wypadek usunięcia plików poza programem)
                int validImageCountInProfile = 0;
                if (characterProfile.SourceImagePaths != null)
                {
                    foreach (var path in characterProfile.SourceImagePaths) if (File.Exists(path)) validImageCountInProfile++;
                }
                token.ThrowIfCancellationRequested();

                if (validImageCountInProfile < minImagesToConsiderSplitting)
                {
                    profilesProcessedCount++;
                    progress.Report(new ProgressReport { ProcessedItems = profilesProcessedCount, TotalItems = profilesToAnalyze.Count, StatusMessage = $"Profil '{characterProfile.CategoryName}' pominięty (za mało istniejących obrazów)..." });
                    continue;
                }

                bool shouldSuggestSplitting = validImageCountInProfile >= minImagesToActivelySuggestSplitting;

                // Znajdź odpowiadający ViewModel w UI i ustaw flagę
                var uiCharacterProfileVM = modelVM.CharacterProfiles.FirstOrDefault(pVM => pVM.CategoryName == characterProfile.CategoryName);
                if (uiCharacterProfileVM != null)
                {
                    uiCharacterProfileVM.HasSplitSuggestion = shouldSuggestSplitting;
                    if (shouldSuggestSplitting) profilesMarkedForSplitSuggestion++;
                }
                SimpleFileLogger.Log($"AnalyzeModelForSplitting: Profil '{characterProfile.CategoryName}', rzeczywista liczba obrazów: {validImageCountInProfile}, sugestia podziału: {shouldSuggestSplitting}.");
                profilesProcessedCount++;
                progress.Report(new ProgressReport { ProcessedItems = profilesProcessedCount, TotalItems = profilesToAnalyze.Count, StatusMessage = $"Analiza profilu '{characterProfile.CategoryName}' (sugestia: {shouldSuggestSplitting})..." });
            }
            token.ThrowIfCancellationRequested();

            StatusMessage = $"Analiza podziału profili dla modelki '{modelVM.ModelName}' zakończona. Znaleziono {profilesMarkedForSplitSuggestion} kandydatów do podziału.";
            MessageBox.Show(StatusMessage, "Analiza Podziału Zakończona", MessageBoxButton.OK, profilesMarkedForSplitSuggestion > 0 ? MessageBoxImage.Information : MessageBoxImage.Information);
            progress.Report(new ProgressReport { ProcessedItems = profilesToAnalyze.Count, TotalItems = profilesToAnalyze.Count, StatusMessage = "Analiza podziału profili zakończona." });
        }

        // Metoda do otwierania okna podziału profilu (już istnieje w Twoim pliku)
        private async Task ExecuteOpenSplitProfileDialogAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            if (!(parameter is CategoryProfile originalCP)) { StatusMessage = "Błąd: Wybierz profil do podziału."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            token.ThrowIfCancellationRequested();
            bool anyChangesMade = false; // Flaga, czy dokonano zmian
            progress.Report(new ProgressReport { OperationName = $"Podział Profilu '{originalCP.CategoryName}'", StatusMessage = $"Przygotowywanie danych do podziału profilu...", IsIndeterminate = true });

            var imagesInProfile = new List<ImageFileEntry>();
            if (originalCP.SourceImagePaths != null)
            {
                int totalImages = originalCP.SourceImagePaths.Count;
                int imagesLoaded = 0;
                progress.Report(new ProgressReport { ProcessedItems = imagesLoaded, TotalItems = totalImages, StatusMessage = $"Ładowanie {totalImages} obrazów z profilu '{originalCP.CategoryName}'..." });
                foreach (var path in originalCP.SourceImagePaths)
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(path))
                    {
                        var entry = await _imageMetadataService.ExtractMetadataAsync(path);
                        if (entry != null) imagesInProfile.Add(entry);
                    }
                    imagesLoaded++;
                    progress.Report(new ProgressReport { ProcessedItems = imagesLoaded, TotalItems = totalImages, StatusMessage = $"Załadowano {imagesLoaded}/{totalImages} obrazów..." });
                }
            }
            token.ThrowIfCancellationRequested();

            if (!imagesInProfile.Any())
            {
                StatusMessage = $"Profil '{originalCP.CategoryName}' jest pusty lub nie zawiera istniejących obrazów. Nie można podzielić.";
                MessageBox.Show(StatusMessage, "Pusty Profil", MessageBoxButton.OK, MessageBoxImage.Warning);
                var uiProfile = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == originalCP.CategoryName);
                if (uiProfile != null) uiProfile.HasSplitSuggestion = false; // Usuń flagę sugestii
                progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Profil pusty, podział niemożliwy." });
                return;
            }

            // Prosta logika podziału na dwie grupy (można ją ulepszyć)
            var group1Images = imagesInProfile.Take(imagesInProfile.Count / 2).ToList();
            var group2Images = imagesInProfile.Skip(imagesInProfile.Count / 2).ToList();

            string modelName = _profileService.GetModelNameFromCategory(originalCP.CategoryName);
            string baseCharacterName = _profileService.GetCharacterNameFromCategory(originalCP.CategoryName);
            // Jeśli bazowa nazwa postaci to "General" i jest to główny profil modelki, użyj nazwy modelki jako bazy
            if (baseCharacterName.Equals("General", StringComparison.OrdinalIgnoreCase) &&
                (originalCP.CategoryName.Equals($"{modelName} - General", StringComparison.OrdinalIgnoreCase) || originalCP.CategoryName.Equals(modelName, StringComparison.OrdinalIgnoreCase)))
            {
                baseCharacterName = modelName; // Np. "ModelName - Part 1" zamiast "General - Part 1"
            }

            string suggestedName1 = $"{baseCharacterName} - Part 1";
            string suggestedName2 = $"{baseCharacterName} - Part 2";
            bool? dialogResult = false;
            SplitProfileViewModel? splitVM = null;

            progress.Report(new ProgressReport { StatusMessage = "Oczekiwanie na decyzję użytkownika dotyczącą podziału...", IsIndeterminate = true });
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                splitVM = new SplitProfileViewModel(originalCP, group1Images, group2Images, suggestedName1, suggestedName2);
                var win = new SplitProfileWindow { DataContext = splitVM, Owner = Application.Current.MainWindow };
                win.SetViewModelCloseAction(splitVM); // Umożliwia VM zamknięcie okna
                dialogResult = win.ShowDialog();
            });
            token.ThrowIfCancellationRequested();

            if (dialogResult == true && splitVM != null)
            {
                StatusMessage = $"Rozpoczynanie podziału profilu '{originalCP.CategoryName}'...";
                SimpleFileLogger.LogHighLevelInfo($"SplitProfile: Użytkownik potwierdził podział dla '{originalCP.CategoryName}'. Nowe nazwy: '{splitVM.NewProfile1Name}', '{splitVM.NewProfile2Name}'. Token: {token.GetHashCode()}");

                string fullNewProfile1Name = $"{modelName} - {splitVM.NewProfile1Name}";
                string fullNewProfile2Name = $"{modelName} - {splitVM.NewProfile2Name}";
                var entriesForProfile1 = splitVM.Group1Images.Select(vmImage => vmImage.OriginalImageEntry).ToList();
                var entriesForProfile2 = splitVM.Group2Images.Select(vmImage => vmImage.OriginalImageEntry).ToList();

                // Utwórz foldery dla nowych profili (jeśli jeszcze nie istnieją)
                string profile1Path = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(splitVM.NewProfile1Name));
                string profile2Path = Path.Combine(LibraryRootPath, SanitizeFolderName(modelName), SanitizeFolderName(splitVM.NewProfile2Name));
                Directory.CreateDirectory(profile1Path);
                Directory.CreateDirectory(profile2Path);

                int totalFilesToMove = entriesForProfile1.Count + entriesForProfile2.Count;
                int filesMovedCount = 0;
                progress.Report(new ProgressReport { ProcessedItems = filesMovedCount, TotalItems = totalFilesToMove, StatusMessage = $"Przenoszenie plików do nowych folderów profili..." });

                // Przenieś pliki dla profilu 1
                foreach (var entry in entriesForProfile1)
                {
                    token.ThrowIfCancellationRequested();
                    string newFilePath = Path.Combine(profile1Path, entry.FileName);
                    try { if (!File.Exists(newFilePath) && File.Exists(entry.FilePath)) File.Move(entry.FilePath, newFilePath); if (File.Exists(newFilePath)) entry.FilePath = newFilePath; /* Zaktualizuj ścieżkę w entry */ }
                    catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd przenoszenia pliku '{entry.FilePath}' do '{newFilePath}' dla profilu 1.", ex); }
                    filesMovedCount++; progress.Report(new ProgressReport { ProcessedItems = filesMovedCount, TotalItems = totalFilesToMove, StatusMessage = $"Przeniesiono {filesMovedCount}/{totalFilesToMove} plików..." });
                }
                // Przenieś pliki dla profilu 2
                foreach (var entry in entriesForProfile2)
                {
                    token.ThrowIfCancellationRequested();
                    string newFilePath = Path.Combine(profile2Path, entry.FileName);
                    try { if (!File.Exists(newFilePath) && File.Exists(entry.FilePath)) File.Move(entry.FilePath, newFilePath); if (File.Exists(newFilePath)) entry.FilePath = newFilePath; /* Zaktualizuj ścieżkę w entry */ }
                    catch (Exception ex) { SimpleFileLogger.LogError($"SplitProfile: Błąd przenoszenia pliku '{entry.FilePath}' do '{newFilePath}' dla profilu 2.", ex); }
                    filesMovedCount++; progress.Report(new ProgressReport { ProcessedItems = filesMovedCount, TotalItems = totalFilesToMove, StatusMessage = $"Przeniesiono {filesMovedCount}/{totalFilesToMove} plików..." });
                }
                SimpleFileLogger.Log($"SplitProfile: Zakończono przenoszenie plików.");
                token.ThrowIfCancellationRequested();

                // Wygeneruj nowe profile
                progress.Report(new ProgressReport { StatusMessage = $"Generowanie nowego profilu '{fullNewProfile1Name}'...", IsIndeterminate = true });
                await _profileService.GenerateProfileAsync(fullNewProfile1Name, entriesForProfile1, progress, token);
                token.ThrowIfCancellationRequested();
                progress.Report(new ProgressReport { StatusMessage = $"Generowanie nowego profilu '{fullNewProfile2Name}'...", IsIndeterminate = true });
                await _profileService.GenerateProfileAsync(fullNewProfile2Name, entriesForProfile2, progress, token);
                token.ThrowIfCancellationRequested();

                // Usuń stary profil
                await _profileService.RemoveProfileAsync(originalCP.CategoryName, token);
                anyChangesMade = true;
                StatusMessage = $"Profil '{originalCP.CategoryName}' został pomyślnie podzielony na '{splitVM.NewProfile1Name}' i '{splitVM.NewProfile2Name}'.";

                // Usuń flagę sugestii z UI dla starego profilu (jeśli nadal istnieje w UI przed odświeżeniem)
                var uiOriginalProfile = HierarchicalProfilesList.SelectMany(m => m.CharacterProfiles).FirstOrDefault(p => p.CategoryName == originalCP.CategoryName);
                if (uiOriginalProfile != null) uiOriginalProfile.HasSplitSuggestion = false;
            }
            else // Użytkownik anulował podział
            {
                StatusMessage = $"Podział profilu '{originalCP.CategoryName}' został anulowany przez użytkownika.";
            }

            if (anyChangesMade) // Jeśli dokonano zmian, odśwież listę profili
            {
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token, progress);
                _isRefreshingProfilesPostMove = false;
            }
            progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Operacja podziału profilu zakończona." });
        }

        // Metoda do anulowania bieżącej operacji (już istnieje w Twoim pliku)
        private void ExecuteCancelCurrentOperation(object? parameter)
        {
            SimpleFileLogger.LogHighLevelInfo($"ExecuteCancelCurrentOperation: Próba anulowania. Aktywny CTS: {_activeLongOperationCts != null}. Token CTS: {_activeLongOperationCts?.Token.GetHashCode()}");
            if (_activeLongOperationCts != null && !_activeLongOperationCts.IsCancellationRequested)
            {
                _activeLongOperationCts.Cancel(); // Wyślij sygnał anulowania
                StatusMessage = "Anulowanie operacji..."; // Zaktualizuj status natychmiast
                ProgressStatusText = "Anulowanie operacji...";
                SimpleFileLogger.LogHighLevelInfo("Sygnał anulowania został wysłany do aktywnej operacji.");
            }
            else
            {
                SimpleFileLogger.Log("Brak aktywnej operacji do anulowania lub operacja została już wcześniej anulowana.");
            }
        }

        // Metoda do ładowania miniaturek (już istnieje w Twoim pliku)
        private async Task ExecuteEnsureThumbnailsLoadedAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            // Pobierz listę obrazów do przetworzenia (z parametru lub z głównej listy ImageFiles)
            var imagesToLoadThumbs = (parameter as IEnumerable<ImageFileEntry>)?.ToList() ?? ImageFiles.ToList();
            if (!imagesToLoadThumbs.Any()) { StatusMessage = "Brak obrazów do załadowania miniaturek."; progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Brak obrazów do przetworzenia." }); return; }

            SimpleFileLogger.LogHighLevelInfo($"EnsureThumbnailsLoaded: Rozpoczęto ładowanie miniaturek dla {imagesToLoadThumbs.Count} obrazów. Token: {token.GetHashCode()}");
            progress.Report(new ProgressReport { OperationName = "Ładowanie Miniaturek", ProcessedItems = 0, TotalItems = imagesToLoadThumbs.Count, StatusMessage = $"Rozpoczynanie ładowania {imagesToLoadThumbs.Count} miniaturek..." });

            var tasks = new List<Task>();
            using var thumbnailSemaphore = new SemaphoreSlim(10, 10); // Ogranicz liczbę współbieżnych ładowań miniaturek
            long thumbsLoadedCount = 0; // Użyj long dla Interlocked

            foreach (var entry in imagesToLoadThumbs)
            {
                token.ThrowIfCancellationRequested();
                // Ładuj miniaturkę tylko jeśli jeszcze nie została załadowana i nie jest w trakcie ładowania
                if (entry.Thumbnail == null && !entry.IsLoadingThumbnail)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await thumbnailSemaphore.WaitAsync(token); // Oczekuj na wolne miejsce w semaforze
                        try
                        {
                            if (token.IsCancellationRequested) return; // Sprawdź anulowanie przed operacją
                            await entry.LoadThumbnailAsync(); // Asynchroniczne ładowanie miniaturki
                            Interlocked.Increment(ref thumbsLoadedCount);
                            progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref thumbsLoadedCount), TotalItems = imagesToLoadThumbs.Count, StatusMessage = $"Załadowano miniaturkę dla: {Path.GetFileName(entry.FilePath)} ({Interlocked.Read(ref thumbsLoadedCount)}/{imagesToLoadThumbs.Count})" });
                        }
                        finally
                        {
                            thumbnailSemaphore.Release(); // Zwolnij miejsce w semaforze
                        }
                    }, token));
                }
                else // Miniaturka już jest lub jest w trakcie ładowania
                {
                    Interlocked.Increment(ref thumbsLoadedCount); // Zlicz jako "przetworzony"
                    progress.Report(new ProgressReport { ProcessedItems = (int)Interlocked.Read(ref thumbsLoadedCount), TotalItems = imagesToLoadThumbs.Count, StatusMessage = $"Miniaturka dla {Path.GetFileName(entry.FilePath)} już istnieje lub jest ładowana." });
                }
            }
            await Task.WhenAll(tasks); // Poczekaj na zakończenie wszystkich zadań ładowania
            token.ThrowIfCancellationRequested();

            // Podsumowanie
            int finalActuallyLoadedCount = imagesToLoadThumbs.Count(img => img.Thumbnail != null); // Ile faktycznie ma miniaturkę
            StatusMessage = $"Zakończono ładowanie miniaturek. Załadowano: {finalActuallyLoadedCount} z {imagesToLoadThumbs.Count}.";
            progress.Report(new ProgressReport { ProcessedItems = finalActuallyLoadedCount, TotalItems = imagesToLoadThumbs.Count, StatusMessage = StatusMessage });
            SimpleFileLogger.LogHighLevelInfo(StatusMessage);
        }

        // Metoda do usuwania duplikatów w modelu (już istnieje w Twoim pliku, poprawiona pod kątem Interlocked)
        private async Task ExecuteRemoveDuplicatesInModelAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nieprawidłowy parametr. Oczekiwano ModelDisplayViewModel."; return; }
            string modelName = modelVM.ModelName;
            progress.Report(new ProgressReport { OperationName = $"Usuwanie Duplikatów dla '{modelName}'", StatusMessage = $"Przygotowywanie do usuwania duplikatów...", IsIndeterminate = true });

            if (MessageBox.Show($"Czy na pewno chcesz usunąć duplikaty obrazów dla modelki '{modelName}'?\nOperacja pozostawi tylko najlepszą jakościowo wersję każdego obrazu i usunie pozostałe pliki z dysku.", "Potwierdź Usunięcie Duplikatów", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            { StatusMessage = "Anulowano usuwanie duplikatów."; progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Anulowano." }); return; }

            SimpleFileLogger.LogHighLevelInfo($"[RemoveDuplicatesInModel] Rozpoczęto usuwanie duplikatów dla modelki: {modelName}. Token: {token.GetHashCode()}");
            long totalDuplicatesRemovedThisOperation = 0; // Użyj long
            bool anyProfilesActuallyChanged = false;

            var profilesSnapshot = modelVM.CharacterProfiles.ToList(); // Pracuj na kopii listy profili
            int totalProfilesToScan = profilesSnapshot.Count;
            int profilesProcessedCount = 0;
            progress.Report(new ProgressReport { ProcessedItems = profilesProcessedCount, TotalItems = totalProfilesToScan, StatusMessage = $"Skanowanie {totalProfilesToScan} profili postaci dla '{modelName}'..." });

            foreach (var characterProfile in profilesSnapshot)
            {
                token.ThrowIfCancellationRequested();
                profilesProcessedCount++;
                progress.Report(new ProgressReport { ProcessedItems = profilesProcessedCount, TotalItems = totalProfilesToScan, StatusMessage = $"Przetwarzanie profilu: {characterProfile.CategoryName} ({profilesProcessedCount}/{totalProfilesToScan})..." });

                if (characterProfile.SourceImagePaths == null || characterProfile.SourceImagePaths.Count < 2) continue; // Potrzebujemy co najmniej 2 obrazów do porównania

                var entriesInProfileConcurrentBag = new ConcurrentBag<ImageFileEntry>();
                bool hadMissingFiles = false; // Flaga, czy w profilu były brakujące pliki

                // Użyj long dla Interlocked
                long imagesMetadataLoadedCount = 0;
                int totalImagesInThisProfile = characterProfile.SourceImagePaths.Count;

                // Załaduj metadane i embeddingi dla wszystkich obrazów w profilu
                var metadataAndEmbeddingTasks = characterProfile.SourceImagePaths.Select(imgPath => Task.Run(async () => {
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(imgPath))
                    {
                        SimpleFileLogger.LogWarning($"[RDIM] Plik '{imgPath}' z profilu '{characterProfile.CategoryName}' nie istnieje.");
                        lock (_profileChangeLock) hadMissingFiles = true; // Zaznacz, że były brakujące pliki
                        Interlocked.Increment(ref imagesMetadataLoadedCount); // Zlicz jako przetworzony
                        return; // Nie dodawaj do listy
                    }
                    var entry = await _imageMetadataService.ExtractMetadataAsync(imgPath);
                    if (entry != null)
                    {
                        await _embeddingSemaphore.WaitAsync(token); // Ogranicz dostęp do serwisu embeddingów
                        try
                        {
                            if (token.IsCancellationRequested) return;
                            entry.FeatureVector = await _profileService.GetImageEmbeddingAsync(entry, token); // Pobierz embedding
                        }
                        finally { _embeddingSemaphore.Release(); }

                        if (entry.FeatureVector != null) entriesInProfileConcurrentBag.Add(entry);
                        else SimpleFileLogger.LogWarning($"[RDIM] Nie udało się uzyskać embeddingu dla pliku '{entry.FilePath}'. Pomijanie w analizie duplikatów.");
                    }
                    Interlocked.Increment(ref imagesMetadataLoadedCount); // Zlicz jako przetworzony
                }, token)).ToList();
                await Task.WhenAll(metadataAndEmbeddingTasks);
                token.ThrowIfCancellationRequested();

                var validEntriesWithEmbeddings = entriesInProfileConcurrentBag.Where(e => e.FeatureVector != null).ToList();
                if (validEntriesWithEmbeddings.Count < 2) // Jeśli po załadowaniu embeddingów mamy mniej niż 2 obrazy
                {
                    if (hadMissingFiles) // Jeśli były brakujące pliki, profil mógł się zmienić
                    {
                        // Zregeneruj profil tylko z istniejącymi plikami (bez embeddingów, bo GenerateProfileAsync je doda)
                        var existingEntriesForRegen = characterProfile.SourceImagePaths
                            .Where(File.Exists)
                            .Select(p => _imageMetadataService.ExtractMetadataAsync(p).Result) // Tu można uprościć, jeśli ExtractMetadataAsync jest szybkie
                            .Where(e => e != null)
                            .ToList();
                        if (existingEntriesForRegen != null) // Dodatkowe sprawdzenie nulla
                            await _profileService.GenerateProfileAsync(characterProfile.CategoryName, existingEntriesForRegen, progress, token);
                        lock (_profileChangeLock) anyProfilesActuallyChanged = true;
                    }
                    continue; // Przejdź do następnego profilu
                }

                var filesToRemoveFromDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var processedPathsForDuplicateCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Śledź już sprawdzone ścieżki

                for (int i = 0; i < validEntriesWithEmbeddings.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var currentImage = validEntriesWithEmbeddings[i];
                    if (filesToRemoveFromDisk.Contains(currentImage.FilePath) || processedPathsForDuplicateCheck.Contains(currentImage.FilePath)) continue;

                    var duplicateGroup = new List<ImageFileEntry> { currentImage };
                    for (int j = i + 1; j < validEntriesWithEmbeddings.Count; j++)
                    {
                        token.ThrowIfCancellationRequested();
                        var otherImage = validEntriesWithEmbeddings[j];
                        if (filesToRemoveFromDisk.Contains(otherImage.FilePath) || processedPathsForDuplicateCheck.Contains(otherImage.FilePath)) continue;

                        // Porównaj embeddingi (muszą istnieć, bo filtrowaliśmy wcześniej)
                        if (currentImage.FeatureVector != null && otherImage.FeatureVector != null) // Dodatkowe sprawdzenie nulla
                        {
                            double similarity = Utils.MathUtils.CalculateCosineSimilarity(currentImage.FeatureVector, otherImage.FeatureVector);
                            if (similarity >= DUPLICATE_SIMILARITY_THRESHOLD)
                            {
                                duplicateGroup.Add(otherImage);
                            }
                        }
                    }

                    if (duplicateGroup.Count > 1) // Jeśli znaleziono grupę duplikatów
                    {
                        ImageFileEntry bestImageInGroup = duplicateGroup.First(); // Załóż, że pierwszy jest najlepszy
                        foreach (var imageInGroup in duplicateGroup.Skip(1)) // Porównaj z resztą
                        {
                            if (IsImageBetter(imageInGroup, bestImageInGroup)) bestImageInGroup = imageInGroup;
                        }
                        // Dodaj wszystkie oprócz najlepszego do listy do usunięcia
                        foreach (var imageInGroup in duplicateGroup)
                        {
                            if (!imageInGroup.FilePath.Equals(bestImageInGroup.FilePath, StringComparison.OrdinalIgnoreCase))
                            {
                                filesToRemoveFromDisk.Add(imageInGroup.FilePath);
                            }
                            processedPathsForDuplicateCheck.Add(imageInGroup.FilePath); // Oznacz wszystkie w grupie jako przetworzone
                        }
                    }
                    else // Jeśli nie było duplikatów dla currentImage
                    {
                        processedPathsForDuplicateCheck.Add(currentImage.FilePath);
                    }
                }
                token.ThrowIfCancellationRequested();

                if (filesToRemoveFromDisk.Any())
                {
                    long duplicatesRemovedThisProfile = 0;
                    foreach (var pathToRemove in filesToRemoveFromDisk)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (File.Exists(pathToRemove))
                            {
                                File.Delete(pathToRemove);
                                Interlocked.Increment(ref totalDuplicatesRemovedThisOperation);
                                duplicatesRemovedThisProfile++;
                            }
                        }
                        catch (Exception ex) { SimpleFileLogger.LogError($"[RDIM] Błąd podczas usuwania duplikatu '{pathToRemove}'.", ex); }
                    }
                    // Zregeneruj profil tylko z pozostałymi (najlepszymi) plikami
                    var keptEntries = validEntriesWithEmbeddings
                        .Where(e => !filesToRemoveFromDisk.Contains(e.FilePath) && File.Exists(e.FilePath)) // Upewnij się, że plik nadal istnieje
                        .ToList();
                    await _profileService.GenerateProfileAsync(characterProfile.CategoryName, keptEntries, progress, token);
                    lock (_profileChangeLock) anyProfilesActuallyChanged = true;
                    SimpleFileLogger.LogHighLevelInfo($"[RDIM] Profil '{characterProfile.CategoryName}' zaktualizowany. Usunięto {duplicatesRemovedThisProfile} duplikatów z dysku.");
                }
                else if (hadMissingFiles) // Jeśli nie było duplikatów do usunięcia, ale były brakujące pliki
                {
                    var existingEntriesForRegen = characterProfile.SourceImagePaths
                       .Where(File.Exists)
                       .Select(p => _imageMetadataService.ExtractMetadataAsync(p).Result)
                       .Where(e => e != null)
                       .ToList();
                    if (existingEntriesForRegen != null) // Dodatkowe sprawdzenie nulla
                        await _profileService.GenerateProfileAsync(characterProfile.CategoryName, existingEntriesForRegen, progress, token);
                    lock (_profileChangeLock) anyProfilesActuallyChanged = true;
                }
            } // Koniec pętli po profilach postaci

            token.ThrowIfCancellationRequested();
            progress.Report(new ProgressReport { ProcessedItems = totalProfilesToScan, TotalItems = totalProfilesToScan, StatusMessage = $"Zakończono usuwanie duplikatów dla modelki '{modelName}'." });

            long finalTotalDuplicatesRemoved = Interlocked.Read(ref totalDuplicatesRemovedThisOperation);
            if (finalTotalDuplicatesRemoved > 0 || anyProfilesActuallyChanged)
            {
                StatusMessage = $"Zakończono usuwanie duplikatów dla '{modelName}'. Usunięto: {finalTotalDuplicatesRemoved} plików. Odświeżanie listy profili...";
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token, progress); // Odśwież listę profili
                _isRefreshingProfilesPostMove = false;
                MessageBox.Show($"Usunięto {finalTotalDuplicatesRemoved} duplikatów obrazów dla modelki '{modelName}'.", "Usuwanie Duplikatów Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusMessage = $"Nie znaleziono duplikatów do usunięcia dla modelki '{modelName}'.";
                MessageBox.Show(StatusMessage, "Usuwanie Duplikatów Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // Metoda do automatycznego stosowania wszystkich dopasowań dla modelki (już istnieje w Twoim pliku)
        private async Task ExecuteApplyAllMatchesForModelAsync(object? parameter, CancellationToken token, IProgress<ProgressReport> progress)
        {
            if (!(parameter is ModelDisplayViewModel modelVM)) { StatusMessage = "Błąd: Nieprawidłowy parametr. Oczekiwano ModelDisplayViewModel."; MessageBox.Show(StatusMessage, "Błąd Wyboru", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            string modelName = modelVM.ModelName;
            progress.Report(new ProgressReport { OperationName = $"Stosowanie Wszystkich Dopasowań dla '{modelName}'", StatusMessage = $"Sprawdzanie dostępnych sugestii...", IsIndeterminate = true });

            // Sprawdź, czy cache sugestii dotyczy tej modelki LUB jest globalny
            bool hasRelevantCache = (_lastScannedModelNameForSuggestions == modelVM.ModelName || string.IsNullOrEmpty(_lastScannedModelNameForSuggestions)) &&
                                  _lastModelSpecificSuggestions.Any();

            if (!hasRelevantCache)
            {
                StatusMessage = $"Brak załadowanych sugestii dla modelki '{modelName}' lub sugestie dotyczą innej modelki. Uruchom najpierw 'Szukaj dopasowań dla tej modelki'.";
                MessageBox.Show(StatusMessage, "Brak Sugestii w Pamięci", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Brak sugestii w pamięci." });
                return;
            }

            // Filtruj sugestie, które dotyczą tej modelki i spełniają próg
            var movesToApply = _lastModelSpecificSuggestions
                .Where(m => _profileService.GetModelNameFromCategory(m.TargetCategoryProfileName) == modelName &&
                            m.Similarity >= SuggestionSimilarityThreshold)
                .ToList();

            if (!movesToApply.Any())
            {
                StatusMessage = $"Brak sugestii spełniających próg podobieństwa ({SuggestionSimilarityThreshold:P0}) dla modelki '{modelName}'.";
                MessageBox.Show(StatusMessage, "Brak Odpowiednich Sugestii", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Brak sugestii spełniających próg." });
                // Zaktualizuj liczniki w UI, jeśli były jakieś sugestie poniżej progu
                RefreshPendingSuggestionCountsFromCache();
                return;
            }

            if (MessageBox.Show($"Czy na pewno chcesz automatycznie zastosować {movesToApply.Count} dopasowań (sugestii) dla modelki '{modelName}'?\nPliki zostaną przeniesione zgodnie z sugestiami.", "Potwierdź Automatyczne Stosowanie", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                StatusMessage = "Anulowano automatyczne stosowanie dopasowań.";
                progress.Report(new ProgressReport { ProcessedItems = 1, TotalItems = 1, StatusMessage = "Anulowano." });
                return;
            }

            SimpleFileLogger.LogHighLevelInfo($"[ApplyAllMatchesForModel] Rozpoczęto automatyczne stosowanie {movesToApply.Count} dopasowań dla modelki: {modelName}. Token: {token.GetHashCode()}");

            // InternalHandleApprovedMovesAsync będzie raportować swój własny postęp
            bool anyProfileDataActuallyChanged = await InternalHandleApprovedMovesAsync(new List<Models.ProposedMove>(movesToApply), modelVM, null, token, progress); // Przekaż progress
            token.ThrowIfCancellationRequested();

            if (anyProfileDataActuallyChanged)
            {
                StatusMessage = $"Zakończono automatyczne stosowanie dopasowań dla '{modelName}'. Odświeżanie listy profili...";
                _isRefreshingProfilesPostMove = true;
                await InternalExecuteLoadProfilesAsync(token, progress); // Odśwież listę profili
                _isRefreshingProfilesPostMove = false;
            }
            else
            {
                StatusMessage = $"Zakończono automatyczne stosowanie dopasowań dla '{modelName}'. Brak istotnych zmian w definicjach profili (mogły być np. tylko usunięcia z Mix).";
            }
            RefreshPendingSuggestionCountsFromCache(); // Odśwież liczniki w UI
            MessageBox.Show($"Zastosowano {movesToApply.Count} dopasowań dla modelki '{modelName}'.", "Automatyczne Stosowanie Zakończone", MessageBoxButton.OK, MessageBoxImage.Information);
        }


        // Pomocnicza klasa do porównywania ImageFileEntry po ścieżce (już istnieje w Twoim pliku)
        private class ImageFileEntryPathComparer : IEqualityComparer<ImageFileEntry>
        {
            public bool Equals(ImageFileEntry? x, ImageFileEntry? y) { if (ReferenceEquals(x, y)) return true; if (x is null || y is null) return false; return x.FilePath.Equals(y.FilePath, StringComparison.OrdinalIgnoreCase); }
            public int GetHashCode(ImageFileEntry obj) { return obj.FilePath?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0; }
        }
    }
}