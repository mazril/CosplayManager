// Plik: MainWindow.xaml.cs
using CosplayManager.Models; // Potrzebne dla CategoryProfile
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

        private string PythonExecutablePath = @"C:\Users\GameStation\AppData\Local\Programs\Python\Python311\python.exe";
        private string ClipServerScriptPath = @"C:\Projekt\CosplayManager\Python\clip_server.py";

        public MainWindow()
        {
            InitializeComponent();

            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing_SaveSettings;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var statusTextBlock = this.FindName("StatusTextBlock") as TextBlock;
            if (statusTextBlock != null) statusTextBlock.Text = "Inicjalizacja...";

            _settingsServiceInstance = new SettingsService();
            UserSettings? loadedSettings = await _settingsServiceInstance.LoadSettingsAsync();
            // Stosowanie ustawie� Pythona, je�li s� i je�li chcesz je wczytywa� st�d
            // PythonExecutablePath = ...
            // ClipServerScriptPath = ...

            if (!File.Exists(PythonExecutablePath))
            {
                MessageBox.Show($"Krytyczny b��d: Nie znaleziono interpretera Pythona w: \n{PythonExecutablePath}\n\nSprawd� konfiguracj� �cie�ek.",
                                "B��d konfiguracji interpretera Python", MessageBoxButton.OK, MessageBoxImage.Error);
                if (statusTextBlock != null) statusTextBlock.Text = "B��d konfiguracji Pythona.";
                return;
            }
            if (!File.Exists(ClipServerScriptPath))
            {
                MessageBox.Show($"Krytyczny b��d: Nie znaleziono skryptu serwera Pythona w: \n{ClipServerScriptPath}\n\nSprawd� konfiguracj� �cie�ek.",
                                "B��d konfiguracji skryptu serwera", MessageBoxButton.OK, MessageBoxImage.Error);
                if (statusTextBlock != null) statusTextBlock.Text = "B��d konfiguracji skryptu serwera.";
                return;
            }

            if (statusTextBlock != null) statusTextBlock.Text = "Uruchamianie serwera AI (CLIP)...";
            SimpleFileLogger.Log("MainWindow: Uruchamianie serwera CLIP...");

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
                SimpleFileLogger.LogError("MainWindow: Krytyczny b��d podczas uruchamiania serwera CLIP.", ex);
                MessageBox.Show($"Krytyczny b��d podczas uruchamiania serwera CLIP: {ex.Message}\nSprawd� logi aplikacji.",
                               "B��d serwera AI", MessageBoxButton.OK, MessageBoxImage.Error);
                if (statusTextBlock != null) statusTextBlock.Text = "Krytyczny b��d serwera AI.";
                return;
            }

            if (serverStarted)
            {
                SimpleFileLogger.Log("MainWindow: Serwer CLIP uruchomiony pomy�lnie.");
                if (statusTextBlock != null) statusTextBlock.Text = "Serwer AI uruchomiony. Inicjalizacja ViewModel...";

                _profileServiceInstance = new ProfileService(_clipService);
                var fileScanner = new FileScannerService();
                var metadataService = new ImageMetadataService();

                _viewModelInstance = new MainWindowViewModel(
                    _profileServiceInstance,
                    fileScanner,
                    metadataService,
                    _settingsServiceInstance);
                this.DataContext = _viewModelInstance;

                try
                {
                    await _viewModelInstance.InitializeAsync();
                    if (statusTextBlock != null) statusTextBlock.Text = "Aplikacja gotowa.";
                }
                catch (Exception vmEx)
                {
                    SimpleFileLogger.LogError("B��d podczas inicjalizacji MainWindowViewModel", vmEx);
                    MessageBox.Show($"Wyst�pi� b��d podczas inicjalizacji danych aplikacji: {vmEx.Message}", "B��d Inicjalizacji", MessageBoxButton.OK, MessageBoxImage.Error);
                    if (statusTextBlock != null) statusTextBlock.Text = "B��d inicjalizacji ViewModel.";
                }
            }
            else
            {
                SimpleFileLogger.LogError("MainWindow: Nie uda�o si� uruchomi� serwera CLIP (StartServerAsync zwr�ci� false).");
                if (statusTextBlock != null) statusTextBlock.Text = "B��d serwera AI. Funkcje AI niedost�pne.";
                MessageBox.Show("Nie uda�o si� uruchomi� serwera AI (CLIP). Funkcjonalno�� zwi�zana z analiz� obraz�w b�dzie niedost�pna. Sprawd� konsol� oraz pliki log�w.",
                               "B��d uruchamiania serwera AI", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var testClipMenuItem = this.FindName("TestClipMenuItem") as MenuItem;
            if (testClipMenuItem != null) testClipMenuItem.IsEnabled = serverStarted;
        }

        private async void MainWindow_Closing_SaveSettings(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SimpleFileLogger.Log("MainWindow: Zamykanie aplikacji...");

            if (_viewModelInstance != null)
            {
                await _viewModelInstance.OnAppClosingAsync();
            }

            _clipService?.Dispose();
            SimpleFileLogger.Log("MainWindow: Serwis CLIP zatrzymany.");
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private async void TestClipButton_Click(object sender, RoutedEventArgs e)
        {
            // ... (bez zmian)
            if (_clipService == null)
            {
                MessageBox.Show("Us�uga CLIP nie zosta�a zainicjalizowana.", "B��d us�ugi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!await _clipService.IsServerRunningAsync(checkEmbedderInitialization: true))
            {
                var result = MessageBox.Show("Serwer AI (CLIP) nie jest gotowy lub nie dzia�a poprawnie. Czy spr�bowa� go uruchomi�/zrestartowa�?",
                                             "Serwer AI niegotowy", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    var statusTextBlock = this.FindName("StatusTextBlock") as TextBlock;
                    if (statusTextBlock != null) statusTextBlock.Text = "Pr�ba restartu serwera AI...";
                    bool restarted = await _clipService.StartServerAsync();
                    if (statusTextBlock != null) statusTextBlock.Text = restarted ? "Serwer AI (re)startowany." : "Nie uda�o si� (re)startowa� serwera AI.";
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
                SimpleFileLogger.Log($"TestClipButton: Pr�ba analizy obrazu: {imageFilePath}");

                try
                {
                    float[]? embedding = await _clipService.GetImageEmbeddingFromPathAsync(imageFilePath);
                    if (embedding != null && embedding.Any())
                    {
                        string message = $"Uzyskano wektor cech dla obrazu:\n{Path.GetFileName(imageFilePath)}\n\n" +
                                         $"D�ugo�� wektora: {embedding.Length}\n" +
                                         $"Fragment: [{string.Join(", ", embedding.Take(5).Select(f => f.ToString("F4")))} ...]";
                        MessageBox.Show(message, "Analiza CLIP Zako�czona", MessageBoxButton.OK, MessageBoxImage.Information);
                        if (statusTextBlock != null) statusTextBlock.Text = $"Analiza '{Path.GetFileName(imageFilePath)}' zako�czona.";
                        SimpleFileLogger.Log($"TestClipButton: Sukces. Obraz: {Path.GetFileName(imageFilePath)}, D�. wektora: {embedding.Length}");
                    }
                    else
                    {
                        MessageBox.Show("Nie uda�o si� uzyska� wektora cech dla obrazu (wynik null lub pusty).", "B��d Analizy CLIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (statusTextBlock != null) statusTextBlock.Text = $"B��d analizy '{Path.GetFileName(imageFilePath)}'.";
                        SimpleFileLogger.Log($"TestClipButton: Nie uda�o si� uzyska� wektora cech (null/pusty) dla {Path.GetFileName(imageFilePath)}");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Wyst�pi� b��d podczas analizy CLIP: {ex.Message}", "B��d Analizy CLIP", MessageBoxButton.OK, MessageBoxImage.Error);
                    if (statusTextBlock != null) statusTextBlock.Text = "B��d podczas analizy CLIP.";
                    SimpleFileLogger.LogError($"TestClipButton: B��d podczas analizy obrazu {Path.GetFileName(imageFilePath)}", ex);
                }
            }
        }

        // === NOWA METODA OBS�UGI ZAZNACZENIA W TreeView ===
        private void ProfilesTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_viewModelInstance != null && e.NewValue is CategoryProfile selectedCharacterProfile)
            {
                _viewModelInstance.SelectedProfile = selectedCharacterProfile;
            }
            // Je�li zaznaczono w�ze� modelki (ModelDisplayViewModel), mo�emy chcie� wyczy�ci� SelectedProfile
            // lub zaimplementowa� inn� logik� (np. wy�wietlanie podsumowania modelki).
            // Na razie, je�li nie jest to CategoryProfile, SelectedProfile pozostanie niezmienione
            // lub mo�na je wyzerowa�, je�li poprzednio by� wybrany CategoryProfile.
            else if (_viewModelInstance != null && !(e.NewValue is CategoryProfile))
            {
                // Je�li zaznaczono w�ze� modelki (a nie postaci), wyczy�� zaznaczenie profilu postaci.
                // _viewModelInstance.SelectedProfile = null; // Opcjonalne - zale�y od po��danego zachowania.
                // Na razie nie r�bmy nic, aby edytor pozosta� z danymi ostatnio wybranej postaci.
            }
        }
    }
}