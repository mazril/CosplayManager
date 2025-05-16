// File: Services/ProfileService.cs
using CosplayManager.Models;
using CosplayManager.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosplayManager.Services
{
    public class ProfileService
    {
        private List<CategoryProfile> _profiles;
        private readonly ClipServiceHttpClient _clipService;
        private readonly EmbeddingCacheService _embeddingCacheService; // Added
        private readonly string _profilesBaseFolderPath;

        private class ModelProfilesFileContent
        {
            public string ModelName { get; set; } = string.Empty;
            public List<CategoryProfile> CharacterProfiles { get; set; } = new List<CategoryProfile>();
        }

        // Modified constructor
        public ProfileService(ClipServiceHttpClient clipService, EmbeddingCacheService embeddingCacheService, string profilesFolderName = "CategoryProfiles")
        {
            _clipService = clipService ?? throw new ArgumentNullException(nameof(clipService));
            _embeddingCacheService = embeddingCacheService ?? throw new ArgumentNullException(nameof(embeddingCacheService)); // Added
            _profilesBaseFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, profilesFolderName);

            if (!Directory.Exists(_profilesBaseFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(_profilesBaseFolderPath);
                    SimpleFileLogger.Log($"Utworzono folder dla profili kategorii: {_profilesBaseFolderPath}");
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"Nie można utworzyć folderu dla profili kategorii: {_profilesBaseFolderPath}", ex);
                }
            }
            _profiles = new List<CategoryProfile>();
        }

        public IReadOnlyList<CategoryProfile> GetAllProfiles() => _profiles.AsReadOnly();

        public CategoryProfile? GetProfile(string categoryName)
        {
            return _profiles.FirstOrDefault(p =>
                p.CategoryName.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
        }

        public string GetModelNameFromCategory(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return "UnknownModel";
            var parts = categoryName.Split(new[] { " - " }, StringSplitOptions.None);
            return parts.Length > 0 ? parts[0].Trim() : categoryName.Trim();
        }

        public string GetCharacterNameFromCategory(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return "UnknownCharacter";
            var parts = categoryName.Split(new[] { " - " }, StringSplitOptions.None);
            return parts.Length > 1 ? parts[1].Trim() : (parts.Length == 1 ? "General" : "UnknownCharacter");
        }

        private string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "_";
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalidChar.ToString(), "_");
            }
            return name.Trim();
        }

        // Modified to use EmbeddingCacheService
        public async Task<float[]?> GetImageEmbeddingAsync(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                SimpleFileLogger.Log($"ProfileService.GetImageEmbeddingAsync: Nieprawidłowa ścieżka lub plik nie istnieje: {imagePath}");
                return null;
            }
            try
            {
                // Use the cache service here
                return await _embeddingCacheService.GetOrUpdateEmbeddingAsync(imagePath, async (path) =>
                {
                    SimpleFileLogger.Log($"Embedding provider called for: {path}");
                    return await _clipService.GetImageEmbeddingFromPathAsync(path);
                });
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"ProfileService.GetImageEmbeddingAsync: Błąd dla obrazu {imagePath}", ex);
                return null;
            }
        }

        public async Task GenerateProfileAsync(string categoryName, List<ImageFileEntry> imageFileEntries)
        {
            SimpleFileLogger.Log($"GenerateProfileAsync: Rozpoczęto dla kategorii '{categoryName}' z {imageFileEntries?.Count ?? 0} obrazami.");
            if (string.IsNullOrWhiteSpace(categoryName))
            {
                SimpleFileLogger.LogError("GenerateProfileAsync: Nazwa kategorii nie może być pusta.", null);
                throw new ArgumentException("Nazwa kategorii nie może być pusta.", nameof(categoryName));
            }

            string modelName = GetModelNameFromCategory(categoryName);

            if (imageFileEntries == null || !imageFileEntries.Any())
            {
                SimpleFileLogger.Log($"GenerateProfileAsync: Brak obrazów dla '{categoryName}'. Próba usunięcia/wyczyszczenia profilu.");
                CategoryProfile? existingProfile = GetProfile(categoryName);
                if (existingProfile != null)
                {
                    _profiles.Remove(existingProfile);
                    SimpleFileLogger.Log($"GenerateProfileAsync: Usunięto profil '{categoryName}' z pamięci.");
                    await SaveProfilesForModelAsync(modelName);
                }
                return;
            }

            List<string> imagePaths = imageFileEntries
                                     .Where(entry => !string.IsNullOrWhiteSpace(entry.FilePath) && File.Exists(entry.FilePath))
                                     .Select(entry => entry.FilePath)
                                     .ToList();

            if (!imagePaths.Any())
            {
                SimpleFileLogger.Log($"GenerateProfileAsync: Brak prawidłowych ścieżek obrazów dla '{categoryName}'.");
                CategoryProfile? existingProfileToClear = GetProfile(categoryName);
                if (existingProfileToClear != null)
                {
                    _profiles.Remove(existingProfileToClear);
                    SimpleFileLogger.Log($"GenerateProfileAsync: Usunięto profil '{categoryName}' z pamięci z powodu braku prawidłowych obrazów.");
                    await SaveProfilesForModelAsync(modelName);
                }
                return;
            }

            List<float[]> embeddings = new List<float[]>();
            List<string> validImagePathsForProfile = new List<string>();

            foreach (var imagePath in imagePaths)
            {
                // This call will now use the cache
                float[]? embedding = await GetImageEmbeddingAsync(imagePath);
                if (embedding != null)
                {
                    embeddings.Add(embedding);
                    validImagePathsForProfile.Add(imagePath);
                }
                else
                {
                    SimpleFileLogger.Log($"GenerateProfileAsync: Nie udało się uzyskać wektora cech dla obrazu: {imagePath} w kategorii {categoryName}");
                }
            }

            if (embeddings.Count == 0)
            {
                SimpleFileLogger.Log($"GenerateProfileAsync: Nie udało się wygenerować żadnych wektorów cech dla '{categoryName}'. Profil nie został utworzony/zaktualizowany.");
                CategoryProfile? existingProfileToClearNoEmbeddings = GetProfile(categoryName);
                if (existingProfileToClearNoEmbeddings != null)
                {
                    _profiles.Remove(existingProfileToClearNoEmbeddings);
                    SimpleFileLogger.Log($"GenerateProfileAsync: Usunięto profil '{categoryName}' z pamięci z powodu braku embeddingów.");
                    await SaveProfilesForModelAsync(modelName);
                }
                return;
            }

            CategoryProfile? profile = GetProfile(categoryName);
            if (profile == null)
            {
                profile = new CategoryProfile(categoryName);
                _profiles.Add(profile);
                SimpleFileLogger.Log($"GenerateProfileAsync: Utworzono nowy obiekt profilu '{categoryName}' w pamięci.");
            }
            profile.UpdateCentroid(embeddings, validImagePathsForProfile);

            await SaveProfilesForModelAsync(modelName);
            SimpleFileLogger.Log($"GenerateProfileAsync: Zakończono dla kategorii '{categoryName}'. Profil zaktualizowany/utworzony i zapisany dla modelki '{modelName}'.");
        }

        public async Task SaveProfilesForModelAsync(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                SimpleFileLogger.LogError("SaveProfilesForModelAsync: Nazwa modelki nie może być pusta.", null);
                return;
            }

            string sanitizedModelName = SanitizeFileName(modelName);
            string modelProfilePath = Path.Combine(_profilesBaseFolderPath, $"{sanitizedModelName}.json");

            List<CategoryProfile> profilesForThisModel = _profiles
                .Where(p => GetModelNameFromCategory(p.CategoryName).Equals(modelName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!profilesForThisModel.Any())
            {
                if (File.Exists(modelProfilePath))
                {
                    try
                    {
                        File.Delete(modelProfilePath);
                        SimpleFileLogger.Log($"SaveProfilesForModelAsync: Usunięto plik profilu dla modelki '{modelName}', ponieważ nie ma już dla niej postaci: {modelProfilePath}");
                    }
                    catch (Exception ex)
                    {
                        SimpleFileLogger.LogError($"SaveProfilesForModelAsync: Błąd podczas usuwania pliku profilu dla modelki '{modelName}': {modelProfilePath}", ex);
                    }
                }
                return;
            }

            ModelProfilesFileContent fileContent = new ModelProfilesFileContent
            {
                ModelName = modelName,
                CharacterProfiles = profilesForThisModel
            };

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                string jsonString = JsonSerializer.Serialize(fileContent, options);
                await File.WriteAllTextAsync(modelProfilePath, jsonString);
                SimpleFileLogger.Log($"SaveProfilesForModelAsync: Profile dla modelki '{modelName}' zapisane do: {modelProfilePath} (Liczba postaci: {profilesForThisModel.Count})");
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"SaveProfilesForModelAsync: Błąd podczas zapisywania profili dla modelki '{modelName}' do '{modelProfilePath}'", ex);
            }
        }

        public async Task SaveAllProfilesAsync()
        {
            SimpleFileLogger.Log("SaveAllProfilesAsync: Rozpoczęto zapisywanie wszystkich profili.");
            if (!_profiles.Any())
            {
                SimpleFileLogger.Log("SaveAllProfilesAsync: Brak profili w pamięci do zapisania.");
                return;
            }

            var profilesByModel = _profiles.GroupBy(p => GetModelNameFromCategory(p.CategoryName));
            int modelsSavedCount = 0;
            foreach (var group in profilesByModel)
            {
                await SaveProfilesForModelAsync(group.Key);
                modelsSavedCount++;
            }
            SimpleFileLogger.Log($"SaveAllProfilesAsync: Zakończono. Zapisano profile dla {modelsSavedCount} modelek.");

            // Save the embedding cache when all profiles are saved
            _embeddingCacheService.SaveCacheToFile();
        }

        public async Task LoadProfilesAsync()
        {
            _profiles.Clear();
            SimpleFileLogger.Log($"LoadProfilesAsync: Wyczyczono listę profili w pamięci. Rozpoczęto ładowanie z folderu: {_profilesBaseFolderPath}");

            if (!Directory.Exists(_profilesBaseFolderPath))
            {
                SimpleFileLogger.Log($"LoadProfilesAsync: Folder profili nie istnieje: {_profilesBaseFolderPath}. Inicjalizuję pustą listę profili.");
                return;
            }

            int filesProcessed = 0;
            int profilesLoadedTotal = 0;
            foreach (string filePath in Directory.EnumerateFiles(_profilesBaseFolderPath, "*.json"))
            {
                filesProcessed++;
                try
                {
                    string jsonString = await File.ReadAllTextAsync(filePath);
                    ModelProfilesFileContent? fileContent = JsonSerializer.Deserialize<ModelProfilesFileContent>(jsonString);

                    if (fileContent?.CharacterProfiles != null && fileContent.CharacterProfiles.Any())
                    {
                        foreach (var profile in fileContent.CharacterProfiles)
                        {
                            // Ensure CategoryName is fully qualified with ModelName
                            // This logic ensures that if a profile was saved with a partial name, it gets corrected.
                            // It's important especially if CategoryName was manually edited or came from an older format.
                            string expectedPrefix = $"{fileContent.ModelName} - ";
                            if (!profile.CategoryName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) &&
                                !profile.CategoryName.Equals(fileContent.ModelName, StringComparison.OrdinalIgnoreCase)) // Handles cases where character name might be "General" or similar
                            {
                                // Check if the profile category name is just the character part
                                string characterPart = GetCharacterNameFromCategory(profile.CategoryName);
                                if (characterPart.Equals(profile.CategoryName, StringComparison.OrdinalIgnoreCase)) // It's only the character part
                                {
                                    profile.CategoryName = $"{fileContent.ModelName} - {profile.CategoryName}";
                                }
                                // else, the name might be complex or already correct but not matching model name, log it
                                else if (!GetModelNameFromCategory(profile.CategoryName).Equals(fileContent.ModelName, StringComparison.OrdinalIgnoreCase))
                                {
                                    SimpleFileLogger.LogWarning($"LoadProfilesAsync: Profile '{profile.CategoryName}' in file '{Path.GetFileName(filePath)}' for model '{fileContent.ModelName}' has a mismatched model prefix. Attempting to correct to '{fileContent.ModelName} - {characterPart}'.");
                                    profile.CategoryName = $"{fileContent.ModelName} - {characterPart}";
                                }
                                // If it already contains " - " but the model part is wrong, it's a more complex issue.
                                // The above logic tries a simple correction.
                            }
                            else if (profile.CategoryName.Equals(fileContent.ModelName, StringComparison.OrdinalIgnoreCase)) // if category is just model name, assume "General"
                            {
                                profile.CategoryName = $"{fileContent.ModelName} - General";
                            }


                            _profiles.Add(profile);
                            profilesLoadedTotal++;
                        }
                        SimpleFileLogger.Log($"LoadProfilesAsync: Załadowano {fileContent.CharacterProfiles.Count} profili postaci dla modelki '{fileContent.ModelName}' z pliku: {Path.GetFileName(filePath)}");
                    }
                    else
                    {
                        SimpleFileLogger.Log($"LoadProfilesAsync: Plik '{Path.GetFileName(filePath)}' nie zawierał profili postaci lub był niepoprawny.");
                    }
                }
                catch (JsonException jsonEx)
                {
                    SimpleFileLogger.LogError($"LoadProfilesAsync: Błąd deserializacji JSON z pliku: {Path.GetFileName(filePath)}", jsonEx);
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"LoadProfilesAsync: Błąd podczas ładowania profili z pliku: {Path.GetFileName(filePath)}", ex);
                }
            }
            SimpleFileLogger.Log($"LoadProfilesAsync: Zakończono ładowanie. Przetworzono {filesProcessed} plików. Załadowano łącznie {profilesLoadedTotal} profili kategorii.");
        }

        public Tuple<CategoryProfile, double>? SuggestCategory(float[] imageEmbedding, double similarityThreshold = 0.80, string? targetModelName = null)
        {
            if (imageEmbedding == null || !_profiles.Any())
            {
                return null;
            }

            IEnumerable<CategoryProfile> profilesToSearch = _profiles;
            if (!string.IsNullOrWhiteSpace(targetModelName))
            {
                profilesToSearch = _profiles.Where(p => GetModelNameFromCategory(p.CategoryName).Equals(targetModelName, StringComparison.OrdinalIgnoreCase));
            }
            if (!profilesToSearch.Any())
            {
                return null;
            }

            CategoryProfile? bestMatchProfile = null;
            double highestSimilarity = -1.0;

            foreach (var profile in profilesToSearch)
            {
                if (profile.CentroidEmbedding == null) continue;

                double similarity = MathUtils.CalculateCosineSimilarity(imageEmbedding, profile.CentroidEmbedding);
                if (similarity > highestSimilarity)
                {
                    highestSimilarity = similarity;
                    bestMatchProfile = profile;
                }
            }

            if (bestMatchProfile != null && highestSimilarity >= similarityThreshold)
            {
                return Tuple.Create(bestMatchProfile, highestSimilarity);
            }
            return null;
        }

        public async Task<bool> RemoveProfileAsync(string categoryName)
        {
            SimpleFileLogger.Log($"RemoveProfileAsync: Próba usunięcia profilu '{categoryName}'.");
            var profileToRemove = GetProfile(categoryName);
            if (profileToRemove != null)
            {
                string modelName = GetModelNameFromCategory(categoryName);
                bool removedFromMemory = _profiles.Remove(profileToRemove);
                if (removedFromMemory)
                {
                    SimpleFileLogger.Log($"RemoveProfileAsync: Usunięto profil '{categoryName}' z pamięci.");
                    await SaveProfilesForModelAsync(modelName); // Zapisz zmiany dla tego modelu (lub usuń plik, jeśli to był ostatni profil)
                    return true;
                }
            }
            SimpleFileLogger.Log($"RemoveProfileAsync: Nie znaleziono profilu '{categoryName}' w pamięci do usunięcia.");
            return false;
        }

        public async Task<bool> RemoveAllProfilesForModelAsync(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                SimpleFileLogger.LogError("RemoveAllProfilesForModelAsync: Nazwa modelki nie może być pusta.", null);
                return false;
            }

            SimpleFileLogger.Log($"RemoveAllProfilesForModelAsync: Próba usunięcia wszystkich profili i pliku dla modelki '{modelName}'.");

            int removedCount = _profiles.RemoveAll(p => GetModelNameFromCategory(p.CategoryName).Equals(modelName, StringComparison.OrdinalIgnoreCase));
            SimpleFileLogger.Log($"RemoveAllProfilesForModelAsync: Usunięto {removedCount} profili postaci dla modelki '{modelName}' z pamięci.");

            string sanitizedModelName = SanitizeFileName(modelName);
            string modelProfilePath = Path.Combine(_profilesBaseFolderPath, $"{sanitizedModelName}.json");

            if (File.Exists(modelProfilePath))
            {
                try
                {
                    File.Delete(modelProfilePath);
                    SimpleFileLogger.Log($"RemoveAllProfilesForModelAsync: Pomyślnie usunięto plik profilu: {modelProfilePath}");
                    return true;
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"RemoveAllProfilesForModelAsync: Błąd podczas usuwania pliku profilu '{modelProfilePath}'.", ex);
                    return false;
                }
            }
            else
            {
                SimpleFileLogger.Log($"RemoveAllProfilesForModelAsync: Plik profilu '{modelProfilePath}' nie istniał. Nic do usunięcia z dysku.");
                return removedCount > 0;
            }
        }
    }
}