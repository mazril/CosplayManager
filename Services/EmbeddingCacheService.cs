// Plik: Services/EmbeddingCacheService.cs
using CosplayManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;

namespace CosplayManager.Services
{
    public class EmbeddingCacheEntry
    {
        public float[] Embedding { get; set; }
        public DateTime LastModifiedUtc { get; set; }
        public long FileSize { get; set; }
    }

    public class EmbeddingCacheService
    {
        private Dictionary<string, EmbeddingCacheEntry> _embeddingCache;
        private readonly string _cacheFilePath;
        private readonly object _cacheLock = new object();
        private const string CacheFileName = "embedding_cache.json";

        public EmbeddingCacheService(string cacheDirectory = "Cache")
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string fullCacheDirectoryPath = Path.Combine(baseDirectory, cacheDirectory);

            if (!Directory.Exists(fullCacheDirectoryPath))
            {
                Directory.CreateDirectory(fullCacheDirectoryPath);
            }
            _cacheFilePath = Path.Combine(fullCacheDirectoryPath, CacheFileName);
            _embeddingCache = LoadCacheFromFile();
            SimpleFileLogger.Log($"EmbeddingCacheService initialized. Cache path: {_cacheFilePath}. Loaded {_embeddingCache.Count} entries from file.");
        }

        private Dictionary<string, EmbeddingCacheEntry> LoadCacheFromFile()
        {
            lock (_cacheLock)
            {
                if (!File.Exists(_cacheFilePath))
                {
                    return new Dictionary<string, EmbeddingCacheEntry>(StringComparer.OrdinalIgnoreCase);
                }
                try
                {
                    string json = File.ReadAllText(_cacheFilePath);
                    var loadedCache = JsonSerializer.Deserialize<Dictionary<string, EmbeddingCacheEntry>>(json);
                    return new Dictionary<string, EmbeddingCacheEntry>(loadedCache ?? new Dictionary<string, EmbeddingCacheEntry>(), StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"Error loading embedding cache from '{_cacheFilePath}'. Returning new cache.", ex);
                    return new Dictionary<string, EmbeddingCacheEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public void SaveCacheToFile()
        {
            lock (_cacheLock)
            {
                try
                {
                    var cacheCopy = new Dictionary<string, EmbeddingCacheEntry>(_embeddingCache, StringComparer.OrdinalIgnoreCase);
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(cacheCopy, options);
                    File.WriteAllText(_cacheFilePath, json);
                    SimpleFileLogger.Log($"Embedding cache saved to '{_cacheFilePath}'. Saved {cacheCopy.Count} entries.");
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"Error saving embedding cache to '{_cacheFilePath}'.", ex);
                }
            }
        }

        // ZMODYFIKOWANA SYGNATURA METODY
        public async Task<float[]?> GetOrUpdateEmbeddingAsync(
            string imagePath,
            DateTime currentFileLastModifiedUtc, // Przekazywana wartość
            long currentFileSize,                // Przekazywana wartość
            Func<string, Task<float[]?>> embeddingProvider)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) // Usunięto File.Exists, bo zakładamy, że plik istnieje, skoro mamy jego metadane
            {
                SimpleFileLogger.LogWarning($"GetOrUpdateEmbeddingAsync: Invalid path: {imagePath}");
                return null;
            }

            // Usunięto:
            // FileInfo fileInfo = new FileInfo(imagePath);
            // DateTime currentLastModifiedUtc = fileInfo.LastWriteTimeUtc;
            // long currentFileSize = fileInfo.Length;

            lock (_cacheLock)
            {
                if (_embeddingCache.TryGetValue(imagePath, out EmbeddingCacheEntry cachedEntry))
                {
                    // Używamy przekazanych currentFileLastModifiedUtc i currentFileSize
                    if (cachedEntry.LastModifiedUtc == currentFileLastModifiedUtc &&
                        cachedEntry.FileSize == currentFileSize &&
                        cachedEntry.Embedding != null)
                    {
                        SimpleFileLogger.Log($"Cache hit for: {imagePath}");
                        return cachedEntry.Embedding;
                    }
                    else
                    {
                        SimpleFileLogger.Log($"Cache invalid for: {imagePath}. CachedMod: {cachedEntry.LastModifiedUtc}, CurrentMod: {currentFileLastModifiedUtc}, CachedSize: {cachedEntry.FileSize}, CurrentSize: {currentFileSize}");
                    }
                }
            }

            SimpleFileLogger.Log($"Cache miss or invalid for: {imagePath}. Fetching new embedding.");
            float[]? newEmbedding = await embeddingProvider(imagePath);

            if (newEmbedding != null)
            {
                lock (_cacheLock)
                {
                    _embeddingCache[imagePath] = new EmbeddingCacheEntry
                    {
                        Embedding = newEmbedding,
                        LastModifiedUtc = currentFileLastModifiedUtc, // Używamy przekazanej wartości
                        FileSize = currentFileSize                    // Używamy przekazanej wartości
                    };
                }
            }
            return newEmbedding;
        }

        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _embeddingCache.Clear();
            }
            SaveCacheToFile();
            SimpleFileLogger.Log("Embedding cache cleared.");
        }
    }
}