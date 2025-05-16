// Plik: Services/ImageMetadataService.cs
using CosplayManager.Models;
using SixLabors.ImageSharp;
using System.IO;
using System.Threading.Tasks;

namespace CosplayManager.Services
{
    public class ImageMetadataService
    {
        public async Task<ImageFileEntry?> ExtractMetadataAsync(string filePath)
        {
            try
            {
                // Szybkie pobranie wymiarów za pomocą ImageSharp
                IImageInfo? imageInfo = await Image.IdentifyAsync(filePath);
                if (imageInfo == null)
                {
                    SimpleFileLogger.LogWarning($"ImageMetadataService: Nie udało się zidentyfikować obrazu (Image.IdentifyAsync zwrócił null): {filePath}");
                    return null;
                }

                FileInfo fileInfo = new FileInfo(filePath); // Pobierz FileInfo raz

                var entry = new ImageFileEntry
                {
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    Width = imageInfo.Width,
                    Height = imageInfo.Height,
                    FileLastModifiedUtc = fileInfo.LastWriteTimeUtc, // Ustawienie nowej właściwości
                    FileSize = fileInfo.Length                     // Ustawienie nowej właściwości
                };
                return entry;
            }
            catch (System.Exception ex)
            {
                SimpleFileLogger.LogError($"Błąd odczytu metadanych dla {filePath}", ex);
                return null;
            }
        }
    }
}