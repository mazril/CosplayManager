// Plik: Services/ProfileManager.cs
using CosplayManager.Models; // Założenie, że CategoryProfile jest tutaj
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks; // Dla potencjalnych operacji asynchronicznych

namespace CosplayManager.Services
{
    public class ProfileManager
    {
        private List<CategoryProfile> _profiles;
        private readonly ClipServiceHttpClient _clipService; // Potrzebne do pobierania embeddingów

        public ProfileManager(ClipServiceHttpClient clipService)
        {
            _clipService = clipService ?? throw new ArgumentNullException(nameof(clipService));
            _profiles = new List<CategoryProfile>();
            // Tutaj można by załadować profile z pliku przy starcie
        }

        public IReadOnlyList<CategoryProfile> GetAllProfiles() => _profiles.AsReadOnly();

        public CategoryProfile GetProfile(string categoryName)
        {
            return _profiles.FirstOrDefault(p => p.CategoryName.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Tworzy nowy profil lub aktualizuje istniejący na podstawie listy ścieżek do obrazów.
        /// </summary>
        public async Task CreateOrUpdateProfileAsync(string categoryName, List<string> imagePaths)
        {
            if (imagePaths == null || imagePaths.Count == 0)
            {
                Console.WriteLine($"Brak obrazów do utworzenia profilu dla kategorii: {categoryName}");
                // Można usunąć profil, jeśli istnieje i lista jest pusta
                var existingEmptyProfile = GetProfile(categoryName);
                if (existingEmptyProfile != null) _profiles.Remove(existingEmptyProfile);
                return;
            }

            List<float[]> embeddings = new List<float[]>();
            foreach (var imagePath in imagePaths)
            {
                try
                {
                    // Upewnij się, że _clipService jest gotowy i serwer działa
                    // Metoda GetImageEmbeddingFromPathAsync w ClipServiceHttpClient powinna to obsługiwać
                    float[] embedding = await _clipService.GetImageEmbeddingFromPathAsync(imagePath);
                    if (embedding != null)
                    {
                        embeddings.Add(embedding);
                    }
                    else
                    {
                        Console.WriteLine($"Nie udało się uzyskać wektora cech dla obrazu: {imagePath} w kategorii {categoryName}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Błąd podczas pobierania wektora cech dla {imagePath}: {ex.Message}");
                    // Rozważ, czy przerwać, czy kontynuować z pozostałymi obrazami
                }
            }

            if (embeddings.Count == 0)
            {
                Console.WriteLine($"Nie udało się wygenerować żadnych wektorów cech dla kategorii: {categoryName}. Profil nie został utworzony/zaktualizowany.");
                return;
            }

            CategoryProfile profile = GetProfile(categoryName);
            if (profile == null)
            {
                profile = new CategoryProfile(categoryName);
                _profiles.Add(profile);
            }
            profile.UpdateCentroid(embeddings, imagePaths);
            Console.WriteLine($"Profil dla kategorii '{categoryName}' został utworzony/zaktualizowany przy użyciu {profile.ImageCountInProfile} obrazów.");
        }

        /// <summary>
        /// Sugeruje kategorię dla danego wektora cech obrazu.
        /// </summary>
        /// <returns>Nazwę najlepiej pasującej kategorii i podobieństwo, lub null jeśli nic nie pasuje.</returns>
        public Tuple<string, double> SuggestCategory(float[] imageEmbedding, double similarityThreshold = 0.80) // Domyślny próg 0.80
        {
            if (imageEmbedding == null || _profiles.Count == 0)
            {
                return null;
            }

            string bestCategory = null;
            double highestSimilarity = -1.0; // Podobieństwo kosinusowe jest w zakresie [-1, 1]

            foreach (var profile in _profiles)
            {
                if (profile.CentroidEmbedding == null) continue;

                double similarity = Utils.MathUtils.CalculateCosineSimilarity(imageEmbedding, profile.CentroidEmbedding);
                Console.WriteLine($"Podobieństwo do profilu '{profile.CategoryName}': {similarity:F4}");
                if (similarity > highestSimilarity)
                {
                    highestSimilarity = similarity;
                    bestCategory = profile.CategoryName;
                }
            }

            if (bestCategory != null && highestSimilarity >= similarityThreshold)
            {
                return Tuple.Create(bestCategory, highestSimilarity);
            }
            return null; // Brak wystarczająco dobrego dopasowania
        }


        // Metody do zapisu/odczytu profili (np. do pliku JSON)
        public void SaveProfiles(string filePath)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(_profiles, options);
                File.WriteAllText(filePath, jsonString);
                Console.WriteLine($"Profile zapisane do: {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Błąd podczas zapisywania profili: {ex.Message}");
            }
        }

        public void LoadProfiles(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Plik profili nie istnieje: {filePath}");
                _profiles = new List<CategoryProfile>(); // Zainicjuj pustą listą, jeśli plik nie istnieje
                return;
            }
            try
            {
                string jsonString = File.ReadAllText(filePath);
                _profiles = JsonSerializer.Deserialize<List<CategoryProfile>>(jsonString);
                if (_profiles == null) _profiles = new List<CategoryProfile>(); // Upewnij się, że lista nie jest null
                Console.WriteLine($"Profile załadowane z: {filePath}. Załadowano {_profiles.Count} profili.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Błąd podczas ładowania profili: {ex.Message}");
                _profiles = new List<CategoryProfile>(); // W razie błędu, zacznij z pustą listą
            }
        }
    }
}