// Plik: Models/CategoryProfile.cs
using CosplayManager.Services; // Dla SimpleFileLogger
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.ComponentModel; // Dla INotifyPropertyChanged
using System.Runtime.CompilerServices; // Dla CallerMemberName

namespace CosplayManager.Models
{
    public class CategoryProfile : INotifyPropertyChanged
    {
        private string _categoryName = string.Empty;
        public string CategoryName
        {
            get => _categoryName;
            set => SetProperty(ref _categoryName, value);
        }

        private float[]? _centroidEmbedding;
        public float[]? CentroidEmbedding
        {
            get => _centroidEmbedding;
            set => SetProperty(ref _centroidEmbedding, value);
        }

        private int _imageCountInProfile;
        public int ImageCountInProfile
        {
            get => _imageCountInProfile;
            set => SetProperty(ref _imageCountInProfile, value);
        }

        private List<string> _sourceImagePaths = new List<string>();
        public List<string> SourceImagePaths
        {
            get => _sourceImagePaths;
            set => SetProperty(ref _sourceImagePaths, value);
        }

        private int _pendingSuggestionsCount;
        [JsonIgnore]
        public int PendingSuggestionsCount
        {
            get => _pendingSuggestionsCount;
            set
            {
                if (SetProperty(ref _pendingSuggestionsCount, value))
                {
                    OnPropertyChanged(nameof(HasPendingSuggestions)); // Powiadom o zmianie HasPendingSuggestions
                }
            }
        }

        [JsonIgnore] // Pomocnicza właściwość dla XAML
        public bool HasPendingSuggestions => PendingSuggestionsCount > 0;


        public CategoryProfile()
        {
        }

        [JsonConstructor]
        public CategoryProfile(string categoryName, float[]? centroidEmbedding, int imageCountInProfile, List<string> sourceImagePaths)
        {
            CategoryName = categoryName ?? string.Empty;
            CentroidEmbedding = centroidEmbedding;
            ImageCountInProfile = imageCountInProfile;
            SourceImagePaths = sourceImagePaths ?? new List<string>();
            PendingSuggestionsCount = 0;
        }
        public CategoryProfile(string categoryName) : this(categoryName, null, 0, new List<string>())
        {
        }

        public void UpdateCentroid(List<float[]> embeddings, List<string> imagePathsForProfile)
        {
            if (embeddings == null || !embeddings.Any(e => e != null && e.Length > 0))
            {
                SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Brak poprawnych embeddingów do przetworzenia. Resetowanie profilu.");
                CentroidEmbedding = null;
                ImageCountInProfile = 0;
                SourceImagePaths = new List<string>();
                return;
            }

            var firstValidEmbedding = embeddings.First(e => e != null && e.Length > 0);
            if (firstValidEmbedding == null)
            {
                SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Pierwszy poprawny embedding jest null. Resetowanie profilu.");
                CentroidEmbedding = null;
                ImageCountInProfile = 0;
                SourceImagePaths = new List<string>();
                return;
            }
            int embeddingLength = firstValidEmbedding.Length;

            float[] sumVector = new float[embeddingLength];
            int validEmbeddingsCount = 0;
            List<string> actualSourcePathsUsed = new List<string>();

            for (int i = 0; i < embeddings.Count; i++)
            {
                var embedding = embeddings[i];
                if (embedding != null && embedding.Length == embeddingLength)
                {
                    for (int j = 0; j < embeddingLength; j++)
                    {
                        sumVector[j] += embedding[j];
                    }
                    validEmbeddingsCount++;
                    if (imagePathsForProfile != null && i < imagePathsForProfile.Count)
                    {
                        actualSourcePathsUsed.Add(imagePathsForProfile[i]);
                    }
                }
                else
                {
                    string pathInfo = (imagePathsForProfile != null && i < imagePathsForProfile.Count) ? imagePathsForProfile[i] : "N/A";
                    SimpleFileLogger.Log($"OSTRZEŻENIE (UpdateCentroid): Pusty lub niezgodna długość wektora cech dla obrazu '{pathInfo}' w profilu '{CategoryName}'. Embedding pominięty.");
                }
            }

            if (validEmbeddingsCount == 0)
            {
                SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Po filtrowaniu brak poprawnych embeddingów. Resetowanie profilu.");
                CentroidEmbedding = null;
                ImageCountInProfile = 0;
                SourceImagePaths = new List<string>();
                return;
            }

            var newCentroid = new float[embeddingLength];
            for (int i = 0; i < embeddingLength; i++)
            {
                newCentroid[i] = sumVector[i] / validEmbeddingsCount;
            }

            // Używamy SetProperty, aby zapewnić powiadomienia
            SetProperty(ref _centroidEmbedding, newCentroid, nameof(CentroidEmbedding));
            SetProperty(ref _imageCountInProfile, validEmbeddingsCount, nameof(ImageCountInProfile));
            SetProperty(ref _sourceImagePaths, actualSourcePathsUsed, nameof(SourceImagePaths));

            SimpleFileLogger.Log($"Profil '{CategoryName}' zaktualizowany. Użyto {ImageCountInProfile} obrazów. Zapisano {SourceImagePaths.Count} ścieżek źródłowych.");
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