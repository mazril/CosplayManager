// Plik: Models/CategoryProfile.cs
using CosplayManager.Services;
using CosplayManager.Utils; // Upewnij się, że ta przestrzeń nazw jest poprawna dla MathUtils
using CosplayManager.ViewModels.Base; // <<< DODAJ TEN USING
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CosplayManager.Models
{
    // Zmieniono CategoryProfile, aby dziedziczyło z ObservableObject
    public class CategoryProfile : ObservableObject
    {



         
         public string GetCharacterName()
         {
            // Prosta implementacja - dostosuj, jeśli masz bardziej złożoną logikę w ProfileService
            var parts = CategoryName.Split(new[] { " - " }, 2, StringSplitOptions.None);
            return parts.Length > 1 ? parts[1].Trim() : (parts.Length == 1 && !string.IsNullOrWhiteSpace(parts[0]) ? "General" : CategoryName);
         }








        private string _categoryName;
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

        private List<string> _sourceImagePaths;
        public List<string> SourceImagePaths
        {
            get => _sourceImagePaths;
            set
            {
                if (SetProperty(ref _sourceImagePaths, value))
                {
                    OnPropertyChanged(nameof(ImageCountInProfile)); // Powiadom o zmianie ImageCountInProfile
                }
            }
        }

        private DateTime _lastCalculatedUtc;
        public DateTime LastCalculatedUtc
        {
            get => _lastCalculatedUtc;
            set => SetProperty(ref _lastCalculatedUtc, value);
        }

        [JsonIgnore]
        public int ImageCountInProfile => SourceImagePaths?.Count ?? 0;

        private bool _hasSplitSuggestion = false;
        [JsonIgnore]
        public bool HasSplitSuggestion
        {
            get => _hasSplitSuggestion;
            set => SetProperty(ref _hasSplitSuggestion, value);
        }

        private int _pendingSuggestionsCount = 0;
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

        [JsonIgnore]
        public bool HasPendingSuggestions => PendingSuggestionsCount > 0;

        // Konstruktor bezparametrowy dla deserializacji JSON, jeśli potrzebny
        public CategoryProfile() : this("Unknown")
        {
            // Ustawienie domyślnych wartości, jeśli CategoryName nie jest ustawiane przez deserializator
            _sourceImagePaths = new List<string>();
            _lastCalculatedUtc = DateTime.UtcNow;
        }


        public CategoryProfile(string categoryName)
        {
            _categoryName = categoryName ?? throw new ArgumentNullException(nameof(categoryName));
            _sourceImagePaths = new List<string>();
            _lastCalculatedUtc = DateTime.UtcNow;
        }

        public void UpdateCentroid(List<float[]> imageEmbeddings, List<string> sourceImagePaths)
        {
            // Użyj właściwości SourceImagePaths, aby setter został wywołany
            this.SourceImagePaths = new List<string>(sourceImagePaths ?? new List<string>());

            if (imageEmbeddings == null || !imageEmbeddings.Any() || !imageEmbeddings.All(e => e != null && e.Length > 0))
            {
                this.CentroidEmbedding = null; // Użyj właściwości
                this.LastCalculatedUtc = DateTime.UtcNow; // Użyj właściwości
                SimpleFileLogger.Log($"Profil '{CategoryName}' zaktualizowany, ale brak poprawnych embeddingów do obliczenia centroidu. Centroid ustawiony na null. Liczba ścieżek: {this.SourceImagePaths.Count}.");
                return;
            }

            var validEmbeddingsWithPaths = new List<(float[] Embedding, string Path)>();
            for (int i = 0; i < imageEmbeddings.Count; i++)
            {
                if (i < this.SourceImagePaths.Count && imageEmbeddings[i] != null && imageEmbeddings[i]!.Length > 0)
                {
                    validEmbeddingsWithPaths.Add((imageEmbeddings[i]!, this.SourceImagePaths[i]));
                }
            }

            if (!validEmbeddingsWithPaths.Any())
            {
                this.CentroidEmbedding = null; // Użyj właściwości
                this.LastCalculatedUtc = DateTime.UtcNow; // Użyj właściwości
                SimpleFileLogger.Log($"Profil '{CategoryName}' zaktualizowany, ale po filtracji brak poprawnych embeddingów. Centroid ustawiony na null. Liczba ścieżek: {this.SourceImagePaths.Count}.");
                return;
            }

            var initialEmbeddings = validEmbeddingsWithPaths.Select(ep => ep.Embedding).ToList();
            var initialPaths = validEmbeddingsWithPaths.Select(ep => ep.Path).ToList();

            float[]? preliminaryCentroid = MathUtils.CalculateAverageEmbedding(initialEmbeddings!);
            if (preliminaryCentroid == null)
            {
                this.CentroidEmbedding = null; // Użyj właściwości
                this.LastCalculatedUtc = DateTime.UtcNow; // Użyj właściwości
                SimpleFileLogger.LogWarning($"Nie udało się obliczyć wstępnego centroidu dla '{CategoryName}'.");
                return;
            }
            SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Wstępny centroid obliczony z {initialEmbeddings.Count} obrazów.");

            const double outlierRemovalSimilarityThreshold = 0.75;
            var filteredEmbeddingsWithPaths = new List<(float[] Embedding, string Path)>();
            int outliersRemoved = 0;

            for (int i = 0; i < initialEmbeddings.Count; i++)
            {
                if (initialEmbeddings[i] == null) continue;
                double similarity = MathUtils.CalculateCosineSimilarity(initialEmbeddings[i]!, preliminaryCentroid);
                if (similarity >= outlierRemovalSimilarityThreshold)
                {
                    filteredEmbeddingsWithPaths.Add((initialEmbeddings[i]!, initialPaths[i]));
                }
                else
                {
                    SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Odrzucono embedding dla obrazu '{initialPaths[i]}' jako skrajny (podobieństwo do wstępnego centroidu: {similarity:F4} < {outlierRemovalSimilarityThreshold}).");
                    outliersRemoved++;
                }
            }

            if (outliersRemoved > 0)
            {
                SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Odrzucono {outliersRemoved} skrajnych embeddingów. Pozostało {filteredEmbeddingsWithPaths.Count} do obliczenia finalnego centroidu.");
            }

            if (filteredEmbeddingsWithPaths.Any())
            {
                this.CentroidEmbedding = MathUtils.CalculateAverageEmbedding(filteredEmbeddingsWithPaths.Select(ep => ep.Embedding).ToList()!); // Użyj właściwości
                this.SourceImagePaths = filteredEmbeddingsWithPaths.Select(ep => ep.Path).ToList(); // Użyj właściwości
            }
            else
            {
                SimpleFileLogger.LogWarning($"UpdateCentroid dla '{CategoryName}': Wszystkie embeddingi zostały odrzucone jako skrajne. Używam wstępnego centroidu (lub null, jeśli nie było obrazów).");
                this.CentroidEmbedding = initialEmbeddings.Any() ? preliminaryCentroid : null; // Użyj właściwości
                this.SourceImagePaths = initialPaths; // Użyj właściwości
            }

            this.LastCalculatedUtc = DateTime.UtcNow; // Użyj właściwości
            SimpleFileLogger.Log($"Profil '{CategoryName}' zaktualizowany. Finalny centroid obliczony z {(this.CentroidEmbedding != null && filteredEmbeddingsWithPaths.Any() ? filteredEmbeddingsWithPaths.Count : 0)} obrazów. Zapisano {this.SourceImagePaths.Count} ścieżek źródłowych.");
        }
    }
}