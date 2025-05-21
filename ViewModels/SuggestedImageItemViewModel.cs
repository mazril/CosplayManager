using CosplayManager.Models;
using CosplayManager.ViewModels.Base;
using System.Threading.Tasks;
using System.Windows.Media.Imaging; // Dla BitmapImage

namespace CosplayManager.ViewModels
{
    public class SuggestedImageItemViewModel : ObservableObject
    {
        private ImageFileEntry _originalImage;
        public ImageFileEntry OriginalImage
        {
            get => _originalImage;
            set => SetProperty(ref _originalImage, value);
        }

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set => SetProperty(ref _thumbnail, value);
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private double _similarityScore;
        public double SimilarityScore
        {
            get => _similarityScore;
            set
            {
                if (SetProperty(ref _similarityScore, value))
                {
                    OnPropertyChanged(nameof(SimilarityDisplayText));
                }
            }
        }

        public string SimilarityDisplayText => $"Podob.: {SimilarityScore:P0}"; // Np. Podob.: 92%

        private string _sourceFileName;
        public string SourceFileName
        {
            get => _sourceFileName;
            set => SetProperty(ref _sourceFileName, value);
        }

        private string _sourceDirectoryName; // Np. Nazwa folderu "Mix" lub "Mieszane"
        public string SourceDirectoryName
        {
            get => _sourceDirectoryName;
            set => SetProperty(ref _sourceDirectoryName, value);
        }

        private bool _isLoadingThumbnail;
        public bool IsLoadingThumbnail
        {
            get => _isLoadingThumbnail;
            set => SetProperty(ref _isLoadingThumbnail, value);
        }

        public SuggestedImageItemViewModel(ImageFileEntry originalImage, double similarityScore, string sourceDirectory)
        {
            _originalImage = originalImage;
            _similarityScore = similarityScore;
            _sourceFileName = originalImage.FileName;
            _sourceDirectoryName = sourceDirectory; // Przekaż nazwę folderu źródłowego
            // Ładowanie miniaturki można zainicjować tutaj lub przekazać gotową
        }

        public async Task LoadThumbnailAsync()
        {
            if (Thumbnail != null || string.IsNullOrEmpty(OriginalImage.FilePath)) return;

            IsLoadingThumbnail = true;
            try
            {
                // Poprawione wywołanie
                Thumbnail = await OriginalImage.LoadThumbnailAsync(150); // Użyj domyślnej wartości lub innej odpowiedniej
            }
            catch (System.Exception ex)
            {
                // Log error
                Services.SimpleFileLogger.LogError($"Error loading thumbnail for {OriginalImage.FilePath}", ex);
                Thumbnail = null; // or a default placeholder image
            }
            finally
            {
                IsLoadingThumbnail = false;
            }
        }
    }
}