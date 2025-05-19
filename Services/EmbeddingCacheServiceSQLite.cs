// Plik: Services/EmbeddingCacheServiceSQLite.cs
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json; // Nadal potrzebne do serializacji/deserializacji float[] do/z BLOB
using System.Threading.Tasks;

namespace CosplayManager.Services
{
    public class EmbeddingCacheEntry // Przeniesiono tu dla spójności lub upewnij się, że jest dostępne
    {
        public float[] Embedding { get; set; }
        public DateTime LastModifiedUtc { get; set; }
        public long FileSize { get; set; }
    }

    public class EmbeddingCacheServiceSQLite : IDisposable
    {
        private readonly string _databasePath;
        private readonly object _dbLock = new object(); // Prosta blokada dla operacji na bazie danych

        private const string CacheDbFileName = "embedding_cache.db";
        private const string TableName = "Embeddings";

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
                    // Opcjonalnie: Dodaj indeksy dla optymalizacji, jeśli potrzebne
                    // np. CREATE INDEX IF NOT EXISTS IDX_LastModifiedUtc ON Embeddings (LastModifiedUtc);
                }
            }
            SimpleFileLogger.Log($"Database initialized/checked at {_databasePath}");
        }

        public async Task<float[]?> GetOrUpdateEmbeddingAsync(
            string imagePath,
            // ShardKey nie jest już potrzebny, kluczem jest imagePath
            DateTime currentFileLastModifiedUtc,
            long currentFileSize,
            Func<string, Task<float[]?>> embeddingProvider)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                SimpleFileLogger.LogWarning($"GetOrUpdateEmbeddingAsync (SQLite): Invalid imagePath.");
                return null;
            }

            EmbeddingCacheEntry? cachedEntry = null;
            bool entryFoundAndValid = false;

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

                                    cachedEntry = new EmbeddingCacheEntry
                                    {
                                        Embedding = JsonSerializer.Deserialize<float[]>(embeddingBlob), // Deserializacja z BLOB
                                        LastModifiedUtc = new DateTime(lastModifiedTicks, DateTimeKind.Utc),
                                        FileSize = fileSize
                                    };

                                    if (cachedEntry.LastModifiedUtc == currentFileLastModifiedUtc &&
                                        cachedEntry.FileSize == currentFileSize &&
                                        cachedEntry.Embedding != null)
                                    {
                                        SimpleFileLogger.Log($"SQLite Cache hit for: {imagePath}");
                                        entryFoundAndValid = true;
                                    }
                                    else
                                    {
                                        SimpleFileLogger.Log($"SQLite Cache invalid for: {imagePath}. DBMod: {cachedEntry.LastModifiedUtc}, CurrentMod: {currentFileLastModifiedUtc}, DBSize: {cachedEntry.FileSize}, CurrentSize: {currentFileSize}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    SimpleFileLogger.LogError($"Error deserializing embedding from DB for {imagePath}", ex);
                                    cachedEntry = null; // Traktuj jako cache miss
                                }
                            }
                        }
                    }
                }
            }

            if (entryFoundAndValid && cachedEntry?.Embedding != null)
            {
                return cachedEntry.Embedding;
            }

            SimpleFileLogger.Log($"SQLite Cache miss or invalid for: {imagePath}. Fetching new embedding.");
            float[]? newEmbedding = await embeddingProvider(imagePath);

            if (newEmbedding != null)
            {
                lock (_dbLock)
                {
                    using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                    {
                        connection.Open();
                        // Użyj UPSERT (INSERT OR REPLACE) do wstawienia lub aktualizacji wpisu
                        string upsertQuery = $@"
                            INSERT OR REPLACE INTO {TableName} (ImagePath, Embedding, LastModifiedUtc, FileSize)
                            VALUES (@ImagePath, @Embedding, @LastModifiedUtc, @FileSize);";
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = upsertQuery;
                            command.Parameters.AddWithValue("@ImagePath", imagePath);
                            command.Parameters.AddWithValue("@Embedding", JsonSerializer.SerializeToUtf8Bytes(newEmbedding)); // Serializacja do BLOB
                            command.Parameters.AddWithValue("@LastModifiedUtc", currentFileLastModifiedUtc.Ticks);
                            command.Parameters.AddWithValue("@FileSize", currentFileSize);
                            command.ExecuteNonQuery();
                            SimpleFileLogger.Log($"Saved/Updated embedding to SQLite for: {imagePath}");
                        }
                    }
                }
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
                    // Opcjonalnie: VACUUM, aby zmniejszyć rozmiar pliku bazy danych po usunięciu wielu wierszy
                    // string vacuumQuery = "VACUUM;";
                    // using (var command = connection.CreateCommand()) { command.CommandText = vacuumQuery; command.ExecuteNonQuery(); }
                }
            }
            SimpleFileLogger.LogHighLevelInfo("SQLite embedding cache cleared (all entries deleted).");
        }

        // Implementacja IDisposable, jeśli jest potrzebna do zarządzania połączeniem
        // W przypadku SqliteConnection używanego w blokach `using`, jawne Dispose nie jest krytyczne
        // dla samej klasy serwisu, ale dobra praktyka.
        public void Dispose()
        {
            // Obecnie połączenia są otwierane i zamykane w każdej metodzie, więc
            // dedykowane Dispose dla klasy może nie być konieczne, chyba że
            // chcielibyśmy utrzymać jedno połączenie otwarte dłużej.
            // Na razie pozostawiam puste.
            GC.SuppressFinalize(this);
        }
    }
}