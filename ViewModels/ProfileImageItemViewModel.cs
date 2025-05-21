using CosplayManager.Models;
using CosplayManager.ViewModels.Base;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CosplayManager.ViewModels
{
    public class ProfileImageItemViewModel : ObservableObject
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

        private string _fileName;
        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        private bool _isLoadingThumbnail;
        public bool IsLoadingThumbnail
        {
            get => _isLoadingThumbnail;
            set => SetProperty(ref _isLoadingThumbnail, value);
        }

        public ProfileImageItemViewModel(ImageFileEntry originalImage)
        {
            _originalImage = originalImage;
            _fileName = originalImage.FileName;
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
                Services.SimpleFileLogger.LogError($"Error loading thumbnail for {OriginalImage.FilePath}", ex);
                Thumbnail = null;
            }
            finally
            {
                IsLoadingThumbnail = false;
            }
        }
    }
}