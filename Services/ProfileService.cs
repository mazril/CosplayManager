// Plik: Services/ProfileService.cs
using CosplayManager.Models;
using CosplayManager.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosplayManager.Services
{
    public class ProfileService
    {
        private List<CategoryProfile> _profiles;
        private readonly ClipServiceHttpClient? _clipService; // Może być null, jeśli serwer nie działa
        private readonly EmbeddingCacheServiceSQLite _embeddingCacheService;
        private readonly string _profilesBaseFolderPath;
        private const int MAX_CONCURRENT_EMBEDDINGS_PER_PROFILE_GENERATION = 1; // Pozostawiamy na 1 dla stabilności

        // --- DODANO: Słownik obiektów blokad dla zapisu plików profili modelek ---
        private readonly ConcurrentDictionary<string, object> _modelFileLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        // ----------------------------------------------------------------------

        private class ModelProfilesFileContent
        {
            public string ModelName { get; set; } = string.Empty;
            public List<CategoryProfile> CharacterProfiles { get; set; } = new List<CategoryProfile>();
        }

        // Zezwól na null dla clipService, jeśli serwer Pythona nie jest krytyczny dla każdej operacji
        public ProfileService(ClipServiceHttpClient? clipService, EmbeddingCacheServiceSQLite embeddingCacheService, string profilesFolderName = "CategoryProfiles")
        {
            _clipService = clipService; // Nie rzucaj wyjątku, jeśli jest null
            _embeddingCacheService = embeddingCacheService ?? throw new ArgumentNullException(nameof(embeddingCacheService));
            _profilesBaseFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, profilesFolderName);

            if (!Directory.Exists(_profilesBaseFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(_profilesBaseFolderPath);
                    SimpleFileLogger.LogHighLevelInfo($"Utworzono folder dla profili kategorii: {_profilesBaseFolderPath}");
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"Nie można utworzyć folderu dla profili kategorii: {_profilesBaseFolderPath}", ex);
                }
            }
            _profiles = new List<CategoryProfile>();
        }

        // ... (GetProfile, GetModelNameFromCategory, GetCharacterNameFromCategory, SanitizeFileName bez zmian) ...
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
            return parts.Length > 1 ? string.Join(" - ", parts.Skip(1)).Trim() : (parts.Length == 1 ? "General" : "UnknownCharacter");
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


        public async Task<float[]?> GetImageEmbeddingAsync(ImageFileEntry imageEntry)
        {
            if (_clipService == null) // Sprawdzenie, czy clipService jest dostępny
            {
                SimpleFileLogger.LogWarning($"GetImageEmbeddingAsync: ClipService jest null. Nie można pobrać embeddingu dla {imageEntry?.FilePath}.");
                return null;
            }
            if (imageEntry == null || string.IsNullOrWhiteSpace(imageEntry.FilePath))
            {
                SimpleFileLogger.Log($"ProfileService.GetImageEmbeddingAsync: Nieprawidłowy ImageFileEntry lub ścieżka.");
                return null;
            }
            // Dodatkowe logowanie wartości przekazywanych do cache'u
            SimpleFileLogger.Log($"GetImageEmbeddingAsync dla '{imageEntry.FilePath}': CurrentModUTC={imageEntry.FileLastModifiedUtc:o}, CurrentSize={imageEntry.FileSize}");

            try
            {
                return await _embeddingCacheService.GetOrUpdateEmbeddingAsync(
                    imageEntry.FilePath,
                    imageEntry.FileLastModifiedUtc,
                    imageEntry.FileSize,
                    async (path) =>
                    {
                        SimpleFileLogger.Log($"Embedding provider (cache miss/invalid) called for: {path}");
                        if (_clipService == null) return null; // Ponowne sprawdzenie na wszelki wypadek
                        return await _clipService.GetImageEmbeddingFromPathAsync(path);
                    });
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"ProfileService.GetImageEmbeddingAsync: Błąd dla obrazu {imageEntry.FilePath}", ex);
                return null;
            }
        }

        public async Task GenerateProfileAsync(string categoryName, List<ImageFileEntry> imageFileEntries)
        {
            SimpleFileLogger.LogHighLevelInfo($"GenerateProfileAsync: Rozpoczęto dla kategorii '{categoryName}' z {imageFileEntries?.Count ?? 0} obrazami.");
            if (string.IsNullOrWhiteSpace(categoryName))
            {
                SimpleFileLogger.LogError("GenerateProfileAsync: Nazwa kategorii nie może być pusta.", null);
                throw new ArgumentException("Nazwa kategorii nie może być pusta.", nameof(categoryName));
            }

            string modelName = GetModelNameFromCategory(categoryName);

            var validImageFileEntries = imageFileEntries?
                                     .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.FilePath) && File.Exists(entry.FilePath))
                                     .ToList() ?? new List<ImageFileEntry>();

            if (!validImageFileEntries.Any())
            {
                SimpleFileLogger.Log($"GenerateProfileAsync: Brak prawidłowych obrazów dla '{categoryName}'. Próba usunięcia/wyczyszczenia profilu.");
                CategoryProfile? existingProfile = GetProfile(categoryName);
                if (existingProfile != null)
                {
                    lock (_profiles) // Zabezpiecz modyfikację listy _profiles
                    {
                        _profiles.Remove(existingProfile);
                    }
                    SimpleFileLogger.Log($"GenerateProfileAsync: Usunięto profil '{categoryName}' z pamięci.");
                    await SaveProfilesForModelAsync(modelName);
                }
                return;
            }

            var embeddingsWithPaths = new ConcurrentBag<(float[] Embedding, string Path)>();
            if (_clipService != null) // Pobieraj embeddingi tylko jeśli _clipService jest dostępny
            {
                using (var localEmbeddingSemaphore = new SemaphoreSlim(MAX_CONCURRENT_EMBEDDINGS_PER_PROFILE_GENERATION))
                {
                    var embeddingTasks = new List<Task>();
                    foreach (var entry in validImageFileEntries)
                    {
                        embeddingTasks.Add(Task.Run(async () =>
                        {
                            await localEmbeddingSemaphore.WaitAsync();
                            try
                            {
                                float[]? embedding = await GetImageEmbeddingAsync(entry);
                                if (embedding != null)
                                {
                                    embeddingsWithPaths.Add((embedding, entry.FilePath));
                                }
                                else
                                {
                                    SimpleFileLogger.LogWarning($"GenerateProfileAsync: Nie udało się uzyskać wektora cech dla obrazu: {entry.FilePath} w kategorii {categoryName}");
                                }
                            }
                            finally
                            {
                                localEmbeddingSemaphore.Release();
                            }
                        }));
                    }
                    await Task.WhenAll(embeddingTasks);
                }
            }
            else
            {
                SimpleFileLogger.LogWarning($"GenerateProfileAsync: ClipService jest null. Nie można pobrać embeddingów dla profilu '{categoryName}'. Profil zostanie utworzony bez centroidu.");
            }


            var collectedEmbeddings = embeddingsWithPaths.Select(ep => ep.Embedding).ToList();
            var collectedPaths = embeddingsWithPaths.Select(ep => ep.Path).ToList();

            CategoryProfile? profile;
            lock (_profiles) // Zabezpiecz dostęp do _profiles
            {
                profile = GetProfile(categoryName); // GetProfile jest teraz bezpieczne
                if (profile == null)
                {
                    profile = new CategoryProfile(categoryName);
                    _profiles.Add(profile);
                    SimpleFileLogger.Log($"GenerateProfileAsync: Utworzono nowy obiekt profilu '{categoryName}' w pamięci.");
                }
            }

            // Jeśli nie ma embeddingów (bo np. clipService był null), to UpdateCentroid wyczyści istniejący
            profile.UpdateCentroid(collectedEmbeddings, collectedPaths.Any() ? collectedPaths : validImageFileEntries.Select(e => e.FilePath).ToList());


            await SaveProfilesForModelAsync(modelName);
            SimpleFileLogger.LogHighLevelInfo($"GenerateProfileAsync: Zakończono dla kategorii '{categoryName}'. Profil zaktualizowany/utworzony i zapisany dla modelki '{modelName}'.");
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

            List<CategoryProfile> profilesForThisModel;
            lock (_profiles) // Zabezpiecz odczyt z _profiles
            {
                profilesForThisModel = _profiles
                   .Where(p => GetModelNameFromCategory(p.CategoryName).Equals(modelName, StringComparison.OrdinalIgnoreCase))
                   .ToList(); // Tworzymy kopię, aby uniknąć problemów z modyfikacją podczas iteracji/zapisu
            }


            // --- ZMIANA: Blokada zapisu pliku dla konkretnej modelki ---
            object fileLock = _modelFileLocks.GetOrAdd(modelProfilePath, _ => new object());
            lock (fileLock)
            // ---------------------------------------------------------
            {
                if (!profilesForThisModel.Any())
                {
                    if (File.Exists(modelProfilePath))
                    {
                        try
                        {
                            File.Delete(modelProfilePath);
                            SimpleFileLogger.LogHighLevelInfo($"SaveProfilesForModelAsync: Usunięto plik profilu dla modelki '{modelName}', ponieważ nie ma już dla niej postaci: {modelProfilePath}");
                        }
                        catch (Exception ex)
                        {
                            SimpleFileLogger.LogError($"SaveProfilesForModelAsync: Błąd podczas usuwania pliku profilu dla modelki '{modelName}': {modelProfilePath}", ex);
                        }
                    }
                    return; // Zwróć, jeśli nie ma profili do zapisania (poza blokadą)
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
                    // Użyj File.WriteAllText zamiast WriteAllTextAsync wewnątrz locka, aby uniknąć problemów z async w lock
                    File.WriteAllText(modelProfilePath, jsonString);
                    SimpleFileLogger.LogHighLevelInfo($"SaveProfilesForModelAsync: Profile dla modelki '{modelName}' zapisane do: {modelProfilePath} (Liczba postaci: {profilesForThisModel.Count})");
                }
                catch (IOException ioEx) // Łap konkretnie IOException
                {
                    SimpleFileLogger.LogError($"SaveProfilesForModelAsync: Błąd IO podczas zapisywania profili dla modelki '{modelName}' do '{modelProfilePath}'. Być może plik jest nadal używany.", ioEx);
                    // Można tu dodać logikę ponawiania próby lub inną obsługę
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"SaveProfilesForModelAsync: Inny błąd podczas zapisywania profili dla modelki '{modelName}' do '{modelProfilePath}'", ex);
                }
            } // Koniec lock(fileLock)
        }

        public async Task SaveAllProfilesAsync()
        {
            SimpleFileLogger.LogHighLevelInfo("SaveAllProfilesAsync: Rozpoczęto zapisywanie wszystkich profili.");
            List<string> modelNamesToSave;
            lock (_profiles) // Zabezpiecz odczyt _profiles
            {
                if (!_profiles.Any())
                {
                    SimpleFileLogger.LogHighLevelInfo("SaveAllProfilesAsync: Brak profili w pamięci do zapisania.");
                    return;
                }
                modelNamesToSave = _profiles.Select(p => GetModelNameFromCategory(p.CategoryName)).Distinct().ToList();
            }

            // Zapisywanie może odbywać się równolegle dla RÓŻNYCH modelek,
            // ponieważ każda modelka ma swój plik i swoją blokadę.
            var saveTasks = new List<Task>();
            foreach (var modelName in modelNamesToSave)
            {
                // SaveProfilesForModelAsync teraz samo w sobie jest synchroniczne w części plikowej (wewnątrz locka),
                // ale samo wywołanie tutaj może być Task.Run, jeśli chcemy odciążyć wątek UI.
                // Jednak SaveProfilesForModelAsync jest już async, więc wystarczy await.
                saveTasks.Add(SaveProfilesForModelAsync(modelName));
            }
            await Task.WhenAll(saveTasks);
            SimpleFileLogger.LogHighLevelInfo($"SaveAllProfilesAsync: Zakończono. Próbowano zapisać profile dla {modelNamesToSave.Count} modelek.");
        }

        public async Task LoadProfilesAsync()
        {
            List<CategoryProfile> loadedProfiles = new List<CategoryProfile>(); // Tymczasowa lista
            SimpleFileLogger.LogHighLevelInfo($"LoadProfilesAsync: Rozpoczęto ładowanie z folderu: {_profilesBaseFolderPath}");

            if (!Directory.Exists(_profilesBaseFolderPath))
            {
                SimpleFileLogger.Log($"LoadProfilesAsync: Folder profili nie istnieje: {_profilesBaseFolderPath}.");
                lock (_profiles) { _profiles.Clear(); } // Wyczyść, jeśli folder nie istnieje
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
                            // Walidacja i korekta nazw kategorii (pozostaje bez zmian)
                            // ... (kod walidacji nazwy) ...
                            string expectedPrefix = $"{fileContent.ModelName} - ";
                            if (!profile.CategoryName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) &&
                                !profile.CategoryName.Equals(fileContent.ModelName, StringComparison.OrdinalIgnoreCase))
                            {
                                string characterPart = GetCharacterNameFromCategory(profile.CategoryName);
                                if (characterPart.Equals(profile.CategoryName, StringComparison.OrdinalIgnoreCase))
                                {
                                    profile.CategoryName = $"{fileContent.ModelName} - {profile.CategoryName}";
                                }
                                else if (!GetModelNameFromCategory(profile.CategoryName).Equals(fileContent.ModelName, StringComparison.OrdinalIgnoreCase))
                                {
                                    SimpleFileLogger.LogWarning($"LoadProfilesAsync: Profile '{profile.CategoryName}' in file '{Path.GetFileName(filePath)}' for model '{fileContent.ModelName}' has a mismatched model prefix. Attempting to correct to '{fileContent.ModelName} - {characterPart}'.");
                                    profile.CategoryName = $"{fileContent.ModelName} - {characterPart}";
                                }
                            }
                            else if (profile.CategoryName.Equals(fileContent.ModelName, StringComparison.OrdinalIgnoreCase))
                            {
                                profile.CategoryName = $"{fileContent.ModelName} - General";
                            }
                            loadedProfiles.Add(profile); // Dodaj do tymczasowej listy
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

            lock (_profiles) // Zaktualizuj główną listę profili w sposób bezpieczny wątkowo
            {
                _profiles.Clear();
                _profiles.AddRange(loadedProfiles);
            }
            SimpleFileLogger.LogHighLevelInfo($"LoadProfilesAsync: Zakończono ładowanie. Przetworzono {filesProcessed} plików. Załadowano łącznie {profilesLoadedTotal} profili kategorii.");
        }

        public Tuple<CategoryProfile, double>? SuggestCategory(float[] imageEmbedding, double similarityThreshold = 0.80, string? targetModelName = null)
        {
            if (imageEmbedding == null) return null;

            List<CategoryProfile> currentProfiles;
            lock (_profiles) // Odczyt z _profiles również powinien być bezpieczny
            {
                if (!_profiles.Any()) return null;
                currentProfiles = new List<CategoryProfile>(_profiles); // Kopiuj listę, aby uniknąć modyfikacji podczas iteracji
            }

            IEnumerable<CategoryProfile> profilesToSearch = currentProfiles;
            if (!string.IsNullOrWhiteSpace(targetModelName))
            {
                profilesToSearch = currentProfiles.Where(p => GetModelNameFromCategory(p.CategoryName).Equals(targetModelName, StringComparison.OrdinalIgnoreCase));
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
            SimpleFileLogger.LogHighLevelInfo($"RemoveProfileAsync: Próba usunięcia profilu '{categoryName}'.");
            CategoryProfile? profileToRemove;
            bool removedFromMemory = false;
            string modelName = GetModelNameFromCategory(categoryName);

            lock (_profiles) // Zabezpiecz dostęp do _profiles
            {
                profileToRemove = GetProfile(categoryName); // Użyj wewnętrznej metody, która nie blokuje
                if (profileToRemove != null)
                {
                    removedFromMemory = _profiles.Remove(profileToRemove);
                }
            }

            if (removedFromMemory && profileToRemove != null) // Sprawdź też profileToRemove, chociaż powinno być ok
            {
                SimpleFileLogger.LogHighLevelInfo($"RemoveProfileAsync: Usunięto profil '{categoryName}' z pamięci.");
                await SaveProfilesForModelAsync(modelName);
                return true;
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

            SimpleFileLogger.LogHighLevelInfo($"RemoveAllProfilesForModelAsync: Próba usunięcia wszystkich profili i pliku dla modelki '{modelName}'.");
            int removedCount;
            lock (_profiles) // Zabezpiecz dostęp do _profiles
            {
                removedCount = _profiles.RemoveAll(p => GetModelNameFromCategory(p.CategoryName).Equals(modelName, StringComparison.OrdinalIgnoreCase));
            }
            SimpleFileLogger.LogHighLevelInfo($"RemoveAllProfilesForModelAsync: Usunięto {removedCount} profili postaci dla modelki '{modelName}' z pamięci.");

            string sanitizedModelName = SanitizeFileName(modelName);
            string modelProfilePath = Path.Combine(_profilesBaseFolderPath, $"{sanitizedModelName}.json");

            // Blokada dla operacji na pliku
            object fileLock = _modelFileLocks.GetOrAdd(modelProfilePath, _ => new object());
            lock (fileLock)
            {
                if (File.Exists(modelProfilePath))
                {
                    try
                    {
                        File.Delete(modelProfilePath);
                        SimpleFileLogger.LogHighLevelInfo($"RemoveAllProfilesForModelAsync: Pomyślnie usunięto plik profilu: {modelProfilePath}");
                    }
                    catch (Exception ex)
                    {
                        SimpleFileLogger.LogError($"RemoveAllProfilesForModelAsync: Błąd podczas usuwania pliku profilu '{modelProfilePath}'.", ex);
                        return false; // Błąd usuwania pliku
                    }
                }
                else
                {
                    SimpleFileLogger.Log($"RemoveAllProfilesForModelAsync: Plik profilu '{modelProfilePath}' nie istniał. Nic do usunięcia z dysku.");
                }
            }
            return removedCount > 0 || !File.Exists(modelProfilePath); // Zwraca true, jeśli usunięto z pamięci LUB plik nie istnieje
        }
    }
}