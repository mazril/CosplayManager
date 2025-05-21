// Plik: CosplayManager/Models/ImageFileEntry.cs
using CosplayManager.Services;
using System; // Dodane dla Uri
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
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

        public async Task<BitmapImage?> LoadThumbnailAsync(int decodePixelWidth = 150)
        {
            // Jeśli miniaturka już istnieje i jest poprawna (plik nadal istnieje), zwróć ją.
            // Można dodać opcjonalny parametr "forceReload", jeśli chcemy wymusić przeładowanie.
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
                // Upewnij się, że Thumbnail jest null, jeśli plik nie istnieje lub ścieżka jest pusta
                if (this.Thumbnail != null)
                {
                    this.Thumbnail = null;
                }
                return null;
            }

            // Proste zabezpieczenie przed wielokrotnym ładowaniem tej samej miniaturki jednocześnie.
            // Bardziej zaawansowane mechanizmy mogłyby używać SemaphoreSlim.
            if (IsLoadingThumbnail)
            {
                // Można by poczekać na zakończenie poprzedniego ładowania, np. używając TaskCompletionSource,
                // ale dla uproszczenia, jeśli już ładujemy, to zwracamy obecny stan (prawdopodobnie null lub stara miniaturka).
                // Jeśli Thumbnail jest już ustawiony, to pierwszy warunek if (this.Thumbnail != null) by to obsłużył.
                // Więc tutaj prawdopodobnie Thumbnail jest null.
                SimpleFileLogger.Log($"ImageFileEntry.LoadThumbnailAsync: Już w trakcie ładowania dla '{FilePath}'.");
                return this.Thumbnail; // Zwróć aktualną wartość, która może być null
            }

            IsLoadingThumbnail = true;
            BitmapImage? loadedThumbnail = null;
            try
            {
                loadedThumbnail = await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(FilePath)) // Ponowne sprawdzenie w wątku roboczym
                        {
                            SimpleFileLogger.LogWarning($"ImageFileEntry.LoadThumbnailAsync (Task.Run): Plik nie znaleziony '{FilePath}' przed utworzeniem BitmapImage.");
                            return null;
                        }

                        BitmapImage bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(FilePath);
                        bmp.CacheOption = BitmapCacheOption.OnLoad; // Ładuj całą miniaturkę od razu
                        bmp.DecodePixelWidth = decodePixelWidth;   // Ustaw szerokość dekodowania
                        bmp.EndInit();
                        bmp.Freeze(); // Zamroź obiekt, aby można było go używać w innych wątkach (np. UI)
                        return bmp;
                    }
                    catch (FileNotFoundException)
                    {
                        SimpleFileLogger.LogWarning($"ImageFileEntry.LoadThumbnailAsync (Task.Run): FileNotFoundException dla '{FilePath}'.");
                        return null;
                    }
                    catch (Exception ex)
                    {
                        SimpleFileLogger.LogError($"Błąd tworzenia miniatury dla {FilePath} w Task.Run", ex);
                        return null;
                    }
                });

                // Ustaw właściwość Thumbnail tylko jeśli ładowanie się powiodło
                // i jesteśmy w oryginalnym kontekście synchronizacji (jeśli to konieczne)
                // lub po prostu ustawiamy ją, a PropertyChanged zadba o resztę.
                this.Thumbnail = loadedThumbnail;
            }
            catch (Exception ex)
            {
                // Ten catch jest bardziej na wypadek błędów w samym Task.Run lub await
                SimpleFileLogger.LogError($"Zewnętrzny błąd podczas operacji ładowania miniatury dla {FilePath}", ex);
                this.Thumbnail = null; // W razie błędu, upewnij się, że Thumbnail jest null
            }
            finally
            {
                IsLoadingThumbnail = false;
            }
            return this.Thumbnail; // Zwróć załadowaną (lub null) miniaturkę
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