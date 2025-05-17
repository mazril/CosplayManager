// Plik: ViewModels/ProposedMoveViewModel.cs
using CosplayManager.Models;
using CosplayManager.Services;
using CosplayManager.ViewModels.Base;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CosplayManager.ViewModels
{
    public class ProposedMoveViewModel : ObservableObject
    {
        private readonly ProposedMove _move;
        private bool _isApprovedForMove; // Nazwa właściwości używana w PreviewChangesViewModel
        private BitmapImage? _sourceThumbnail;
        private BitmapImage? _targetThumbnail;
        private bool _isLoadingSourceThumbnail;
        private bool _isLoadingTargetThumbnail;

        public ProposedMove OriginalMove => _move;

        public ImageFileEntry SourceImage => _move.SourceImage;
        public ImageFileEntry? TargetImageDisplay => _move.TargetImage; // Używamy _move.TargetImage (zgodnie z Modelem)
        public string ProposedTargetPath => _move.ProposedTargetPath;
        public double Similarity => _move.Similarity;
        public string TargetCategoryProfileName => _move.TargetCategoryProfileName;

        public ProposedMoveActionType Action
        {
            get => _move.Action;
            set { if (_move.Action != value) { _move.Action = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActionDescription)); } }
        }

        public bool IsApprovedForMove // Ta nazwa jest używana w PreviewChangesViewModel
        {
            get => _isApprovedForMove;
            set => SetProperty(ref _isApprovedForMove, value);
        }

        public BitmapImage? SourceThumbnail
        {
            get => _sourceThumbnail;
            private set => SetProperty(ref _sourceThumbnail, value);
        }

        public BitmapImage? TargetThumbnail
        {
            get => _targetThumbnail;
            private set => SetProperty(ref _targetThumbnail, value);
        }

        public bool IsLoadingSourceThumbnail
        {
            get => _isLoadingSourceThumbnail;
            private set => SetProperty(ref _isLoadingSourceThumbnail, value);
        }
        public bool IsLoadingTargetThumbnail
        {
            get => _isLoadingTargetThumbnail;
            private set => SetProperty(ref _isLoadingTargetThumbnail, value);
        }

        public string SourceFileName => Path.GetFileName(SourceImage.FilePath);
        public string TargetFileNameDisplay => TargetImageDisplay != null ? Path.GetFileName(TargetImageDisplay.FilePath) :
                                             (Action == ProposedMoveActionType.CopyNew || Action == ProposedMoveActionType.OverwriteExisting ? Path.GetFileName(ProposedTargetPath) : "---");

        public string ActionDescription
        {
            get
            {
                switch (Action)
                {
                    case ProposedMoveActionType.CopyNew: return $"Kopiuj jako nowy do '{TargetCategoryProfileName}'";
                    case ProposedMoveActionType.OverwriteExisting: return $"Nadpisz istniejący w '{TargetCategoryProfileName}'";
                    case ProposedMoveActionType.KeepExistingDeleteSource: return $"Zachowaj w '{TargetCategoryProfileName}', usuń źródło";
                    case ProposedMoveActionType.ConflictKeepBoth: return $"Konflikt nazwy w '{TargetCategoryProfileName}'. Zachowaj oba (zmień nazwę).";
                    case ProposedMoveActionType.NoAction: return "Brak akcji";
                    default: return Action.ToString();
                }
            }
        }

        public ProposedMoveViewModel(Models.ProposedMove modelMove)
        {
            _move = modelMove;
            // Domyślnie wszystko co nie jest NoAction jest do zatwierdzenia, chyba że logika PreviewChangesViewModel to zmieni
            _isApprovedForMove = (modelMove.Action != ProposedMoveActionType.NoAction);

            _ = LoadThumbnailsAsync();
        }

        public async Task LoadThumbnailsAsync()
        {
            if (SourceImage != null && SourceThumbnail == null && !IsLoadingSourceThumbnail)
            {
                IsLoadingSourceThumbnail = true;
                if (SourceImage.Thumbnail != null)
                {
                    SourceThumbnail = SourceImage.Thumbnail;
                }
                else
                {
                    await SourceImage.LoadThumbnailAsync();
                    SourceThumbnail = SourceImage.Thumbnail;
                }
                IsLoadingSourceThumbnail = false;
            }

            if (TargetImageDisplay != null && TargetThumbnail == null && !IsLoadingTargetThumbnail)
            {
                IsLoadingTargetThumbnail = true;
                if (TargetImageDisplay.Thumbnail != null)
                {
                    TargetThumbnail = TargetImageDisplay.Thumbnail;
                }
                else
                {
                    await TargetImageDisplay.LoadThumbnailAsync();
                    TargetThumbnail = TargetImageDisplay.Thumbnail;
                }
                IsLoadingTargetThumbnail = false;
            }
        }

        public void ReleaseThumbnails()
        {
            SimpleFileLogger.Log($"Releasing thumbnails for Source: {SourceImage?.FileName}, Target: {TargetImageDisplay?.FileName}");
            SourceThumbnail = null;
            TargetThumbnail = null;
        }
    }
}