// Plik: Services/EmbeddingCacheServiceSQLite.cs
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq; // Dodano dla Any()
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosplayManager.Services
{
    // Definicja EmbeddingCacheEntry powinna być w Models lub tutaj, jeśli tylko tu używana.
    // Zakładam, że jest w CosplayManager.Models, więc usunąłem duplikat.
    // Jeśli nie, należy ją tu przywrócić lub przenieść do Models.
    // public class EmbeddingCacheEntry { ... } 

    public class EmbeddingCacheServiceSQLite : IDisposable
    {
        private readonly string _databasePath;
        private readonly object _dbLock = new object();

        private const string CacheDbFileName = "embedding_cache.db";
        private const string TableName = "Embeddings";
        private static readonly TimeSpan DateComparisonTolerance = TimeSpan.FromSeconds(2); // Tolerancja dla porównania daty modyfikacji

        public EmbeddingCacheServiceSQLite(string cacheDirectory = "Cache")
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string fullCacheDirectoryPath = Path.Combine(baseDirectory, cacheDirectory);

            if (!Directory.Exists(fullCacheDirectoryPath))
            {
                Directory.CreateDirectory(fullCacheDirectoryPath);
            }
            _databasePath = Path.Combine(fullCacheDirectoryPath, CacheDbFileName);

            InitializeDatabase();
            SimpleFileLogger.LogHighLevelInfo($"EmbeddingCacheServiceSQLite initialized. Database path: {_databasePath}");
        }

        private void InitializeDatabase()
        {
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();

                    // WŁĄCZANIE TRYBU WAL (Write-Ahead Logging)
                    using (var walCommand = connection.CreateCommand())
                    {
                        walCommand.CommandText = "PRAGMA journal_mode=WAL;";
                        try
                        {
                            walCommand.ExecuteNonQuery();
                            SimpleFileLogger.Log("EmbeddingCacheServiceSQLite: Successfully set journal_mode to WAL.");
                        }
                        catch (Exception ex)
                        {
                            SimpleFileLogger.LogError("EmbeddingCacheServiceSQLite: Failed to set journal_mode to WAL.", ex);
                        }
                    }
                    // KONIEC ZMIANY - WŁĄCZANIE TRYBU WAL

                    string createTableQuery = $@"
                        CREATE TABLE IF NOT EXISTS {TableName} (
                            ImagePath TEXT PRIMARY KEY,
                            Embedding BLOB NOT NULL,
                            LastModifiedUtc INTEGER NOT NULL,
                            FileSize INTEGER NOT NULL
                        );";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = createTableQuery;
                        command.ExecuteNonQuery();
                    }
                }
            }
            SimpleFileLogger.Log($"Database initialized/checked at {_databasePath}");
        }

        public Task<float[]?> GetFromCacheOnlyAsync(string imagePath, DateTime currentFileLastModifiedUtc, long currentFileSize, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                SimpleFileLogger.LogWarning($"GetFromCacheOnlyAsync (SQLite): Invalid imagePath.");
                return Task.FromResult<float[]?>(null);
            }
            cancellationToken.ThrowIfCancellationRequested();

            float[]? embedding = null;
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string selectQuery = $@"
                        SELECT Embedding, LastModifiedUtc, FileSize
                        FROM {TableName}
                        WHERE ImagePath = @ImagePath;";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = selectQuery;
                        command.Parameters.AddWithValue("@ImagePath", imagePath);

                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                try
                                {
                                    byte[] embeddingBlob = (byte[])reader["Embedding"];
                                    long lastModifiedTicks = reader.GetInt64(reader.GetOrdinal("LastModifiedUtc"));
                                    long fileSize = reader.GetInt64(reader.GetOrdinal("FileSize"));
                                    var cachedLastModifiedUtc = new DateTime(lastModifiedTicks, DateTimeKind.Utc);

                                    bool sizeMatches = fileSize == currentFileSize;
                                    bool dateMatches = Math.Abs((cachedLastModifiedUtc - currentFileLastModifiedUtc).TotalSeconds) < DateComparisonTolerance.TotalSeconds;

                                    var deserializedEmbedding = JsonSerializer.Deserialize<float[]>(embeddingBlob);

                                    if (sizeMatches && dateMatches && deserializedEmbedding != null && deserializedEmbedding.Any())
                                    {
                                        SimpleFileLogger.Log($"SQLite Cache hit (GetFromCacheOnlyAsync) for: {imagePath}");
                                        embedding = deserializedEmbedding;
                                    }
                                    else
                                    {
                                        SimpleFileLogger.Log($"SQLite Cache invalid (GetFromCacheOnlyAsync) for: {imagePath}. " +
                                            $"SizeMatch: {sizeMatches} (DB: {fileSize}, Current: {currentFileSize}). " +
                                            $"DateMatch ({DateComparisonTolerance.TotalSeconds}s tol.): {dateMatches} (DB: {cachedLastModifiedUtc:o}, Current: {currentFileLastModifiedUtc:o}). " +
                                            $"EmbeddingNullOrEmpty: {deserializedEmbedding == null || !deserializedEmbedding.Any()}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    SimpleFileLogger.LogError($"Error deserializing embedding from DB (GetFromCacheOnlyAsync) for {imagePath}", ex);
                                }
                            }
                        }
                    }
                }
            }
            return Task.FromResult(embedding);
        }

        public Task StoreInCacheAsync(string imagePath, DateTime fileLastModifiedUtc, long fileSize, float[] embedding, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || embedding == null || !embedding.Any())
            {
                SimpleFileLogger.LogWarning($"StoreInCacheAsync (SQLite): Invalid imagePath or empty embedding. Not caching for {imagePath}.");
                return Task.CompletedTask;
            }
            cancellationToken.ThrowIfCancellationRequested();

            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string upsertQuery = $@"
                        INSERT OR REPLACE INTO {TableName} (ImagePath, Embedding, LastModifiedUtc, FileSize)
                        VALUES (@ImagePath, @Embedding, @LastModifiedUtc, @FileSize);";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = upsertQuery;
                        command.Parameters.AddWithValue("@ImagePath", imagePath);
                        command.Parameters.AddWithValue("@Embedding", JsonSerializer.SerializeToUtf8Bytes(embedding));
                        command.Parameters.AddWithValue("@LastModifiedUtc", fileLastModifiedUtc.Ticks);
                        command.Parameters.AddWithValue("@FileSize", fileSize);
                        command.ExecuteNonQuery();
                        SimpleFileLogger.Log($"Saved/Updated embedding to SQLite (StoreInCacheAsync) for: {imagePath}");
                    }
                }
            }
            return Task.CompletedTask;
        }

        public async Task<float[]?> GetOrUpdateEmbeddingAsync(
            string imagePath,
            DateTime currentFileLastModifiedUtc,
            long currentFileSize,
            Func<string, CancellationToken, Task<float[]?>> embeddingProvider, // Zmieniony Func
            CancellationToken cancellationToken = default) // Dodany CancellationToken
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                SimpleFileLogger.LogWarning($"GetOrUpdateEmbeddingAsync (SQLite): Invalid imagePath.");
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();

            float[]? cachedEmbedding = await GetFromCacheOnlyAsync(imagePath, currentFileLastModifiedUtc, currentFileSize, cancellationToken);
            if (cachedEmbedding != null)
            {
                return cachedEmbedding;
            }
            cancellationToken.ThrowIfCancellationRequested();

            SimpleFileLogger.Log($"SQLite Cache miss or invalid for: {imagePath}. Fetching new embedding.");
            // Wywołaj dostawcę embeddingu, przekazując CancellationToken
            float[]? newEmbedding = await embeddingProvider(imagePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (newEmbedding != null && newEmbedding.Any()) // Sprawdź, czy embedding nie jest pusty
            {
                await StoreInCacheAsync(imagePath, currentFileLastModifiedUtc, currentFileSize, newEmbedding, cancellationToken);
            }
            else
            {
                SimpleFileLogger.LogWarning($"Embedding provider returned null or empty embedding for: {imagePath}. Not caching.");
            }
            return newEmbedding;
        }

        public void ClearCache()
        {
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string deleteQuery = $"DELETE FROM {TableName};";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = deleteQuery;
                        command.ExecuteNonQuery();
                    }
                    string vacuumQuery = $"VACUUM {TableName};"; // VACUUM dla konkretnej tabeli
                    using (var command = connection.CreateCommand()) { command.CommandText = vacuumQuery; command.ExecuteNonQuery(); }
                }
            }
            SimpleFileLogger.LogHighLevelInfo($"SQLite embedding cache cleared ({TableName} table).");
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}