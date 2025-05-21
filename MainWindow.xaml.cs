// Plik: MainWindow.xaml.cs
using CosplayManager.Models; // Dla ProgressReport
using CosplayManager.Services;
using CosplayManager.ViewModels;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private EmbeddingCacheServiceSQLite? _embeddingCacheServiceInstance;
        private ImageMetadataService? _imageMetadataService;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing_SaveItems;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var statusTextBlock = this.StatusTextBlock; // U¿ywamy x:Name z XAML
            if (statusTextBlock != null) statusTextBlock.Text = "Inicjalizacja aplikacji...";

            _settingsServiceInstance = new SettingsService();

            _clipService = new ClipServiceHttpClient();
            _embeddingCacheServiceInstance = new EmbeddingCacheServiceSQLite();
            _imageMetadataService = new ImageMetadataService();
            var fileScanner = new FileScannerService();
            _profileServiceInstance = new ProfileService(_clipService, _embeddingCacheServiceInstance);

            _viewModelInstance = new MainWindowViewModel(
                _profileServiceInstance,
                fileScanner,
                _imageMetadataService,
                _settingsServiceInstance);
            this.DataContext = _viewModelInstance;

            if (_viewModelInstance != null)
            {
                using (var ctsInitVM = new CancellationTokenSource())
                {
                    // Poprawione wywo³anie - delegat teraz akceptuje token i progress
                    await _viewModelInstance.RunLongOperation(
                        async (token, progress) => await _viewModelInstance.InitializeAsync(token, progress),
                        "Inicjalizacja ViewModelu"
                    );
                }
            }

            bool serverConnectedAndVerified = false;
            if (_clipService != null)
            {
                if (statusTextBlock != null) statusTextBlock.Text = "Sprawdzanie po³¹czenia z serwerem AI (CLIP)...";
                SimpleFileLogger.LogHighLevelInfo("MainWindow: Sprawdzanie po³¹czenia z serwerem CLIP...");
                try
                {
                    serverConnectedAndVerified = await _clipService.EnsureServerConnectionAsync();
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError("MainWindow: Krytyczny b³¹d podczas próby po³¹czenia z serwerem CLIP.", ex);
                    MessageBox.Show($"Krytyczny b³¹d podczas próby po³¹czenia z serwerem CLIP: {ex.Message}\nSprawdŸ logi aplikacji.",
                                   "B³¹d Po³¹czenia z Serwerem AI", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            if (serverConnectedAndVerified)
            {
                SimpleFileLogger.LogHighLevelInfo("MainWindow: Serwer CLIP dzia³a i jest gotowy.");
                if (statusTextBlock != null) statusTextBlock.Text = "Serwer AI po³¹czony. Aplikacja gotowa.";
            }
            else
            {
                SimpleFileLogger.LogError("MainWindow: Nie uda³o siê po³¹czyæ z serwerem CLIP lub serwer nie jest gotowy. Uruchom serwer Pythona rêcznie na porcie 8008 (lub skonfigurowanym).");
                if (statusTextBlock != null) statusTextBlock.Text = "B³¹d po³¹czenia z serwerem AI. Funkcje AI niedostêpne.";
                MessageBox.Show("Nie uda³o siê po³¹czyæ z serwerem AI (CLIP) lub serwer nie jest gotowy.\nUpewnij siê, ¿e serwer Pythona (`clip_server.py`) jest uruchomiony rêcznie i nas³uchuje na skonfigurowanym porcie (domyœlnie 8008).\n\nFunkcjonalnoœæ zwi¹zana z analiz¹ obrazów bêdzie niedostêpna.",
                              "B³¹d Po³¹czenia z Serwerem AI", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var testClipMenuItem = this.TestClipMenuItem; // U¿ywamy x:Name
            if (testClipMenuItem != null) testClipMenuItem.IsEnabled = serverConnectedAndVerified;

            if (statusTextBlock != null && (_viewModelInstance?.ProgressStatusText.EndsWith("...") == true || _viewModelInstance?.ProgressStatusText.Contains("Inicjalizacja ViewModelu - Zakoñczono.") == true))
            {
                // Po inicjalizacji, ProgressStatusText powinien byæ ju¿ ustawiony przez RunLongOperation
                // Jeœli nie, mo¿na by ustawiæ domyœlny status.
                if (string.IsNullOrEmpty(_viewModelInstance?.ProgressStatusText) || _viewModelInstance.ProgressStatusText.EndsWith("..."))
                {
                    _viewModelInstance.ProgressStatusText = serverConnectedAndVerified ? "Aplikacja gotowa." : "Aplikacja gotowa (funkcje AI niedostêpne).";
                }
            }
        }

        private async void MainWindow_Closing_SaveItems(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SimpleFileLogger.LogHighLevelInfo("MainWindow: Zamykanie aplikacji...");
            if (_viewModelInstance != null)
            {
                await _viewModelInstance.OnAppClosingAsync();
            }
            _clipService?.Dispose();
            SimpleFileLogger.LogHighLevelInfo("MainWindow: Zasoby ClipServiceHttpClient zwolnione.");
            // EmbeddingCacheServiceSQLite nie ma obecnie metody Dispose do wywo³ania, ale jeœli by mia³a, to tutaj.
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
            if (_clipService == null)
            {
                MessageBox.Show("Us³uga CLIPService nie zosta³a zainicjalizowana.", "B³¹d us³ugi CLIP", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!await _clipService.EnsureServerConnectionAsync())
            {
                MessageBox.Show("Nie uda³o siê po³¹czyæ z serwerem AI (CLIP) lub serwer nie jest gotowy.\nUpewnij siê, ¿e serwer Pythona jest uruchomiony rêcznie.",
                                             "Serwer AI niegotowy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Image files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All files (*.*)|*.*",
                Title = "Wybierz obraz do analizy CLIP"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string imageFilePath = openFileDialog.FileName;
                var statusTextBlock = this.StatusTextBlock;
                if (statusTextBlock != null && _viewModelInstance != null) _viewModelInstance.ProgressStatusText = $"Analizowanie obrazu: {Path.GetFileName(imageFilePath)}...";
                SimpleFileLogger.LogHighLevelInfo($"TestClipButton: Próba analizy obrazu: {imageFilePath}");

                try
                {
                    var imageEntry = await _imageMetadataService.ExtractMetadataAsync(imageFilePath);
                    if (imageEntry == null)
                    {
                        MessageBox.Show("Nie uda³o siê odczytaæ metadanych obrazu.", "B³¹d odczytu obrazu", MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (statusTextBlock != null && _viewModelInstance != null) _viewModelInstance.ProgressStatusText = $"B³¹d odczytu metadanych dla '{Path.GetFileName(imageFilePath)}'.";
                        return;
                    }

                    float[]? embedding = await _profileServiceInstance.GetImageEmbeddingAsync(imageEntry);


                    if (embedding != null && embedding.Any())
                    {
                        string message = $"Uzyskano wektor cech dla obrazu:\n{Path.GetFileName(imageFilePath)}\n\n" +
                                         $"D³ugoœæ wektora: {embedding.Length}\n" +
                                         $"Fragment: [{string.Join(", ", embedding.Take(5).Select(f => f.ToString("F4")))} ...]";
                        MessageBox.Show(message, "Analiza CLIP Zakoñczona", MessageBoxButton.OK, MessageBoxImage.Information);
                        if (statusTextBlock != null && _viewModelInstance != null) _viewModelInstance.ProgressStatusText = $"Analiza '{Path.GetFileName(imageFilePath)}' zakoñczona.";
                        SimpleFileLogger.LogHighLevelInfo($"TestClipButton: Sukces. Obraz: {Path.GetFileName(imageFilePath)}, D³. wektora: {embedding.Length}");
                    }
                    else
                    {
                        MessageBox.Show("Nie uda³o siê uzyskaæ wektora cech dla obrazu (wynik null lub pusty). Upewnij siê, ¿e serwer AI dzia³a i odpowiada.", "B³¹d Analizy CLIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (statusTextBlock != null && _viewModelInstance != null) _viewModelInstance.ProgressStatusText = $"B³¹d analizy '{Path.GetFileName(imageFilePath)}'.";
                        SimpleFileLogger.LogWarning($"TestClipButton: Nie uda³o siê uzyskaæ wektora cech (null/pusty) dla {Path.GetFileName(imageFilePath)}");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Wyst¹pi³ b³¹d podczas analizy CLIP: {ex.Message}", "B³¹d Analizy CLIP", MessageBoxButton.OK, MessageBoxImage.Error);
                    if (statusTextBlock != null && _viewModelInstance != null) _viewModelInstance.ProgressStatusText = "B³¹d podczas analizy CLIP.";
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
            else if (_viewModelInstance != null && e.NewValue is ModelDisplayViewModel)
            {
                // Jeœli wybrano wêze³ modelki, mo¿na wyczyœciæ SelectedProfile lub ustawiæ na null,
                // aby edytor profilu nie pokazywa³ danych ostatnio wybranej postaci.
                // _viewModelInstance.SelectedProfile = null; // Opcjonalne, zale¿y od po¿¹danego zachowania
            }
        }
    }
}