// Plik: CosplayManager/Models/ImageFileEntry.cs
using CosplayManager.Services;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System;

namespace CosplayManager.Models
{
    public class ImageFileEntry : INotifyPropertyChanged // Zmieniono na public
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

        [JsonIgnore]
        public int FeatureVectorLength => _featureVector?.Length ?? 0;

        [JsonIgnore]
        public bool HasFeatureVector => _featureVector != null;

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
        public bool IsLoadingThumbnail // Pozostawiam public, aby ProgressBar w XAML mógł się bindować
        {
            get => _isLoadingThumbnail;
            private set => SetProperty(ref _isLoadingThumbnail, value);
        }

        public async Task LoadThumbnailAsync(int decodePixelWidth = 150)
        {
            if (Thumbnail != null || string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            {
                if (Thumbnail == null && (!string.IsNullOrWhiteSpace(FilePath) && !File.Exists(FilePath)))
                {
                    SimpleFileLogger.LogWarning($"LoadThumbnailAsync: Plik źródłowy nie istnieje dla '{FilePath}', miniatura nie zostanie załadowana.");
                }
                return;
            }

            if (IsLoadingThumbnail) return; // Nie ładuj, jeśli już jest w trakcie

            IsLoadingThumbnail = true;
            try
            {
                BitmapImage? tempThumbnail = await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(FilePath))
                        {
                            SimpleFileLogger.Log($"ImageFileEntry.LoadThumbnailAsync (Task.Run): Plik nie znaleziony '{FilePath}'");
                            return null;
                        }

                        BitmapImage bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(FilePath);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = decodePixelWidth;
                        bmp.EndInit();
                        bmp.Freeze(); // Ważne dla użycia w innym wątku (UI)
                        return bmp;
                    }
                    catch (Exception ex)
                    {
                        SimpleFileLogger.LogError($"Błąd tworzenia miniatury dla {FilePath} w Task.Run", ex);
                        return null;
                    }
                });
                Thumbnail = tempThumbnail;
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Zewnętrzny błąd ładowania miniatury dla {FilePath}", ex);
                Thumbnail = null; // W razie błędu, upewnij się, że thumbnail jest null
            }
            finally
            {
                IsLoadingThumbnail = false;
            }
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