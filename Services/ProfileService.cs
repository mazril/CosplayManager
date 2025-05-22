// Plik: Services/ProfileService.cs
using CosplayManager.Models;
using CosplayManager.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics; // Dla Stopwatch w GenerateProfileAsync, jeśli potrzebne
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
        private readonly ClipServiceHttpClient? _clipService;
        private readonly EmbeddingCacheServiceSQLite _embeddingCacheService;
        private readonly string _profilesBaseFolderPath;

        private const int MAX_CONCURRENT_BATCH_REQUESTS = 2;
        private const int EMBEDDING_BATCH_SIZE = 32;

        private readonly ConcurrentDictionary<string, object> _modelFileLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private class ModelProfilesFileContent
        {
            public string ModelName { get; set; } = string.Empty;
            public List<CategoryProfile> CharacterProfiles { get; set; } = new List<CategoryProfile>();
        }

        public ProfileService(ClipServiceHttpClient? clipService, EmbeddingCacheServiceSQLite embeddingCacheService, string profilesFolderName = "CategoryProfiles")
        {
            _clipService = clipService;
            _embeddingCacheService = embeddingCacheService ?? throw new ArgumentNullException(nameof(embeddingCacheService));
            _profilesBaseFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, profilesFolderName);

            if (!Directory.Exists(_profilesBaseFolderPath))
            {
                try { Directory.CreateDirectory(_profilesBaseFolderPath); SimpleFileLogger.LogHighLevelInfo($"Utworzono folder dla profili kategorii: {_profilesBaseFolderPath}"); }
                catch (Exception ex) { SimpleFileLogger.LogError($"Nie można utworzyć folderu dla profili kategorii: {_profilesBaseFolderPath}", ex); }
            }
            _profiles = new List<CategoryProfile>();
        }

        public IReadOnlyList<CategoryProfile> GetAllProfiles() => _profiles.AsReadOnly();

        public CategoryProfile? GetProfile(string categoryName)
        {
            return _profiles.FirstOrDefault(p => p.CategoryName.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
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
            foreach (char invalidChar in Path.GetInvalidFileNameChars()) { name = name.Replace(invalidChar.ToString(), "_"); }
            return name.Trim();
        }

        public async Task<float[]?> GetImageEmbeddingAsync(ImageFileEntry imageEntry, CancellationToken cancellationToken = default)
        {
            if (_clipService == null) { SimpleFileLogger.LogWarning($"GetImageEmbeddingAsync: ClipService jest null. Nie można pobrać embeddingu dla {imageEntry?.FilePath}."); return null; }
            if (imageEntry == null || string.IsNullOrWhiteSpace(imageEntry.FilePath)) { SimpleFileLogger.Log($"ProfileService.GetImageEmbeddingAsync: Nieprawidłowy ImageFileEntry lub ścieżka."); return null; }
            cancellationToken.ThrowIfCancellationRequested();
            SimpleFileLogger.Log($"GetImageEmbeddingAsync dla '{imageEntry.FilePath}': CurrentModUTC={imageEntry.FileLastModifiedUtc:o}, CurrentSize={imageEntry.FileSize}");

            try
            {
                return await _embeddingCacheService.GetOrUpdateEmbeddingAsync(
                    imageEntry.FilePath,
                    imageEntry.FileLastModifiedUtc,
                    imageEntry.FileSize,
                    async (path, token) =>
                    {
                        SimpleFileLogger.Log($"Embedding provider (cache miss/invalid) called for: {path}");
                        if (_clipService == null) return null;
                        token.ThrowIfCancellationRequested();
                        return await _clipService.GetImageEmbeddingFromPathAsync(path, token);
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) { SimpleFileLogger.LogWarning($"GetImageEmbeddingAsync: Operacja anulowana dla {imageEntry.FilePath}."); return null; }
            catch (Exception ex) { SimpleFileLogger.LogError($"ProfileService.GetImageEmbeddingAsync: Błąd dla obrazu {imageEntry.FilePath}", ex); return null; }
        }

        public async Task GenerateProfileAsync(string categoryName, List<ImageFileEntry> imageFileEntries, IProgress<ProgressReport> progress, CancellationToken cancellationToken = default)
        {
            SimpleFileLogger.LogHighLevelInfo($"GenerateProfileAsync: Rozpoczęto dla kategorii '{categoryName}' z {imageFileEntries?.Count ?? 0} obrazami.");
            progress.Report(new ProgressReport { OperationName = "Generowanie Profilu", StatusMessage = $"Inicjalizacja dla '{categoryName}'...", TotalItems = imageFileEntries?.Count ?? 0 });

            if (string.IsNullOrWhiteSpace(categoryName)) { SimpleFileLogger.LogError("GenerateProfileAsync: Nazwa kategorii nie może być pusta.", null); throw new ArgumentException("Nazwa kategorii nie może być pusta.", nameof(categoryName)); }
            cancellationToken.ThrowIfCancellationRequested();

            string modelName = GetModelNameFromCategory(categoryName);
            var validImageFileEntries = imageFileEntries?.Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.FilePath) && File.Exists(entry.FilePath)).ToList() ?? new List<ImageFileEntry>();
            int totalValidFiles = validImageFileEntries.Count;
            progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = totalValidFiles, StatusMessage = $"Znaleziono {totalValidFiles} prawidłowych plików dla '{categoryName}'." });


            if (!validImageFileEntries.Any())
            {
                SimpleFileLogger.Log($"GenerateProfileAsync: Brak prawidłowych obrazów dla '{categoryName}'. Próba usunięcia/wyczyszczenia profilu w pamięci.");
                CategoryProfile? existingProfileToClear;
                lock (_profiles)
                {
                    existingProfileToClear = GetProfile(categoryName);
                    if (existingProfileToClear != null)
                    {
                        _profiles.Remove(existingProfileToClear);
                        SimpleFileLogger.Log($"GenerateProfileAsync: Usunięto profil '{categoryName}' z pamięci.");
                    }
                }
                // Usunięto automatyczny zapis: await SaveProfilesForModelAsync(modelName, cancellationToken);
                progress.Report(new ProgressReport { ProcessedItems = 0, TotalItems = 0, StatusMessage = $"Brak obrazów dla '{categoryName}', profil wyczyszczony/usunięty z pamięci." });
                return;
            }

            var embeddingsWithPaths = new ConcurrentDictionary<string, float[]>();
            var pathsWithCacheMiss = new ConcurrentBag<ImageFileEntry>();
            int processedCountForCacheCheck = 0;

            SimpleFileLogger.Log($"GenerateProfileAsync ({categoryName}): Krok 1 - Sprawdzanie cache dla {totalValidFiles} obrazów.");
            var cacheCheckTasks = new List<Task>();
            foreach (var entry in validImageFileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cacheCheckTasks.Add(Task.Run(async () => {
                    float[]? cachedEmbedding = await _embeddingCacheService.GetFromCacheOnlyAsync(entry.FilePath, entry.FileLastModifiedUtc, entry.FileSize, cancellationToken);
                    if (cachedEmbedding != null) { embeddingsWithPaths.TryAdd(entry.FilePath, cachedEmbedding); }
                    else { pathsWithCacheMiss.Add(entry); }
                    Interlocked.Increment(ref processedCountForCacheCheck);
                    progress.Report(new ProgressReport { ProcessedItems = processedCountForCacheCheck, TotalItems = totalValidFiles, StatusMessage = $"Cache: {Path.GetFileName(entry.FilePath)} ({processedCountForCacheCheck}/{totalValidFiles})" });
                }, cancellationToken));
            }
            await Task.WhenAll(cacheCheckTasks);
            cancellationToken.ThrowIfCancellationRequested();
            SimpleFileLogger.Log($"GenerateProfileAsync ({categoryName}): Krok 1 - Zakończono. Znaleziono w cache: {embeddingsWithPaths.Count}. Cache miss: {pathsWithCacheMiss.Count}");
            progress.Report(new ProgressReport { ProcessedItems = processedCountForCacheCheck, TotalItems = totalValidFiles, StatusMessage = $"Cache sprawdzony. Miss: {pathsWithCacheMiss.Count}." });


            if (_clipService != null && pathsWithCacheMiss.Any())
            {
                int totalMisses = pathsWithCacheMiss.Count;
                int processedMisses = 0;
                SimpleFileLogger.Log($"GenerateProfileAsync ({categoryName}): Krok 2 - Pobieranie {totalMisses} embeddingów z serwera CLIP (wsadowo).");
                progress.Report(new ProgressReport { ProcessedItems = processedCountForCacheCheck, TotalItems = totalValidFiles, StatusMessage = $"Pobieranie {totalMisses} embeddingów z serwera..." });

                var missEntriesList = pathsWithCacheMiss.ToList();
                var batchTasks = new List<Task>();
                using (var batchSemaphore = new SemaphoreSlim(MAX_CONCURRENT_BATCH_REQUESTS))
                {
                    for (int i = 0; i < missEntriesList.Count; i += EMBEDDING_BATCH_SIZE)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var currentBatchEntries = missEntriesList.Skip(i).Take(EMBEDDING_BATCH_SIZE).ToList();
                        if (!currentBatchEntries.Any()) continue;

                        int currentBatchNumber = (i / EMBEDDING_BATCH_SIZE) + 1;
                        int totalBatches = (int)Math.Ceiling((double)totalMisses / EMBEDDING_BATCH_SIZE);

                        batchTasks.Add(Task.Run(async () =>
                        {
                            await batchSemaphore.WaitAsync(cancellationToken);
                            try
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var pathsForBatch = currentBatchEntries.Select(e => e.FilePath).ToList();
                                SimpleFileLogger.Log($"GenerateProfileAsync ({categoryName}): Wysyłanie paczki {pathsForBatch.Count} obrazów (paczka {currentBatchNumber}/{totalBatches}).");
                                progress.Report(new ProgressReport
                                {
                                    ProcessedItems = processedCountForCacheCheck + processedMisses,
                                    TotalItems = totalValidFiles,
                                    StatusMessage = $"Serwer CLIP: Paczka {currentBatchNumber}/{totalBatches} ({pathsForBatch.Count} obr.)..."
                                });

                                List<float[]>? batchEmbeddings = await _clipService.GetImageEmbeddingsBatchAsync(pathsForBatch, cancellationToken);

                                if (batchEmbeddings != null && batchEmbeddings.Count == currentBatchEntries.Count)
                                {
                                    SimpleFileLogger.Log($"GenerateProfileAsync ({categoryName}): Otrzymano {batchEmbeddings.Count} embeddingów z paczki {currentBatchNumber}.");
                                    for (int j = 0; j < currentBatchEntries.Count; j++)
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        var entry = currentBatchEntries[j];
                                        var embedding = batchEmbeddings[j];
                                        if (embedding != null)
                                        {
                                            embeddingsWithPaths.TryAdd(entry.FilePath, embedding);
                                            await _embeddingCacheService.StoreInCacheAsync(entry.FilePath, entry.FileLastModifiedUtc, entry.FileSize, embedding, cancellationToken);
                                        }
                                        else { SimpleFileLogger.LogWarning($"GenerateProfileAsync ({categoryName}): Serwer zwrócił null embedding dla {entry.FilePath} w paczce."); }
                                        Interlocked.Increment(ref processedMisses);
                                        progress.Report(new ProgressReport
                                        {
                                            ProcessedItems = processedCountForCacheCheck + processedMisses,
                                            TotalItems = totalValidFiles,
                                            StatusMessage = $"Serwer CLIP: {Path.GetFileName(entry.FilePath)} ({processedCountForCacheCheck + processedMisses}/{totalValidFiles})"
                                        });
                                    }
                                }
                                else { SimpleFileLogger.LogError($"GenerateProfileAsync ({categoryName}): Niezgodna liczba embeddingów ({batchEmbeddings?.Count ?? -1}) z paczki dla {currentBatchEntries.Count} obrazów lub błąd serwera.", null); }
                            }
                            catch (OperationCanceledException) { SimpleFileLogger.LogWarning($"GenerateProfileAsync ({categoryName}): Pobieranie paczki embeddingów anulowane."); }
                            catch (Exception ex) { SimpleFileLogger.LogError($"GenerateProfileAsync ({categoryName}): Błąd podczas pobierania/przetwarzania paczki embeddingów.", ex); }
                            finally { batchSemaphore.Release(); }
                        }, cancellationToken));
                    }
                    await Task.WhenAll(batchTasks);
                }
                cancellationToken.ThrowIfCancellationRequested();
                SimpleFileLogger.Log($"GenerateProfileAsync ({categoryName}): Krok 2 - Zakończono. Łącznie embeddingów po pobraniu z serwera: {embeddingsWithPaths.Count}");
            }
            else if (_clipService == null && pathsWithCacheMiss.Any()) { SimpleFileLogger.LogWarning($"GenerateProfileAsync ({categoryName}): ClipService jest null. Nie można pobrać {pathsWithCacheMiss.Count} brakujących embeddingów. Profil będzie bazował tylko na cache."); }

            progress.Report(new ProgressReport { ProcessedItems = processedCountForCacheCheck + pathsWithCacheMiss.Count, TotalItems = totalValidFiles, StatusMessage = "Obliczanie centroidu..." });

            var finalEmbeddingsForProfile = new List<float[]>();
            var finalPathsForProfile = new List<string>();
            foreach (var entry in validImageFileEntries) { if (embeddingsWithPaths.TryGetValue(entry.FilePath, out var emb)) { finalEmbeddingsForProfile.Add(emb); finalPathsForProfile.Add(entry.FilePath); } }

            CategoryProfile? profile;
            lock (_profiles)
            {
                profile = GetProfile(categoryName);
                if (profile == null)
                {
                    profile = new CategoryProfile(categoryName);
                    _profiles.Add(profile);
                    SimpleFileLogger.Log($"GenerateProfileAsync: Utworzono nowy obiekt profilu '{categoryName}' w pamięci.");
                }
            }

            profile.UpdateCentroid(finalEmbeddingsForProfile, finalPathsForProfile.Any() ? finalPathsForProfile : validImageFileEntries.Select(e => e.FilePath).ToList());
            cancellationToken.ThrowIfCancellationRequested();

            // USUNIĘTO: await SaveProfilesForModelAsync(modelName, cancellationToken);
            // Zapis będzie realizowany przez MainWindowViewModel po zakończeniu operacji na modelu.
            SimpleFileLogger.LogHighLevelInfo($"GenerateProfileAsync: Zakończono dla kategorii '{categoryName}'. Profil zaktualizowany/utworzony w PAMIĘCI dla modelki '{modelName}'.");
            progress.Report(new ProgressReport { ProcessedItems = totalValidFiles, TotalItems = totalValidFiles, StatusMessage = $"Profil '{categoryName}' gotowy (w pamięci)." });
        }

        public async Task SaveProfilesForModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(modelName)) { SimpleFileLogger.LogError("SaveProfilesForModelAsync: Nazwa modelki nie może być pusta.", null); return; }
            cancellationToken.ThrowIfCancellationRequested();

            string sanitizedModelName = SanitizeFileName(modelName);
            string modelProfilePath = Path.Combine(_profilesBaseFolderPath, $"{sanitizedModelName}.json");

            List<CategoryProfile> profilesForThisModel;
            lock (_profiles) { profilesForThisModel = _profiles.Where(p => GetModelNameFromCategory(p.CategoryName).Equals(modelName, StringComparison.OrdinalIgnoreCase)).ToList(); }

            object fileLock = _modelFileLocks.GetOrAdd(modelProfilePath, _ => new object());
            await Task.Run(() => {
                lock (fileLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!profilesForThisModel.Any())
                    {
                        if (File.Exists(modelProfilePath))
                        {
                            try { File.Delete(modelProfilePath); SimpleFileLogger.LogHighLevelInfo($"SaveProfilesForModelAsync: Usunięto plik profilu dla modelki '{modelName}'."); }
                            catch (Exception ex) { SimpleFileLogger.LogError($"SaveProfilesForModelAsync: Błąd usuwania pliku profilu dla '{modelName}'.", ex); }
                        }
                        return;
                    }
                    ModelProfilesFileContent fileContent = new ModelProfilesFileContent { ModelName = modelName, CharacterProfiles = profilesForThisModel };
                    try
                    {
                        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                        string jsonString = JsonSerializer.Serialize(fileContent, options);
                        File.WriteAllText(modelProfilePath, jsonString);
                        SimpleFileLogger.LogHighLevelInfo($"SaveProfilesForModelAsync: Profile dla '{modelName}' zapisane ({profilesForThisModel.Count} postaci).");
                    }
                    catch (Exception ex) { SimpleFileLogger.LogError($"SaveProfilesForModelAsync: Błąd zapisywania profili dla '{modelName}'.", ex); }
                }
            }, cancellationToken);
        }

        public async Task SaveAllProfilesAsync(CancellationToken cancellationToken = default)
        {
            SimpleFileLogger.LogHighLevelInfo("SaveAllProfilesAsync: Rozpoczęto.");
            List<string> modelNamesToSave;
            lock (_profiles) { if (!_profiles.Any()) { SimpleFileLogger.LogHighLevelInfo("SaveAllProfilesAsync: Brak profili."); return; } modelNamesToSave = _profiles.Select(p => GetModelNameFromCategory(p.CategoryName)).Distinct().ToList(); }
            cancellationToken.ThrowIfCancellationRequested();
            var saveTasks = modelNamesToSave.Select(modelName => SaveProfilesForModelAsync(modelName, cancellationToken)).ToList();
            await Task.WhenAll(saveTasks);
            SimpleFileLogger.LogHighLevelInfo($"SaveAllProfilesAsync: Zakończono dla {modelNamesToSave.Count} modelek.");
        }

        public async Task LoadProfilesAsync(CancellationToken cancellationToken = default)
        {
            List<CategoryProfile> loadedProfiles = new List<CategoryProfile>();
            SimpleFileLogger.LogHighLevelInfo($"LoadProfilesAsync: Rozpoczęto z folderu: {_profilesBaseFolderPath}");
            if (!Directory.Exists(_profilesBaseFolderPath)) { SimpleFileLogger.Log($"LoadProfilesAsync: Folder profili nie istnieje."); lock (_profiles) { _profiles.Clear(); } return; }
            cancellationToken.ThrowIfCancellationRequested();

            int filesProcessed = 0; int profilesLoadedTotal = 0;
            var filePaths = Directory.EnumerateFiles(_profilesBaseFolderPath, "*.json").ToList();
            foreach (string filePath in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested(); filesProcessed++;
                try
                {
                    string jsonString = await File.ReadAllTextAsync(filePath, cancellationToken);
                    ModelProfilesFileContent? fileContent = JsonSerializer.Deserialize<ModelProfilesFileContent>(jsonString);
                    if (fileContent?.CharacterProfiles != null && fileContent.CharacterProfiles.Any())
                    {
                        foreach (var profile in fileContent.CharacterProfiles)
                        {
                            string expectedPrefix = $"{fileContent.ModelName} - ";
                            if (!profile.CategoryName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) && !profile.CategoryName.Equals(fileContent.ModelName, StringComparison.OrdinalIgnoreCase))
                            {
                                string characterPart = GetCharacterNameFromCategory(profile.CategoryName);
                                if (characterPart.Equals(profile.CategoryName, StringComparison.OrdinalIgnoreCase)) profile.CategoryName = $"{fileContent.ModelName} - {profile.CategoryName}";
                                else if (!GetModelNameFromCategory(profile.CategoryName).Equals(fileContent.ModelName, StringComparison.OrdinalIgnoreCase)) { SimpleFileLogger.LogWarning($"LoadProfilesAsync: Korekta nazwy profilu '{profile.CategoryName}' dla modelki '{fileContent.ModelName}'."); profile.CategoryName = $"{fileContent.ModelName} - {characterPart}"; }
                            }
                            else if (profile.CategoryName.Equals(fileContent.ModelName, StringComparison.OrdinalIgnoreCase)) profile.CategoryName = $"{fileContent.ModelName} - General";
                            loadedProfiles.Add(profile); profilesLoadedTotal++;
                        }
                        SimpleFileLogger.Log($"LoadProfilesAsync: Załadowano {fileContent.CharacterProfiles.Count} profili dla '{fileContent.ModelName}' z '{Path.GetFileName(filePath)}'.");
                    }
                    else { SimpleFileLogger.Log($"LoadProfilesAsync: Plik '{Path.GetFileName(filePath)}' pusty lub niepoprawny."); }
                }
                catch (OperationCanceledException) { SimpleFileLogger.LogWarning($"LoadProfilesAsync: Anulowano podczas pliku {Path.GetFileName(filePath)}."); throw; }
                catch (Exception ex) { SimpleFileLogger.LogError($"LoadProfilesAsync: Błąd z pliku: {Path.GetFileName(filePath)}", ex); }
            }
            lock (_profiles) { _profiles.Clear(); _profiles.AddRange(loadedProfiles); }
            SimpleFileLogger.LogHighLevelInfo($"LoadProfilesAsync: Zakończono. Plików: {filesProcessed}. Profili: {profilesLoadedTotal}.");
        }

        public Tuple<CategoryProfile, double>? SuggestCategory(float[] imageEmbedding, double similarityThreshold = 0.80, string? targetModelName = null)
        {
            if (imageEmbedding == null) return null;
            List<CategoryProfile> currentProfiles;
            lock (_profiles) { if (!_profiles.Any()) return null; currentProfiles = new List<CategoryProfile>(_profiles); }
            IEnumerable<CategoryProfile> profilesToSearch = string.IsNullOrWhiteSpace(targetModelName) ? currentProfiles : currentProfiles.Where(p => GetModelNameFromCategory(p.CategoryName).Equals(targetModelName, StringComparison.OrdinalIgnoreCase));
            if (!profilesToSearch.Any()) return null;
            CategoryProfile? bestMatchProfile = null; double highestSimilarity = -1.0;
            foreach (var profile in profilesToSearch) { if (profile.CentroidEmbedding == null) continue; double similarity = MathUtils.CalculateCosineSimilarity(imageEmbedding, profile.CentroidEmbedding); if (similarity > highestSimilarity) { highestSimilarity = similarity; bestMatchProfile = profile; } }
            return (bestMatchProfile != null && highestSimilarity >= similarityThreshold) ? Tuple.Create(bestMatchProfile, highestSimilarity) : null;
        }

        public async Task<bool> RemoveProfileAsync(string categoryName, CancellationToken cancellationToken = default)
        {
            SimpleFileLogger.LogHighLevelInfo($"RemoveProfileAsync: Próba usunięcia '{categoryName}'.");
            CategoryProfile? profileToRemove; bool removedFromMemory = false;
            string modelName = GetModelNameFromCategory(categoryName);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_profiles) { profileToRemove = GetProfile(categoryName); if (profileToRemove != null) removedFromMemory = _profiles.Remove(profileToRemove); }
            if (removedFromMemory && profileToRemove != null)
            {
                SimpleFileLogger.LogHighLevelInfo($"RemoveProfileAsync: Usunięto '{categoryName}' z pamięci. Zapisuję zmiany dla modelki '{modelName}'.");
                await SaveProfilesForModelAsync(modelName, cancellationToken);
                return true;
            }
            SimpleFileLogger.Log($"RemoveProfileAsync: Nie znaleziono '{categoryName}'."); return false;
        }

        public async Task<bool> RemoveAllProfilesForModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(modelName)) { SimpleFileLogger.LogError("RemoveAllProfilesForModelAsync: Nazwa modelki pusta.", null); return false; }
            cancellationToken.ThrowIfCancellationRequested();
            SimpleFileLogger.LogHighLevelInfo($"RemoveAllProfilesForModelAsync: Próba usunięcia dla '{modelName}'.");
            int removedCount; lock (_profiles) { removedCount = _profiles.RemoveAll(p => GetModelNameFromCategory(p.CategoryName).Equals(modelName, StringComparison.OrdinalIgnoreCase)); }
            SimpleFileLogger.LogHighLevelInfo($"RemoveAllProfilesForModelAsync: Usunięto {removedCount} profili dla '{modelName}' z pamięci.");

            // Zapis polega na usunięciu pliku JSON dla modelki, jeśli nie ma już dla niej profili,
            // lub zapisaniu pustej listy profili, co SaveProfilesForModelAsync powinno obsłużyć.
            await SaveProfilesForModelAsync(modelName, cancellationToken);

            return removedCount > 0;
        }
    }
}