// Plik: Models/CategoryProfile.cs
using CosplayManager.Services; // Dla SimpleFileLogger
using CosplayManager.Utils;   // Potrzebne dla MathUtils
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CosplayManager.Models
{
    public class CategoryProfile : INotifyPropertyChanged
    {
        // ... (istniejące właściwości: CategoryName, CentroidEmbedding, ImageCountInProfile, SourceImagePaths, PendingSuggestionsCount, HasPendingSuggestions) ...
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
                    OnPropertyChanged(nameof(HasPendingSuggestions));
                }
            }
        }

        [JsonIgnore]
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

        // ZMODYFIKOWANA METODA UpdateCentroid
        public void UpdateCentroid(List<float[]> allEmbeddings, List<string> allImagePaths, double outlierSimilarityThreshold = 0.75)
        {
            if (allEmbeddings == null || !allEmbeddings.Any(e => e != null && e.Length > 0))
            {
                SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Brak poprawnych embeddingów do przetworzenia. Resetowanie profilu.");
                CentroidEmbedding = null;
                ImageCountInProfile = 0;
                SourceImagePaths = new List<string>();
                return;
            }

            var validInitialEmbeddings = new List<(float[] Embedding, string Path)>();
            int embeddingLength = 0;

            for (int i = 0; i < allEmbeddings.Count; i++)
            {
                var embedding = allEmbeddings[i];
                if (embedding != null && embedding.Length > 0)
                {
                    if (embeddingLength == 0) embeddingLength = embedding.Length;
                    if (embedding.Length == embeddingLength) // Upewnij się, że wszystkie wektory mają tę samą długość
                    {
                        validInitialEmbeddings.Add((embedding, allImagePaths[i]));
                    }
                    else
                    {
                        SimpleFileLogger.LogWarning($"UpdateCentroid dla '{CategoryName}': Pominięto embedding dla obrazu '{allImagePaths[i]}' z powodu niezgodnej długości ({embedding.Length} vs oczekiwano {embeddingLength}).");
                    }
                }
            }

            if (!validInitialEmbeddings.Any())
            {
                SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Po wstępnej walidacji brak poprawnych embeddingów. Resetowanie profilu.");
                CentroidEmbedding = null;
                ImageCountInProfile = 0;
                SourceImagePaths = new List<string>();
                return;
            }

            // Krok 1: Oblicz wstępny centroid ze wszystkich poprawnych embeddingów
            float[]? preliminaryCentroid = CalculateAverageEmbedding(validInitialEmbeddings.Select(item => item.Embedding).ToList());

            if (preliminaryCentroid == null || preliminaryCentroid.Length == 0)
            {
                SimpleFileLogger.LogError($"UpdateCentroid dla '{CategoryName}': Nie udało się obliczyć wstępnego centroidu. Używam wszystkich embeddingów bez filtrowania skrajnych.", null);
                // W przypadku błędu, wracamy do starej logiki - uśredniania wszystkiego co poprawne
                SetProfileData(validInitialEmbeddings.Select(item => item.Embedding).ToList(), validInitialEmbeddings.Select(item => item.Path).ToList());
                return;
            }

            SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Wstępny centroid obliczony z {validInitialEmbeddings.Count} obrazów.");

            // Krok 2 i 3: Zidentyfikuj i odfiltruj skrajne wektory
            var filteredEmbeddingsWithPaths = new List<(float[] Embedding, string Path)>();
            int outliersCount = 0;

            if (validInitialEmbeddings.Count <= 2) // Jeśli mamy bardzo mało obrazów, nie odrzucaj skrajnych, bo centroid może być niestabilny
            {
                SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Mniej niż 3 obrazy ({validInitialEmbeddings.Count}), pomijanie filtrowania skrajnych wektorów.");
                filteredEmbeddingsWithPaths.AddRange(validInitialEmbeddings);
            }
            else
            {
                foreach (var item in validInitialEmbeddings)
                {
                    double similarityToPreliminaryCentroid = MathUtils.CalculateCosineSimilarity(item.Embedding, preliminaryCentroid);
                    if (similarityToPreliminaryCentroid >= outlierSimilarityThreshold)
                    {
                        filteredEmbeddingsWithPaths.Add(item);
                    }
                    else
                    {
                        outliersCount++;
                        SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Odrzucono embedding dla obrazu '{item.Path}' jako skrajny (podobieństwo do wstępnego centroidu: {similarityToPreliminaryCentroid:F4} < {outlierSimilarityThreshold:F2}).");
                    }
                }
            }


            if (!filteredEmbeddingsWithPaths.Any())
            {
                SimpleFileLogger.LogWarning($"UpdateCentroid dla '{CategoryName}': Po filtrowaniu skrajnych nie pozostały żadne embeddingi (odrzucono {outliersCount}). Używam wstępnego centroidu z {validInitialEmbeddings.Count} obrazów.");
                // Jeśli wszystko zostało odrzucone, to coś jest nie tak. Lepiej użyć wstępnego centroidu.
                SetProfileData(validInitialEmbeddings.Select(item => item.Embedding).ToList(), validInitialEmbeddings.Select(item => item.Path).ToList());
            }
            else
            {
                SimpleFileLogger.Log($"UpdateCentroid dla '{CategoryName}': Odrzucono {outliersCount} skrajnych embeddingów. Pozostało {filteredEmbeddingsWithPaths.Count} do obliczenia finalnego centroidu.");
                // Krok 4: Oblicz ostateczny centroid z odfiltrowanych wektorów
                SetProfileData(filteredEmbeddingsWithPaths.Select(item => item.Embedding).ToList(), filteredEmbeddingsWithPaths.Select(item => item.Path).ToList());
            }
        }

        private float[]? CalculateAverageEmbedding(List<float[]> embeddings)
        {
            if (embeddings == null || !embeddings.Any() || embeddings[0] == null) return null;

            int embeddingLength = embeddings[0].Length;
            float[] sumVector = new float[embeddingLength];
            int validCount = 0;

            foreach (var embedding in embeddings)
            {
                if (embedding != null && embedding.Length == embeddingLength)
                {
                    for (int j = 0; j < embeddingLength; j++)
                    {
                        sumVector[j] += embedding[j];
                    }
                    validCount++;
                }
            }

            if (validCount == 0) return null;

            var averageVector = new float[embeddingLength];
            for (int i = 0; i < embeddingLength; i++)
            {
                averageVector[i] = sumVector[i] / validCount;
            }
            return averageVector;
        }

        private void SetProfileData(List<float[]> finalEmbeddings, List<string> finalImagePaths)
        {
            if (finalEmbeddings == null || !finalEmbeddings.Any())
            {
                SimpleFileLogger.Log($"SetProfileData dla '{CategoryName}': Brak embeddingów do ustawienia. Resetowanie profilu.");
                CentroidEmbedding = null;
                ImageCountInProfile = 0;
                SourceImagePaths = new List<string>();
                return;
            }

            float[]? newCentroid = CalculateAverageEmbedding(finalEmbeddings);

            SetProperty(ref _centroidEmbedding, newCentroid, nameof(CentroidEmbedding));
            SetProperty(ref _imageCountInProfile, finalEmbeddings.Count, nameof(ImageCountInProfile)); // Liczba obrazów użytych do finalnego centroidu
            SetProperty(ref _sourceImagePaths, finalImagePaths, nameof(SourceImagePaths)); // Ścieżki obrazów użytych do finalnego centroidu

            SimpleFileLogger.Log($"Profil '{CategoryName}' zaktualizowany. Finalny centroid obliczony z {ImageCountInProfile} obrazów. Zapisano {SourceImagePaths.Count} ścieżek źródłowych.");
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