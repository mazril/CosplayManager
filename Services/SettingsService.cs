// Plik: Services/SettingsService.cs
using CosplayManager.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosplayManager.Services
{
    public class SettingsService
    {
        private readonly string _settingsFilePath;
        private const string SettingsFileName = "app_settings.json";

        public SettingsService()
        {
            // Zapisz ustawienia w folderze danych aplikacji użytkownika
            string appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appSpecificFolder = Path.Combine(appDataFolder, "CosplayManager"); // Nazwa folderu aplikacji

            if (!Directory.Exists(appSpecificFolder))
            {
                Directory.CreateDirectory(appSpecificFolder);
            }
            _settingsFilePath = Path.Combine(appSpecificFolder, SettingsFileName);
            SimpleFileLogger.Log($"Ścieżka pliku ustawień aplikacji: {_settingsFilePath}");
        }

        public async Task<UserSettings?> LoadSettingsAsync()
        {
            if (!File.Exists(_settingsFilePath))
            {
                SimpleFileLogger.Log($"Plik ustawień '{_settingsFilePath}' nie istnieje. Zwracam domyślne ustawienia.");
                return new UserSettings(); // Zwróć domyślne, jeśli plik nie istnieje
            }

            try
            {
                SimpleFileLogger.Log($"Próba wczytania ustawień z: {_settingsFilePath}");
                string jsonString = await File.ReadAllTextAsync(_settingsFilePath);
                UserSettings? settings = JsonSerializer.Deserialize<UserSettings>(jsonString);
                SimpleFileLogger.Log("Ustawienia wczytane pomyślnie.");
                return settings ?? new UserSettings(); // Zwróć domyślne, jeśli deserializacja da null
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Błąd podczas wczytywania ustawień z '{_settingsFilePath}'. Zwracam domyślne.", ex);
                return new UserSettings(); // Zwróć domyślne w przypadku błędu
            }
        }

        public async Task SaveSettingsAsync(UserSettings settings)
        {
            if (settings == null)
            {
                SimpleFileLogger.LogError("Próba zapisania pustych ustawień (null). Przerywam.", null);
                return;
            }

            try
            {
                SimpleFileLogger.Log($"Próba zapisania ustawień do: {_settingsFilePath}");
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                string jsonString = JsonSerializer.Serialize(settings, options);
                await File.WriteAllTextAsync(_settingsFilePath, jsonString);
                SimpleFileLogger.Log("Ustawienia zapisane pomyślnie.");
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Błąd podczas zapisywania ustawień do '{_settingsFilePath}'.", ex);
                // Można powiadomić użytkownika, ale na razie tylko logujemy
            }
        }
    }
}