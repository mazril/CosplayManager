// Plik: CosplayManager/Models/ImageFileEntry.cs
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization; // Potrzebne dla JsonIgnore

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

        // Właściwość tylko do odczytu dla długości wektora
        [JsonIgnore]
        public int FeatureVectorLength => _featureVector?.Length ?? 0;

        // Właściwość pomocnicza dla XAML, wskazująca czy wektor istnieje
        [JsonIgnore]
        public bool HasFeatureVector => _featureVector != null;

        // Właściwość pomocnicza dla XAML, wskazująca czy hash istnieje
        [JsonIgnore]
        public bool HasPerceptualHash => _perceptualHash != null;


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