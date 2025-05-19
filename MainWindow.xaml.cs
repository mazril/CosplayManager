// Plik: MainWindow.xaml.cs
using CosplayManager.Models;
using CosplayManager.Services;
using CosplayManager.ViewModels;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;

namespace CosplayManager
{
    public partial class MainWindow : MetroWindow
    {
        private ClipServiceHttpClient? _clipService;
        private ProfileService? _profileServiceInstance;
        private MainWindowViewModel? _viewModelInstance;
        private SettingsService? _settingsServiceInstance;
        private EmbeddingCacheServiceSQLite? _embeddingCacheServiceInstance; // ZMIANA TYPU
        private ImageMetadataService? _imageMetadataService;

        private string PythonExecutablePath = @"C:\Users\GameStation\AppData\Local\Programs\Python\Python311\python.exe"; // TODO: Przenieœæ do ustawieñ
        private string ClipServerScriptPath = @"C:\Projekt\CosplayManager\Python\clip_server.py"; // TODO: Przenieœæ do ustawieñ

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing_SaveItems;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var statusTextBlock = this.FindName("StatusTextBlock") as TextBlock;
            if (statusTextBlock != null) statusTextBlock.Text = "Inicjalizacja...";

            _settingsServiceInstance = new SettingsService();
            UserSettings? loadedSettings = await _settingsServiceInstance.LoadSettingsAsync();

            // Zastosuj œcie¿ki Pythona i inne ustawienia z loadedSettings, jeœli istniej¹
            // PythonExecutablePath = loadedSettings?.PythonExecutablePath ?? PythonExecutablePath;
            // ClipServerScriptPath = loadedSettings?.ClipServerScriptPath ?? ClipServerScriptPath;
            // SimpleFileLogger.IsDebugLoggingEnabled = loadedSettings?.EnableDebugLogging ?? false; // Ustawione w ViewModel

            if (!File.Exists(PythonExecutablePath))
            {
                MessageBox.Show($"Krytyczny b³¹d: Nie znaleziono interpretera Pythona w: \n{PythonExecutablePath}\n\nSprawdŸ konfiguracjê œcie¿ek.",
                                "B³¹d konfiguracji interpretera Python", MessageBoxButton.OK, MessageBoxImage.Error);
                if (statusTextBlock != null) statusTextBlock.Text = "B³¹d konfiguracji Pythona.";
                return;
            }
            if (!File.Exists(ClipServerScriptPath))
            {
                MessageBox.Show($"Krytyczny b³¹d: Nie znaleziono skryptu serwera Pythona w: \n{ClipServerScriptPath}\n\nSprawdŸ konfiguracjê œcie¿ek.",
                                "B³¹d konfiguracji skryptu serwera", MessageBoxButton.OK, MessageBoxImage.Error);
                if (statusTextBlock != null) statusTextBlock.Text = "B³¹d konfiguracji skryptu serwera.";
                return;
            }

            if (statusTextBlock != null) statusTextBlock.Text = "Uruchamianie serwera AI (CLIP)...";
            SimpleFileLogger.LogHighLevelInfo("MainWindow: Uruchamianie serwera CLIP...");

            _clipService = new ClipServiceHttpClient(
                pathToPythonExecutable: PythonExecutablePath,
                pathToClipServerScript: ClipServerScriptPath
            );

            bool serverStarted = false;
            try
            {
                serverStarted = await _clipService.StartServerAsync();
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError("MainWindow: Krytyczny b³¹d podczas uruchamiania serwera CLIP.", ex);
                MessageBox.Show($"Krytyczny b³¹d podczas uruchamiania serwera CLIP: {ex.Message}\nSprawdŸ logi aplikacji.",
                               "B³¹d serwera AI", MessageBoxButton.OK, MessageBoxImage.Error);
                if (statusTextBlock != null) statusTextBlock.Text = "Krytyczny b³¹d serwera AI.";
                return;
            }

