// Plik: CosplayManager/ViewModels/ProposedMoveViewModel.cs
using CosplayManager.Models;
using CosplayManager.ViewModels.Base;
using CosplayManager.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CosplayManager.ViewModels
{
    public class ProposedMoveViewModel : ObservableObject // Zmieniono na public
    {
        // ... (reszta kodu bez zmian)
        private ImageFileEntry _sourceImage;
        public ImageFileEntry SourceImage
        {
            get => _sourceImage;
            set => SetProperty(ref _sourceImage, value);
        }

        private ImageFileEntry? _targetImage;
        public ImageFileEntry? TargetImage
        {
            get => _targetImage;
            set => SetProperty(ref _targetImage, value);
        }

        private string _proposedTargetPath;
        public string ProposedTargetPath
        {
            get => _proposedTargetPath;
            set => SetProperty(ref _proposedTargetPath, value);
        }

        private double _similarity;
        public double Similarity
        {
            get => _similarity;
            set => SetProperty(ref _similarity, value);
        }

        private bool _isApprovedForMove;
        public bool IsApprovedForMove
        {
            get => _isApprovedForMove;
            set => SetProperty(ref _isApprovedForMove, value);
        }

        private ProposedMoveActionType _action;
        public ProposedMoveActionType Action
        {
            get => _action;
            set => SetProperty(ref _action, value);
        }

        private string _targetCategoryProfileName;
        public string TargetCategoryProfileName
        {
            get => _targetCategoryProfileName;
            set => SetProperty(ref _targetCategoryProfileName, value);
        }

        private BitmapImage? _sourceThumbnail;
        public BitmapImage? SourceThumbnail
        {
            get => _sourceThumbnail;
            private set => SetProperty(ref _sourceThumbnail, value);
        }

        private BitmapImage? _targetThumbnail;
        public BitmapImage? TargetThumbnail
        {
            get => _targetThumbnail;
            private set => SetProperty(ref _targetThumbnail, value);
        }

        private bool _isLoadingThumbnails;
        public bool IsLoadingThumbnails
        {
            get => _isLoadingThumbnails;
            private set => SetProperty(ref _isLoadingThumbnails, value);
        }

        public ProposedMoveViewModel(Models.ProposedMove modelMove)
        {
            _sourceImage = modelMove.SourceImage;
            _targetImage = modelMove.TargetImage;
            _proposedTargetPath = modelMove.ProposedTargetPath;
            _similarity = modelMove.Similarity;
            _isApprovedForMove = true;

            _action = modelMove.Action;
            _targetCategoryProfileName = modelMove.TargetCategoryProfileName;

            _ = LoadThumbnailsAsync();
        }

        private async Task LoadThumbnailsAsync()
        {
            IsLoadingThumbnails = true;
            try
            {
                if (SourceImage != null && !string.IsNullOrWhiteSpace(SourceImage.FilePath) && File.Exists(SourceImage.FilePath))
                {
                    SourceThumbnail = await CreateThumbnailAsync(SourceImage.FilePath, 150);
                }
                if (TargetImage != null && !string.IsNullOrWhiteSpace(TargetImage.FilePath) && File.Exists(TargetImage.FilePath))
                {
                    TargetThumbnail = await CreateThumbnailAsync(TargetImage.FilePath, 150);
                }
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"Error loading thumbnails for source: {SourceImage?.FilePath}, target: {TargetImage?.FilePath}", ex);
            }
            finally
            {
                IsLoadingThumbnails = false;
            }
        }

        private Task<BitmapImage?> CreateThumbnailAsync(string filePath, int size)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(filePath))
                    {
                        SimpleFileLogger.Log($"CreateThumbnailAsync: File not found '{filePath}'");
                        return null;
                    }

                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(filePath);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = size;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"Error creating thumbnail for {filePath}", ex);
                    return null;
                }
            });
        }

        public void ReleaseThumbnails()
        {
            SimpleFileLogger.Log($"Releasing thumbnails for Source: {SourceImage?.FileName}, Target: {TargetImage?.FileName}");
            SourceThumbnail = null;
            TargetThumbnail = null;
        }
    }
}