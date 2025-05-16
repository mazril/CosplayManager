// Plik: MainWindow.xaml.cs

// ... (reszta kodu bez zmian)

private async void TestClipButton_Click(object sender, RoutedEventArgs e)
{
    if (_clipService == null && _profileServiceInstance == null) // SprawdŸ obie us³ugi
    {
        MessageBox.Show("Us³uga CLIP oraz ProfileService nie zosta³y zainicjalizowane.", "B³¹d us³ugi", MessageBoxButton.OK, MessageBoxImage.Error);
        return;
    }

    // Sprawdzenie gotowoœci serwera CLIP
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
        SimpleFileLogger.Log($"TestClipButton: Próba analizy obrazu: {imageFilePath}");

        try
        {
            float[]? embedding = null;
            if (_profileServiceInstance != null && _imageMetadataService != null) // Upewnij siê, ¿e ImageMetadataService te¿ jest dostêpny
            {
                // Utwórz ImageFileEntry, aby uzyskaæ metadane potrzebne dla cache'u
                var imageEntry = await _imageMetadataService.ExtractMetadataAsync(imageFilePath);
                if (imageEntry != null)
                {
                    // ZMIANA WYWO£ANIA GetImageEmbeddingAsync
                    embedding = await _profileServiceInstance.GetImageEmbeddingAsync(imageEntry);
                }
                else
                {
                    SimpleFileLogger.LogWarning($"TestClipButton: Nie uda³o siê utworzyæ ImageFileEntry dla {imageFilePath}");
                }
            }
            // Opcjonalny fallback, jeœli ProfileService nie jest gotowy (choæ powinien byæ)
            else if (_clipService != null)
            {
                SimpleFileLogger.LogWarning("TestClipButton: ProfileService lub ImageMetadataService niedostêpne, próba bezpoœredniego wywo³ania CLIPService.");
                embedding = await _clipService.GetImageEmbeddingFromPathAsync(imageFilePath);
            }


            if (embedding != null && embedding.Any())
            {
                string message = $"Uzyskano wektor cech dla obrazu:\n{Path.GetFileName(imageFilePath)}\n\n" +
                                 $"D³ugoœæ wektora: {embedding.Length}\n" +
                                 $"Fragment: [{string.Join(", ", embedding.Take(5).Select(f => f.ToString("F4")))} ...]";
                MessageBox.Show(message, "Analiza CLIP Zakoñczona", MessageBoxButton.OK, MessageBoxImage.Information);
                if (statusTextBlock != null) statusTextBlock.Text = $"Analiza '{Path.GetFileName(imageFilePath)}' zakoñczona.";
                SimpleFileLogger.Log($"TestClipButton: Sukces. Obraz: {Path.GetFileName(imageFilePath)}, D³. wektora: {embedding.Length}");
            }
            else
            {
                MessageBox.Show("Nie uda³o siê uzyskaæ wektora cech dla obrazu (wynik null lub pusty).", "B³¹d Analizy CLIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                if (statusTextBlock != null) statusTextBlock.Text = $"B³¹d analizy '{Path.GetFileName(imageFilePath)}'.";
                SimpleFileLogger.Log($"TestClipButton: Nie uda³o siê uzyskaæ wektora cech (null/pusty) dla {Path.GetFileName(imageFilePath)}");
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
// ... (reszta kodu)