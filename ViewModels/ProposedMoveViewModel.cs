// Plik: ViewModels/ProposedMoveViewModel.cs
using CosplayManager.Models;
using CosplayManager.Services; // Potrzebne dla SimpleFileLogger, jeśli go tu używamy
using CosplayManager.ViewModels.Base; // Potrzebne dla ObservableObject
using System.IO; // Dla Path
using System.Threading.Tasks; // Dla Task
using System.Windows.Media.Imaging; // Dla BitmapImage

namespace CosplayManager.ViewModels
{
    public class ProposedMoveViewModel : ObservableObject
    {
        private readonly ProposedMove _move;
        private bool _isApproved;
        private BitmapImage? _sourceThumbnail;
        private BitmapImage? _targetThumbnail;
        private bool _isLoadingSourceThumbnail;
        private bool _isLoadingTargetThumbnail;

        public ProposedMove OriginalMove => _move;

        public ImageFileEntry SourceImage => _move.SourceImage;
        public ImageFileEntry? TargetImageDisplay => _move.TargetImageDisplay; // ZMIANA NAZWY
        public string ProposedTargetPath => _move.ProposedTargetPath;
        public double Similarity => _move.Similarity;
        public string TargetCategoryProfileName => _move.TargetCategoryProfileName;
        public ProposedMoveActionType Action { get => _move.Action; set { _move.Action = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActionDescription)); } }


        public bool IsApproved
        {
            get => _isApproved;
            set => SetProperty(ref _isApproved, value);
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
        public string TargetFileNameDisplay => TargetImageDisplay != null ? Path.GetFileName(TargetImageDisplay.FilePath) : (Action == ProposedMoveActionType.CopyNew || Action == ProposedMoveActionType.OverwriteExisting ? Path.GetFileName(ProposedTargetPath) : "---");


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

        public ProposedMoveViewModel(ProposedMove move)
        {
            _move = move;
            // Domyślnie wszystkie sugestie są zatwierdzone, użytkownik odznacza te, których nie chce
            _isApproved = (move.Action != ProposedMoveActionType.NoAction);
        }

        public async Task LoadThumbnailsAsync()
        {
            if (SourceImage != null && SourceThumbnail == null && !IsLoadingSourceThumbnail)
            {
                IsLoadingSourceThumbnail = true;
                // SourceImage powinien być kompletnym ImageFileEntry z metadanymi
                // Jeśli SourceImage.Thumbnail jest już załadowany przez ImageFileEntry, użyj go
                if (SourceImage.Thumbnail != null)
                {
                    SourceThumbnail = SourceImage.Thumbnail;
                }
                else
                {
                    // Await LoadThumbnailAsync from ImageFileEntry
                    await SourceImage.LoadThumbnailAsync();
                    SourceThumbnail = SourceImage.Thumbnail;
                }
                IsLoadingSourceThumbnail = false;
            }

            // ZMIANA NAZWY: TargetImageDisplay
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
    }
}