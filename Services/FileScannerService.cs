// Plik: CosplayManager/Services/FileScannerService.cs
using System;
using System.Collections.Generic;
using System.IO;
// using System.Linq; // Usunięto, jeśli nie jest bezpośrednio używane po zmianach
using System.Threading.Tasks;

namespace CosplayManager.Services
{
    public class FileScannerService
    {
        // Zmieniono na public readonly, aby było dostępne z ViewModelu dla uproszczenia,
        // lub można było dodać publiczną metodę IsExtensionSupported.
        // Dla bezpieczeństwa, zostawmy private i dodajmy metodę.
        private readonly HashSet<string> _supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

        // *** NOWA METODA PUBLICZNA ***
        public bool IsExtensionSupported(string? extension)
        {
            if (string.IsNullOrEmpty(extension)) return false;
            return _supportedExtensions.Contains(extension.ToLowerInvariant()); // Upewnij się, że porównujesz małe litery i usuwasz kropkę, jeśli jest
        }
        // *** KONIEC NOWEJ METODY ***

        public async Task<List<string>> ScanDirectoryAsync(string rootPath)
        {
            var imageFiles = new List<string>();
            if (!Directory.Exists(rootPath))
            {
                SimpleFileLogger.LogError($"FileScannerService: Directory does not exist: {rootPath}");
                return imageFiles;
            }

            SimpleFileLogger.Log($"FileScannerService: Starting scan in directory: {rootPath}");
            await Task.Run(() =>
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories))
                    {
                        // Użyj nowej metody do sprawdzenia rozszerzenia
                        if (IsExtensionSupported(Path.GetExtension(file)))
                        {
                            imageFiles.Add(file);
                        }
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    SimpleFileLogger.LogError($"FileScannerService: Access denied during scan of '{rootPath}'.", ex);
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"FileScannerService: IO error during scan of '{rootPath}'.", ex);
                }
            });
            SimpleFileLogger.Log($"FileScannerService: Scan complete. Found {imageFiles.Count} supported files in '{rootPath}'.");
            return imageFiles;
        }
    }
}