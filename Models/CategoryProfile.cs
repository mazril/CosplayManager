// Plik: Models/CategoryProfile.cs
using CosplayManager.Services;
using CosplayManager.Utils; // Upewnij się, że ta przestrzeń nazw jest poprawna dla MathUtils
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CosplayManager.Models
{
    public class CategoryProfile
    {
        public string CategoryName { get; set; }

        // --- UPEWNIJ SIĘ, ŻE SETTER JEST PUBLICZNY ---
        public float[]? CentroidEmbedding { get; set; }
        // -------------------------------------------
        public List<string> SourceImagePaths { get; set; }
        public DateTime LastCalculatedUtc { get; set; }

        [JsonIgnore]
        public int ImageCountInProfile => SourceImagePaths?.Count ?? 0;

        [JsonIgnore]
        public bool HasSplitSuggestion { get; set; } = false;
        [JsonIgnore]
        public int PendingSuggestionsCount { get; set; } = 0;
        [JsonIgnore]
        public bool HasPendingSuggestions => PendingSuggestionsCount > 0;

        public CategoryProfile(string categoryName)
        {
            CategoryName = categoryName ?? throw new ArgumentNullException(nameof(categoryName));
            SourceImagePaths = new List<string>();
            LastCalculatedUtc = DateTime.UtcNow;
        }

        public void UpdateCentroid(List<float[]> imageEmbeddings, List<string> sourceImagePaths)
        {
            this.SourceImagePaths = new List<string>(sourceImagePaths ?? new List<string>());

            if (imageEmbeddings == null || !imageEmbeddings.Any() || !imageEmbeddings.All(e => e != null && e.Length > 0))
            {
                this.CentroidEmbedding = null;
                this.LastCalculatedUtc = DateTime.UtcNow;
                SimpleFileLogger.Log($"Profil '{CategoryName}' zaktualizowany, ale brak poprawnych embeddingów do obliczenia centroidu. Centroid ustawiony na null. Liczba ścieżek: {SourceImagePaths.Count}.");
                return;
            }

            var validEmbeddingsWithPaths = new List<(float[] Embedding, string Path)>();
            for (int i = 0; i < imageEmbeddings.Count; i++)
            {
                // Upewnij się, że imageEmbeddings[i] nie jest null przed dostępem do Length
                if (i < sourceImagePaths.Count && imageEmbeddings[i] != null && imageEmbeddings[i]!.Length > 0)
                {
                    validEmbeddingsWithPaths.Add((imageEmbeddings[i]!, sourceImagePaths[i]));
                }
            }

            if (!validEmbeddingsWithPaths.Any())
            {
                this.CentroidEmbedding = null;
                this.LastCalculatedUtc = DateTime.UtcNow;
                SimpleFileLogger.Log($"Profil '{CategoryName}' zaktualizowany, ale po filtracji brak poprawnych embeddingów. Centroid ustawiony na null. Liczba ścieżek: {SourceImagePaths.Count}.");
                return;
            }

            var initialEmbeddings = validEmbeddingsWithPaths.Select(ep => ep.Embedding).ToList();
            var initialPaths = validEmbeddingsWithPaths.Select(ep => ep.Path).ToList();

            // Wywołanie metody statycznej z MathUtils
            float[]? preliminaryCentroid = MathUtils.CalculateAverageEmbedding(initialEmbeddings!);
            if (preliminaryCentroid == null)
            {
                this.CentroidEmbedding = null;
                this.LastCalculatedUtc = DateTime.UtcNow;
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
                // Wywołanie metody statycznej z MathUtils
                this.CentroidEmbedding = MathUtils.CalculateAverageEmbedding(filteredEmbeddingsWithPaths.Select(ep => ep.Embedding).ToList()!);
                this.SourceImagePaths = filteredEmbeddingsWithPaths.Select(ep => ep.Path).ToList();
            }
            else
            {
                SimpleFileLogger.LogWarning($"UpdateCentroid dla '{CategoryName}': Wszystkie embeddingi zostały odrzucone jako skrajne. Używam wstępnego centroidu (lub null, jeśli nie było obrazów).");
                this.CentroidEmbedding = initialEmbeddings.Any() ? preliminaryCentroid : null;
                this.SourceImagePaths = initialPaths;
            }

            this.LastCalculatedUtc = DateTime.UtcNow;
            SimpleFileLogger.Log($"Profil '{CategoryName}' zaktualizowany. Finalny centroid obliczony z {(this.CentroidEmbedding != null && filteredEmbeddingsWithPaths.Any() ? filteredEmbeddingsWithPaths.Count : 0)} obrazów. Zapisano {SourceImagePaths.Count} ścieżek źródłowych.");
        }
    }
}