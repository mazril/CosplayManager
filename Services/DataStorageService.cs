// Plik: Services/DataStorageService.cs
using Microsoft.Data.Sqlite;
using CosplayManager.Models; // Dla CategoryProfile i EmbeddingCacheEntry
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosplayManager.Services
{
    public class DataStorageService : IDisposable
    {
        private readonly string _databasePath;
        private readonly object _dbLock = new object();

        private const string DbFileName = "cosplay_manager_data.db";
        private const string EmbeddingsTableName = "Embeddings";
        private const string ProfilesTableName = "CategoryProfiles";

        private static readonly TimeSpan DateComparisonTolerance = TimeSpan.FromSeconds(2);

        public DataStorageService(string databaseDirectory = "Data")
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string fullDatabaseDirectoryPath = Path.Combine(baseDirectory, databaseDirectory);

            if (!Directory.Exists(fullDatabaseDirectoryPath))
            {
                Directory.CreateDirectory(fullDatabaseDirectoryPath);
            }
            _databasePath = Path.Combine(fullDatabaseDirectoryPath, DbFileName);

            InitializeDatabase();
            SimpleFileLogger.LogHighLevelInfo($"DataStorageService initialized. Database path: {_databasePath}");
        }

        private void InitializeDatabase()
        {
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string createEmbeddingsTableQuery = $@"
                        CREATE TABLE IF NOT EXISTS {EmbeddingsTableName} (
                            ImagePath TEXT PRIMARY KEY,
                            Embedding BLOB NOT NULL,
                            LastModifiedUtc INTEGER NOT NULL, 
                            FileSize INTEGER NOT NULL
                        );";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = createEmbeddingsTableQuery;
                        command.ExecuteNonQuery();
                    }

                    string createProfilesTableQuery = $@"
                        CREATE TABLE IF NOT EXISTS {ProfilesTableName} (
                            CategoryName TEXT PRIMARY KEY,
                            ModelName TEXT NOT NULL,
                            CharacterName TEXT NOT NULL,
                            CentroidEmbedding_Blob BLOB,
                            SourceImagePaths_Json TEXT,
                            LastCalculatedUtc_Ticks INTEGER, -- Przechowuje Ticks z DateTime
                            ImageCountInProfile INTEGER NOT NULL DEFAULT 0
                        );";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = createProfilesTableQuery;
                        command.ExecuteNonQuery();
                    }

                    string createModelNameIndexQuery = $@"
                        CREATE INDEX IF NOT EXISTS IDX_CategoryProfiles_ModelName 
                        ON {ProfilesTableName} (ModelName);";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = createModelNameIndexQuery;
                        command.ExecuteNonQuery();
                    }
                }
            }
            SimpleFileLogger.Log($"Database tables ({EmbeddingsTableName}, {ProfilesTableName}) initialized/checked at {_databasePath}");
        }

        // --- Metody dla Embedding Cache (pozostają takie same) ---
        public async Task<float[]?> GetOrUpdateEmbeddingAsync(
            string imagePath,
            DateTime currentFileLastModifiedUtc,
            long currentFileSize,
            Func<string, Task<float[]?>> embeddingProvider)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                SimpleFileLogger.LogWarning($"GetOrUpdateEmbeddingAsync: Invalid imagePath.");
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
                        FROM {EmbeddingsTableName}
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
                                    long lastModifiedTicksFromDb = reader.GetInt64(reader.GetOrdinal("LastModifiedUtc"));
                                    long fileSizeFromDb = reader.GetInt64(reader.GetOrdinal("FileSize"));

                                    cachedEntry = new EmbeddingCacheEntry
                                    {
                                        Embedding = JsonSerializer.Deserialize<float[]>(embeddingBlob),
                                        LastModifiedUtc = new DateTime(lastModifiedTicksFromDb, DateTimeKind.Utc),
                                        FileSize = fileSizeFromDb
                                    };

                                    bool sizeMatches = cachedEntry.FileSize == currentFileSize;
                                    bool dateMatches = Math.Abs((cachedEntry.LastModifiedUtc - currentFileLastModifiedUtc).TotalSeconds) < DateComparisonTolerance.TotalSeconds;

                                    if (sizeMatches && dateMatches && cachedEntry.Embedding != null && cachedEntry.Embedding.Any())
                                    {
                                        SimpleFileLogger.Log($"SQLite Embedding Cache hit for: {imagePath}");
                                        entryFoundAndValid = true;
                                    }
                                    else
                                    {
                                        SimpleFileLogger.LogWarning($"SQLite Embedding Cache invalid for: {imagePath}. " +
                                            $"SizeMatch: {sizeMatches} (DB: {cachedEntry.FileSize}, Current: {currentFileSize}). " +
                                            $"DateMatch ({DateComparisonTolerance.TotalSeconds}s tol.): {dateMatches} (DB: {cachedEntry.LastModifiedUtc:o}, Current: {currentFileLastModifiedUtc:o}). " +
                                            $"EmbeddingNullOrEmpty: {cachedEntry.Embedding == null || !cachedEntry.Embedding.Any()}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    SimpleFileLogger.LogError($"Error deserializing/processing embedding from DB for {imagePath}", ex);
                                    cachedEntry = null;
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

            SimpleFileLogger.Log($"SQLite Embedding Cache miss or invalid for: {imagePath}. Fetching new embedding.");
            float[]? newEmbedding = await embeddingProvider(imagePath);

            if (newEmbedding != null && newEmbedding.Any())
            {
                lock (_dbLock)
                {
                    using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                    {
                        connection.Open();
                        string upsertQuery = $@"
                            INSERT OR REPLACE INTO {EmbeddingsTableName} (ImagePath, Embedding, LastModifiedUtc, FileSize)
                            VALUES (@ImagePath, @Embedding, @LastModifiedUtc, @FileSize);";
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = upsertQuery;
                            command.Parameters.AddWithValue("@ImagePath", imagePath);
                            command.Parameters.AddWithValue("@Embedding", JsonSerializer.SerializeToUtf8Bytes(newEmbedding));
                            command.Parameters.AddWithValue("@LastModifiedUtc", currentFileLastModifiedUtc.Ticks);
                            command.Parameters.AddWithValue("@FileSize", currentFileSize);
                            command.ExecuteNonQuery();
                            SimpleFileLogger.Log($"Saved/Updated embedding to SQLite for: {imagePath}");
                        }
                    }
                }
            }
            else
            {
                SimpleFileLogger.LogWarning($"Embedding provider returned null or empty embedding for: {imagePath}. Not caching.");
            }
            return newEmbedding;
        }

        public void ClearEmbeddingsCache()
        {
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string deleteQuery = $"DELETE FROM {EmbeddingsTableName};";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = deleteQuery;
                        command.ExecuteNonQuery();
                    }
                    string vacuumQuery = $"VACUUM {EmbeddingsTableName};";
                    using (var command = connection.CreateCommand()) { command.CommandText = vacuumQuery; command.ExecuteNonQuery(); }
                }
            }
            SimpleFileLogger.LogHighLevelInfo($"SQLite Embeddings cache cleared ({EmbeddingsTableName} table).");
        }

        // --- NOWE Metody dla Category Profiles ---

        public Task SaveProfileAsync(CategoryProfile profile) // Usunięto modelName i characterName, bo są w profile.CategoryName
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            // Wydzielenie ModelName i CharacterName z CategoryName
            string modelName = "UnknownModel"; // Domyślne wartości
            string characterName = "UnknownCharacter";
            if (!string.IsNullOrWhiteSpace(profile.CategoryName))
            {
                var parts = profile.CategoryName.Split(new[] { " - " }, 2, StringSplitOptions.None);
                modelName = parts.Length > 0 ? parts[0].Trim() : profile.CategoryName.Trim();
                characterName = parts.Length > 1 ? string.Join(" - ", parts.Skip(1)).Trim() : "General";
                if (string.IsNullOrWhiteSpace(modelName)) modelName = "UnknownModel";
                if (string.IsNullOrWhiteSpace(characterName) && parts.Length == 1) characterName = "General"; // Jeśli tylko nazwa modelki, postać to General
                else if (string.IsNullOrWhiteSpace(characterName)) characterName = "UnknownCharacter";

            }


            SimpleFileLogger.Log($"SaveProfileAsync: Attempting to save profile '{profile.CategoryName}'");
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string upsertQuery = $@"
                        INSERT OR REPLACE INTO {ProfilesTableName} 
                        (CategoryName, ModelName, CharacterName, CentroidEmbedding_Blob, SourceImagePaths_Json, LastCalculatedUtc_Ticks, ImageCountInProfile)
                        VALUES (@CategoryName, @ModelName, @CharacterName, @CentroidEmbedding_Blob, @SourceImagePaths_Json, @LastCalculatedUtc_Ticks, @ImageCountInProfile);";

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = upsertQuery;
                        command.Parameters.AddWithValue("@CategoryName", profile.CategoryName);
                        command.Parameters.AddWithValue("@ModelName", modelName); // Użyj wydzielonej nazwy
                        command.Parameters.AddWithValue("@CharacterName", characterName); // Użyj wydzielonej nazwy

                        if (profile.CentroidEmbedding != null && profile.CentroidEmbedding.Any())
                            command.Parameters.AddWithValue("@CentroidEmbedding_Blob", JsonSerializer.SerializeToUtf8Bytes(profile.CentroidEmbedding));
                        else
                            command.Parameters.AddWithValue("@CentroidEmbedding_Blob", DBNull.Value);

                        string sourceImagePathsJson = JsonSerializer.Serialize(profile.SourceImagePaths ?? new List<string>());
                        command.Parameters.AddWithValue("@SourceImagePaths_Json", sourceImagePathsJson);

                        command.Parameters.AddWithValue("@LastCalculatedUtc_Ticks", profile.LastCalculatedUtc.Ticks);
                        command.Parameters.AddWithValue("@ImageCountInProfile", profile.ImageCountInProfile);

                        command.ExecuteNonQuery();
                    }
                }
            }
            SimpleFileLogger.Log($"Profile '{profile.CategoryName}' saved/updated in SQLite.");
            return Task.CompletedTask;
        }

        private CategoryProfile MapReaderToProfile(SqliteDataReader reader)
        {
            string categoryName = reader.GetString(reader.GetOrdinal("CategoryName"));
            var profile = new CategoryProfile(categoryName);

            if (!reader.IsDBNull(reader.GetOrdinal("CentroidEmbedding_Blob")))
            {
                byte[] centroidBlob = (byte[])reader["CentroidEmbedding_Blob"];
                profile.CentroidEmbedding = JsonSerializer.Deserialize<float[]>(centroidBlob);
            }

            if (!reader.IsDBNull(reader.GetOrdinal("SourceImagePaths_Json")))
            {
                string pathsJson = reader.GetString(reader.GetOrdinal("SourceImagePaths_Json"));
                profile.SourceImagePaths = JsonSerializer.Deserialize<List<string>>(pathsJson) ?? new List<string>();
            }
            else
            {
                profile.SourceImagePaths = new List<string>();
            }

            if (!reader.IsDBNull(reader.GetOrdinal("LastCalculatedUtc_Ticks")))
            {
                profile.LastCalculatedUtc = new DateTime(reader.GetInt64(reader.GetOrdinal("LastCalculatedUtc_Ticks")), DateTimeKind.Utc);
            }
            // profile.ImageCountInProfile jest właściwością obliczaną, ale możemy ją odczytać dla spójności lub logowania, jeśli baza ją przechowuje
            // int imageCountFromDb = reader.GetInt32(reader.GetOrdinal("ImageCountInProfile"));
            // SimpleFileLogger.Log($"MapReaderToProfile for '{categoryName}': DB ImageCount = {imageCountFromDb}, Calculated = {profile.ImageCountInProfile}");

            return profile;
        }

        public Task<CategoryProfile?> GetProfileAsync(string categoryName)
        {
            CategoryProfile? profile = null;
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string selectQuery = $@"SELECT * FROM {ProfilesTableName} WHERE CategoryName = @CategoryName;";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = selectQuery;
                        command.Parameters.AddWithValue("@CategoryName", categoryName);
                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                profile = MapReaderToProfile(reader);
                            }
                        }
                    }
                }
            }
            return Task.FromResult(profile);
        }

        public Task<List<CategoryProfile>> GetAllProfilesAsync()
        {
            var profiles = new List<CategoryProfile>();
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string selectQuery = $"SELECT * FROM {ProfilesTableName} ORDER BY CategoryName;"; // Dodano sortowanie
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = selectQuery;
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                profiles.Add(MapReaderToProfile(reader));
                            }
                        }
                    }
                }
            }
            SimpleFileLogger.Log($"GetAllProfilesAsync: Loaded {profiles.Count} profiles from SQLite.");
            return Task.FromResult(profiles);
        }

        public Task<List<CategoryProfile>> GetProfilesForModelAsync(string modelName)
        {
            var profiles = new List<CategoryProfile>();
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string selectQuery = $@"SELECT * FROM {ProfilesTableName} WHERE ModelName = @ModelName ORDER BY CharacterName;"; // Dodano sortowanie
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = selectQuery;
                        command.Parameters.AddWithValue("@ModelName", modelName);
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                profiles.Add(MapReaderToProfile(reader));
                            }
                        }
                    }
                }
            }
            SimpleFileLogger.Log($"GetProfilesForModelAsync: Loaded {profiles.Count} profiles for model '{modelName}' from SQLite.");
            return Task.FromResult(profiles);
        }

        public Task DeleteProfileAsync(string categoryName)
        {
            SimpleFileLogger.Log($"DeleteProfileAsync: Attempting to delete profile '{categoryName}' from SQLite.");
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string deleteQuery = $"DELETE FROM {ProfilesTableName} WHERE CategoryName = @CategoryName;";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = deleteQuery;
                        command.Parameters.AddWithValue("@CategoryName", categoryName);
                        int rowsAffected = command.ExecuteNonQuery();
                        if (rowsAffected > 0)
                            SimpleFileLogger.Log($"Profile '{categoryName}' deleted from SQLite.");
                        else
                            SimpleFileLogger.Log($"Profile '{categoryName}' not found in SQLite for deletion.");
                    }
                }
            }
            return Task.CompletedTask;
        }

        public Task DeleteProfilesForModelAsync(string modelName)
        {
            SimpleFileLogger.Log($"DeleteProfilesForModelAsync: Attempting to delete all profiles for model '{modelName}' from SQLite.");
            int totalRowsAffected = 0;
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string deleteQuery = $"DELETE FROM {ProfilesTableName} WHERE ModelName = @ModelName;";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = deleteQuery;
                        command.Parameters.AddWithValue("@ModelName", modelName);
                        totalRowsAffected = command.ExecuteNonQuery();
                        SimpleFileLogger.Log($"{totalRowsAffected} profiles for model '{modelName}' deleted from SQLite.");
                    }
                }
            } 
            return Task.CompletedTask;
        } 

        public void ClearAllProfilesData()
        {
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
                {
                    connection.Open();
                    string deleteQuery = $"DELETE FROM {ProfilesTableName};";
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = deleteQuery;
                        command.ExecuteNonQuery();
                    }
                    string vacuumQuery = $"VACUUM {ProfilesTableName};";
                    using (var command = connection.CreateCommand()) { command.CommandText = vacuumQuery; command.ExecuteNonQuery(); }
                }
            }
            SimpleFileLogger.LogHighLevelInfo($"All CategoryProfiles data cleared from SQLite ({ProfilesTableName} table).");
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}