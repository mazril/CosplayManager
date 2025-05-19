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
        private static readonly object LockObj = new object();

        // Publiczna właściwość do kontrolowania logowania debugowania
        public static bool IsDebugLoggingEnabled { get; set; } = false;

        static SimpleFileLogger()
        {
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
                    logDirectory = AppDomain.CurrentDomain.BaseDirectory;
                }
            }
            LogFilePath = Path.Combine(logDirectory, $"CosplayManager_Log_{DateTime.Now:yyyy-MM-dd}.txt");

            try
            {
                // Log startowy loggera zawsze, niezależnie od IsDebugLoggingEnabled
                lock (LockObj)
                {
                    string initialLogEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - INFO: Logger initialized. Debug logging initially: {(IsDebugLoggingEnabled ? "Enabled" : "Disabled")}.{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, initialLogEntry, Encoding.UTF8);
                }
            }
            catch
            {
                // Ignoruj
            }
        }

        public static void Log(string message)
        {
            // Zapis do pliku tylko jeśli logowanie debugowania jest włączone
            if (IsDebugLoggingEnabled)
            {
                try
                {
                    lock (LockObj)
                    {
                        string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - DEBUG: {message}{Environment.NewLine}";
                        File.AppendAllText(LogFilePath, logEntry, Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"FATAL LOGGER ERROR (Log - Debug): {ex.Message} for message: {message}");
                }
            }
            // Wypisanie do konsoli debugowania również zależne od flagi
            if (IsDebugLoggingEnabled)
            {
                System.Diagnostics.Debug.WriteLine($"DEBUG: {message}");
            }
        }

        public static void LogHighLevelInfo(string message) // Nowa metoda dla ważnych, ale nie ostrzegawczych logów
        {
            try
            {
                lock (LockObj)
                {
                    string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - INFO: {message}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, logEntry, Encoding.UTF8);
                }
                // Te logi mogą być również przydatne w konsoli debugowania, nawet jeśli IsDebugLoggingEnabled jest false
                System.Diagnostics.Debug.WriteLine($"INFO: {message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FATAL LOGGER ERROR (LogHighLevelInfo): {ex.Message} for message: {message}");
            }
        }


        public static void LogWarning(string message)
        {
            try
            {
                lock (LockObj)
                {
                    string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - WARNING: {message}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, logEntry, Encoding.UTF8);
                }
                // Komunikaty ostrzegawcze w konsoli debugowania zależne od flagi
                if (IsDebugLoggingEnabled)
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: {message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FATAL LOGGER ERROR (LogWarning): {ex.Message} for message: {message}");
            }
        }

        public static void LogError(string message, Exception? ex = null)
        {
            string errorMessage = $"ERROR: {message}";
            if (ex != null)
            {
                errorMessage += $"{Environment.NewLine}Exception Type: {ex.GetType().FullName}";
                errorMessage += $"{Environment.NewLine}Exception Message: {ex.Message}";
                if (ex.StackTrace != null)
                {
                    errorMessage += $"{Environment.NewLine}Stack Trace (short): {ex.StackTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.None).FirstOrDefault()}";
                }
            }

            try
            {
                lock (LockObj)
                {
                    string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {errorMessage}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, logEntry, Encoding.UTF8);
                }
                // Komunikaty błędów w konsoli debugowania zależne od flagi
                if (IsDebugLoggingEnabled)
                {
                    System.Diagnostics.Debug.WriteLine(errorMessage);
                }
            }
            catch (Exception loggerEx)
            {
                System.Diagnostics.Debug.WriteLine($"FATAL LOGGER ERROR (LogError): {loggerEx.Message} for original error: {message}");
            }
        }
    }
}