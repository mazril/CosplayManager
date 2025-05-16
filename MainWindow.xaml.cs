// Plik: MainWindow.xaml.cs

// ... (reszta kodu bez zmian)

private async void TestClipButton_Click(object sender, RoutedEventArgs e)
{
    if (_clipService == null && _profileServiceInstance == null) // Sprawd� obie us�ugi
    {
        MessageBox.Show("Us�uga CLIP oraz ProfileService nie zosta�y zainicjalizowane.", "B��d us�ugi", MessageBoxButton.OK, MessageBoxImage.Error);
        return;
    }

    // Sprawdzenie gotowo�ci serwera CLIP
    if (_clipService != null && !await _clipService.IsServerRunningAsync(checkEmbedderInitialization: true))
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
            float[]? embedding = null;
            if (_profileServiceInstance != null && _imageMetadataService != null) // Upewnij si�, �e ImageMetadataService te� jest dost�pny
            {
                // Utw�rz ImageFileEntry, aby uzyska� metadane potrzebne dla cache'u
                var imageEntry = await _imageMetadataService.ExtractMetadataAsync(imageFilePath);
                if (imageEntry != null)
                {
                    // ZMIANA WYWO�ANIA GetImageEmbeddingAsync
                    embedding = await _profileServiceInstance.GetImageEmbeddingAsync(imageEntry);
                }
                else
                {
                    SimpleFileLogger.LogWarning($"TestClipButton: Nie uda�o si� utworzy� ImageFileEntry dla {imageFilePath}");
                }
            }
            // Opcjonalny fallback, je�li ProfileService nie jest gotowy (cho� powinien by�)
            else if (_clipService != null)
            {
                SimpleFileLogger.LogWarning("TestClipButton: ProfileService lub ImageMetadataService niedost�pne, pr�ba bezpo�redniego wywo�ania CLIPService.");
                embedding = await _clipService.GetImageEmbeddingFromPathAsync(imageFilePath);
            }


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
// ... (reszta kodu)