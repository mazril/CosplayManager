using CosplayManager.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CosplayManager.Models
{
    public class ImageFileEntry : INotifyPropertyChanged
    {
        private string _filePath = string.Empty;
        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        private string _fileName = string.Empty;
        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        private int _width;
        public int Width
        {
            get => _width;
            set => SetProperty(ref _width, value);
        }

        private int _height;
        public int Height
        {
            get => _height;
            set => SetProperty(ref _height, value);
        }

        private string _modelName = string.Empty;
        public string ModelName
        {
            get => _modelName;
            set => SetProperty(ref _modelName, value);
        }

        private string _characterName = string.Empty;
        public string CharacterName
        {
            get => _characterName;
            set => SetProperty(ref _characterName, value);
        }

        private ulong? _perceptualHash;
        public ulong? PerceptualHash
        {
            get => _perceptualHash;
            set
            {
                if (SetProperty(ref _perceptualHash, value))
                {
                    OnPropertyChanged(nameof(HasPerceptualHash));
                }
            }
        }

        private float[]? _featureVector;
        public float[]? FeatureVector
        {
            get => _featureVector;
            set
            {
                if (SetProperty(ref _featureVector, value))
                {
                    OnPropertyChanged(nameof(FeatureVectorLength));
                    OnPropertyChanged(nameof(HasFeatureVector));
                }
            }
        }

        private DateTime _fileLastModifiedUtc;
        public DateTime FileLastModifiedUtc
        {
            get => _fileLastModifiedUtc;
            set => SetProperty(ref _fileLastModifiedUtc, value);
        }

        private long _fileSize;
        public long FileSize
        {
            get => _fileSize;
            set => SetProperty(ref _fileSize, value);
        }

        [JsonIgnore]
        public int FeatureVectorLength => _featureVector?.Length ?? 0;

        [JsonIgnore]
        public bool HasFeatureVector => _featureVector != null && _featureVector.Length > 0;

        [JsonIgnore]
        public bool HasPerceptualHash => _perceptualHash != null;

        private BitmapImage? _thumbnail;
        [JsonIgnore]
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            private set => SetProperty(ref _thumbnail, value);
        }

        private bool _isLoadingThumbnail;
        [JsonIgnore]
        public bool IsLoadingThumbnail
        {
            get => _isLoadingThumbnail;
            private set => SetProperty(ref _isLoadingThumbnail, value);
        }

        // Globalny semafor do ograniczania liczby równoczesnych operacji ładowania miniaturek
        private static readonly SemaphoreSlim _thumbnailGlobalSemaphore = new SemaphoreSlim(5, 5); // Można dostosować limit (np. 5)

        public async Task<BitmapImage?> LoadThumbnailAsync(int decodePixelWidth = 150)
        {
            if (this.Thumbnail != null && !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath))
            {
                return this.Thumbnail;
            }

            if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            {
                if (!string.IsNullOrWhiteSpace(FilePath) && !File.Exists(FilePath))
                {
                    SimpleFileLogger.LogWarning($"ImageFileEntry.LoadThumbnailAsync: Plik źródłowy nie istnieje dla '{FilePath}', miniatura nie zostanie załadowana.");
                }
                if (this.Thumbnail != null)
                {
                    this.Thumbnail = null; // Wyczyść istniejącą (już nieaktualną) miniaturkę
                }
                return null;
            }

            if (IsLoadingThumbnail)
            {
                SimpleFileLogger.Log($"ImageFileEntry.LoadThumbnailAsync: Już w trakcie ładowania dla '{FilePath}'.");
                return this.Thumbnail; // Zwraca aktualną miniaturkę, która może być null, jeśli ładowanie jeszcze trwa
            }

            IsLoadingThumbnail = true;
            BitmapImage? finalBitmapImage = null;

            await _thumbnailGlobalSemaphore.WaitAsync(); // Czekaj na globalny semafor
            try
            {
                byte[]? imageBytes = await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(FilePath)) // Podwójne sprawdzenie wewnątrz Task.Run
                        {
                            SimpleFileLogger.LogWarning($"ImageFileEntry.LoadThumbnailAsync (Task.Run ImageSharp): Plik nie znaleziony '{FilePath}'.");
                            return null;
                        }

                        using (var image = SixLabors.ImageSharp.Image.Load(FilePath)) // Automatyczne wykrywanie formatu przez ImageSharp
                        {
                            var newWidth = decodePixelWidth;
                            var newHeight = 0;
                            if (image.Height > image.Width) // Obraz portretowy
                            {
                                newWidth = 0;
                                newHeight = decodePixelWidth;
                            }

                            image.Mutate(x => x.Resize(new ResizeOptions
                            {
                                Size = new SixLabors.ImageSharp.Size(newWidth, newHeight),
                                Mode = ResizeMode.Max, // Zachowaj proporcje, dopasuj do maksymalnych wymiarów
                                Sampler = KnownResamplers.Lanczos3 // Dobra jakość próbkowania
                            }));

                            using (var ms = new MemoryStream())
                            {
                                image.SaveAsBmp(ms); // Zapisz jako BMP do strumienia pamięci (szybkie dla BitmapImage)
                                ms.Position = 0;
                                return ms.ToArray();
                            }
                        }
                    }
                    catch (SixLabors.ImageSharp.UnknownImageFormatException uifEx)
                    {
                        SimpleFileLogger.LogError($"ImageFileEntry.LoadThumbnailAsync (Task.Run ImageSharp): UnknownImageFormatException dla {FilePath}. Plik może być uszkodzony lub format nie jest obsługiwany przez ImageSharp.", uifEx);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        SimpleFileLogger.LogError($"Błąd tworzenia miniatury (ImageSharp) dla {FilePath} w Task.Run", ex);
                        return null;
                    }
                });

                if (imageBytes != null && imageBytes.Length > 0)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad; // Załaduj od razu
                    bmp.StreamSource = new MemoryStream(imageBytes);
                    // bmp.DecodePixelWidth = decodePixelWidth; // Już przeskalowane przez ImageSharp, ale można zostawić jako wskazówkę
                    bmp.EndInit();
                    bmp.Freeze(); // Niezbędne do użycia w innym wątku (UI)
                    finalBitmapImage = bmp;
                }

                // Ustawienie właściwości Thumbnail (co wywoła OnPropertyChanged i zaktualizuje UI)
                // musi nastąpić po całkowitym utworzeniu i zamrożeniu obiektu BitmapImage.
                this.Thumbnail = finalBitmapImage;
            }
            catch (Exception ex) // Ogólny wyjątek dla całego bloku try semafora
            {
                SimpleFileLogger.LogError($"Zewnętrzny błąd podczas operacji ładowania miniatury (ImageSharp flow) dla {FilePath}", ex);
                this.Thumbnail = null; // W przypadku błędu, ustaw miniaturkę na null
            }
            finally
            {
                _thumbnailGlobalSemaphore.Release(); // Zwolnij globalny semafor
                IsLoadingThumbnail = false; // Zaktualizuj flagę, nawet jeśli wystąpił błąd
            }
            return this.Thumbnail;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}