// Services/ImageMetadataService.cs
using CosplayManager.Models;
using SixLabors.ImageSharp; // Dla Image.IdentifyAsync
// Usunięto using MetadataExtractor, jeśli nie jest używany w tej uproszczonej wersji.
// Jeśli chcesz dodać szczegółowe metadane, odkomentuj i dodaj odpowiednie usingi.
// using MetadataExtractor; 
// using MetadataExtractor.Formats.Exif;
// using MetadataExtractor.Formats.Jpeg;
// using MetadataExtractor.Formats.Png;
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
                // Szybkie pobranie wymiarów za pomocą ImageSharp [6, 7]
                IImageInfo? imageInfo = await Image.IdentifyAsync(filePath);
                if (imageInfo == null) return null;

                var entry = new ImageFileEntry
                {
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    Width = imageInfo.Width,
                    Height = imageInfo.Height
                    // Możesz tutaj dodać odczyt innych metadanych z imageInfo.Metadata, jeśli są potrzebne
                };

                // Jeśli potrzebujesz bardziej szczegółowych metadanych z MetadataExtractor:
                // IEnumerable<MetadataExtractor.Directory> directories = ImageMetadataReader.ReadMetadata(filePath); [8, 9]
                // var jpegDirectory = directories.OfType<JpegDirectory>().FirstOrDefault();
                // if (jpegDirectory!= null && jpegDirectory.TryGetInt32(JpegDirectory.TagImageWidth, out int jpegWidth)) { /*... */ } [10]
                // var exifSubIfdDirectory = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                // if (exifSubIfdDirectory!= null && exifSubIfdDirectory.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out int exifWidth)) { /*... */ } [11, 10]


                return entry;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Błąd odczytu metadanych dla {filePath}: {ex.Message}");
                return null;
            }
        }
    }
}