            if (serverStarted)
            {
                SimpleFileLogger.LogHighLevelInfo("MainWindow: Serwer CLIP uruchomiony pomyœlnie.");
                if (statusTextBlock != null) statusTextBlock.Text = "Serwer AI uruchomiony. Inicjalizacja ViewModel...";

                _embeddingCacheServiceInstance = new EmbeddingCacheServiceSQLite(); // ZMIANA NA EmbeddingCacheServiceSQLite
                _profileServiceInstance = new ProfileService(_clipService, _embeddingCacheServiceInstance);
                var fileScanner = new FileScannerService();
                _imageMetadataService = new ImageMetadataService();

                _viewModelInstance = new MainWindowViewModel(
                    _profileServiceInstance,
                    fileScanner,
                    _imageMetadataService,
                    _settingsServiceInstance);
                this.DataContext = _viewModelInstance;

                try
                {
                    await _viewModelInstance.InitializeAsync(); // Tutaj ViewModel wczyta ustawienia, w tym EnableDebugLogging
                    if (statusTextBlock != null) statusTextBlock.Text = "Aplikacja gotowa.";
                }
                catch (Exception vmEx)
                {
                    SimpleFileLogger.LogError("B³¹d podczas inicjalizacji MainWindowViewModel", vmEx);
                    MessageBox.Show($"Wyst¹pi³ b³¹d podczas inicjalizacji danych aplikacji: {vmEx.Message}", "B³¹d Inicjalizacji", MessageBoxButton.OK, MessageBoxImage.Error);
                    if (statusTextBlock != null) statusTextBlock.Text = "B³¹d inicjalizacji ViewModel.";
                }
            }
            else
            {
                SimpleFileLogger.LogError("MainWindow: Nie uda³o siê uruchomiæ serwera CLIP (StartServerAsync zwróci³ false).");
                if (statusTextBlock != null) statusTextBlock.Text = "B³¹d serwera AI. Funkcje AI niedostêpne.";
                MessageBox.Show("Nie uda³o siê uruchomiæ serwera AI (CLIP). Funkcjonalnoœæ zwi¹zana z analiz¹ obrazów bêdzie niedostêpna. SprawdŸ konsolê oraz pliki logów.",
                               "B³¹d uruchamiania serwera AI", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var testClipMenuItem = this.FindName("TestClipMenuItem") as MenuItem;
            if (testClipMenuItem != null) testClipMenuItem.IsEnabled = serverStarted;
        }

        private async void MainWindow_Closing_SaveItems(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SimpleFileLogger.LogHighLevelInfo("MainWindow: Zamykanie aplikacji...");

            if (_viewModelInstance != null)
            {
                await _viewModelInstance.OnAppClosingAsync(); // ViewModel zapisze ustawienia
            }

            // EmbeddingCacheServiceSQLite nie wymaga jawnego zapisu ca³ego cache'u przy zamykaniu
            // _embeddingCacheServiceInstance?.Dispose(); // Jeœli zaimplementowano IDisposable w EmbeddingCacheServiceSQLite

            _clipService?.Dispose();
            SimpleFileLogger.LogHighLevelInfo("MainWindow: Serwis CLIP zatrzymany.");
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private async void TestClipButton_Click(object sender, RoutedEventArgs e)
        {
            if (_profileServiceInstance == null || _imageMetadataService == null)
            {
                MessageBox.Show("Us³ugi profilowania lub metadanych nie zosta³y zainicjalizowane.", "B³¹d us³ugi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_clipService != null && !await _clipService.IsServerRunningAsync(checkEmbedderInitialization: true))
            {
                var result = MessageBox.Show("Serwer AI (CLIP) nie jest gotowy lub nie dzia³a poprawnie. Czy spróbowaæ go uruchomiæ/zrestartowaæ?",
                                             "Serwer AI niegotowy", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    var statusTextBlock = this.FindName("StatusTextBlock") as TextBlock;
                    if (statusTextBlock != null) statusTextBlock.Text = "Próba restartu serwera AI...";
                    bool restarted = await _clipService.StartServerAsync();
                    if (statusTextBlock != null) statusTextBlock.Text = restarted ? "Serwer AI (re)startowany." : "Nie uda³o siê (re)startowaæ serwera AI.";
                    if (!restarted) return;
                }
                else
                {
                    return;
                }
            }

            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Image files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All files (*.*)|*.*",
                Title = "Wybierz obraz do analizy CLIP"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string imageFilePath = openFileDialog.FileName;
                var statusTextBlock = this.FindName("StatusTextBlock") as TextBlock;
                if (statusTextBlock != null) statusTextBlock.Text = $"Analizowanie obrazu: {Path.GetFileName(imageFilePath)}...";
                SimpleFileLogger.LogHighLevelInfo($"TestClipButton: Próba analizy obrazu: {imageFilePath}");

                try
                {
                    float[]? embedding = null;
                    var imageEntry = await _imageMetadataService.ExtractMetadataAsync(imageFilePath);
                    if (imageEntry != null)
                    {
                        embedding = await _profileServiceInstance.GetImageEmbeddingAsync(imageEntry);
                    }
                    else
                    {
                        SimpleFileLogger.LogWarning($"TestClipButton: Nie uda³o siê utworzyæ ImageFileEntry dla {imageFilePath}");
                    }

                    if (embedding != null && embedding.Any())
                    {
                        string message = $"Uzyskano wektor cech dla obrazu:\n{Path.GetFileName(imageFilePath)}\n\n" +
                                         $"D³ugoœæ wektora: {embedding.Length}\n" +
                                         $"Fragment: [{string.Join(", ", embedding.Take(5).Select(f => f.ToString("F4")))} ...]";
                        MessageBox.Show(message, "Analiza CLIP Zakoñczona", MessageBoxButton.OK, MessageBoxImage.Information);
                        if (statusTextBlock != null) statusTextBlock.Text = $"Analiza '{Path.GetFileName(imageFilePath)}' zakoñczona.";
                        SimpleFileLogger.LogHighLevelInfo($"TestClipButton: Sukces. Obraz: {Path.GetFileName(imageFilePath)}, D³. wektora: {embedding.Length}");
                    }
                    else
                    {
                        MessageBox.Show("Nie uda³o siê uzyskaæ wektora cech dla obrazu (wynik null lub pusty).", "B³¹d Analizy CLIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (statusTextBlock != null) statusTextBlock.Text = $"B³¹d analizy '{Path.GetFileName(imageFilePath)}'.";
                        SimpleFileLogger.LogWarning($"TestClipButton: Nie uda³o siê uzyskaæ wektora cech (null/pusty) dla {Path.GetFileName(imageFilePath)}");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Wyst¹pi³ b³¹d podczas analizy CLIP: {ex.Message}", "B³¹d Analizy CLIP", MessageBoxButton.OK, MessageBoxImage.Error);
                    if (statusTextBlock != null) statusTextBlock.Text = "B³¹d podczas analizy CLIP.";
                    SimpleFileLogger.LogError($"TestClipButton: B³¹d podczas analizy obrazu {Path.GetFileName(imageFilePath)}", ex);
                }
            }
        }

        private void ProfilesTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_viewModelInstance != null && e.NewValue is CategoryProfile selectedCharacterProfile)
            {
                _viewModelInstance.SelectedProfile = selectedCharacterProfile;
            }
        }
    }
}