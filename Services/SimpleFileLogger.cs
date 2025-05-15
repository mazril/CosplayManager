// Plik: CosplayManager/Services/SimpleFileLogger.cs
using System;
using System.IO;
using System.Linq;
using System.Text; // Potrzebne dla Encoding

namespace CosplayManager.Services
{
    public static class SimpleFileLogger
    {
        private static readonly string LogFilePath;
        private static readonly object LockObj = new object(); // Do synchronizacji zapisu

        static SimpleFileLogger()
        {
            // Ustawienie ścieżki do pliku logu w folderze aplikacji
            string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logDirectory))
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"FATAL LOGGER INIT ERROR (Create Directory): {ex.Message}");
                    // Jeśli nie można utworzyć folderu logów, spróbuj logować w katalogu głównym aplikacji
                    logDirectory = AppDomain.CurrentDomain.BaseDirectory;
                }
            }
            // Nazwa pliku logu z datą, aby tworzyć nowy log każdego dnia
            LogFilePath = Path.Combine(logDirectory, $"CosplayManager_Log_{DateTime.Now:yyyy-MM-dd}.txt");

            try
            {
                Log("Logger initialized.");
            }
            catch
            {
                // Ignoruj błąd logowania podczas inicjalizacji statycznej, jeśli ścieżka jest nieprawidłowa
            }
        }

        public static void Log(string message)
        {
            try
            {
                lock (LockObj) // Prosta synchronizacja, aby uniknąć problemów z wieloma wątkami piszącymi jednocześnie
                {
                    // Zapis do pliku z datą i godziną
                    string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, logEntry, Encoding.UTF8); // Użyj UTF8 dla polskich znaków
                }
                // Opcjonalnie, wypisz też do konsoli debugowania
                System.Diagnostics.Debug.WriteLine($"LOG: {message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FATAL LOGGER ERROR (Log): {ex.Message} for message: {message}");
                // W przypadku błędu logowania, nie rób nic więcej, aby nie powodować pętli błędów
            }
        }

        public static void LogError(string message, Exception? ex = null)
        {
            string errorMessage = $"ERROR: {message}";
            if (ex != null)
            {
                // Logujemy typ wyjątku, wiadomość i skrócony ślad stosu dla zwięzłości
                // Pełny ślad stosu jest często bardzo długi i może zaciemniać logi
                errorMessage += $"{Environment.NewLine}Exception Type: {ex.GetType().FullName}";
                errorMessage += $"{Environment.NewLine}Exception Message: {ex.Message}";
                if (ex.StackTrace != null)
                {
                    errorMessage += $"{Environment.NewLine}Stack Trace (short): {ex.StackTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.None).FirstOrDefault()}";
                }
            }
            Log(errorMessage);
        }
    }
}