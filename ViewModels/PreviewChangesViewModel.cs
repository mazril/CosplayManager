// Plik: ViewModels/PreviewChangesViewModel.cs
using CosplayManager.Models;
using CosplayManager.Services;
using CosplayManager.ViewModels.Base;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace CosplayManager.ViewModels
{
    public class PreviewChangesViewModel : ObservableObject
    {
        private ObservableCollection<ProposedMoveViewModel> _proposedMoves;
        public ObservableCollection<ProposedMoveViewModel> ProposedMoves
        {
            get => _proposedMoves;
            set => SetProperty(ref _proposedMoves, value);
        }

        private double _currentSimilarityThreshold;
        public double CurrentSimilarityThreshold
        {
            get => _currentSimilarityThreshold;
            set
            {
                if (SetProperty(ref _currentSimilarityThreshold, value))
                {
                    FilterVisibleMoves();
                }
            }
        }

        private Action<bool?>? _closeAction; // Action to close the dialog

        public ICommand ApproveAllCommand { get; }
        public ICommand DisapproveAllCommand { get; }
        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }

        private List<ProposedMoveViewModel> _allMovesMasterList; // Przechowuje wszystkie ruchy, niezależnie od progu

        public PreviewChangesViewModel(List<ProposedMove> moves, double initialSimilarityThreshold)
        {
            _allMovesMasterList = moves.Select(m => new ProposedMoveViewModel(m)).ToList();
            _currentSimilarityThreshold = initialSimilarityThreshold;

            // Inicjalizacja ProposedMoves od razu z przefiltrowaną listą
            ProposedMoves = new ObservableCollection<ProposedMoveViewModel>(
                _allMovesMasterList.Where(vm => vm.Similarity >= _currentSimilarityThreshold)
            );

            // Ładowanie miniaturek dla początkowo widocznych ruchów
            LoadThumbnailsForVisibleMoves();

            ApproveAllCommand = new RelayCommand(_ => SetAllApproved(true));
            DisapproveAllCommand = new RelayCommand(_ => SetAllApproved(false));
            ConfirmCommand = new RelayCommand(_ => CloseDialog(true));
            CancelCommand = new RelayCommand(_ => CloseDialog(false));
        }

        public void SetCloseAction(Action<bool?> closeAction)
        {
            _closeAction = closeAction;
        }

        private void CloseDialog(bool? dialogResult)
        {
            _closeAction?.Invoke(dialogResult);
        }


        private void SetAllApproved(bool approved)
        {
            foreach (var moveVM in ProposedMoves) // Działaj tylko na widocznych (przefiltrowanych)
            {
                moveVM.IsApproved = approved;
            }
        }

        public List<ProposedMove> GetApprovedMoves()
        {
            return ProposedMoves.Where(vm => vm.IsApproved).Select(vm => vm.OriginalMove).ToList();
        }

        private void FilterVisibleMoves()
        {
            // Filtruj na podstawie _allMovesMasterList i aktualizuj ProposedMoves
            var filtered = _allMovesMasterList.Where(vm => vm.Similarity >= CurrentSimilarityThreshold).ToList();
            ProposedMoves = new ObservableCollection<ProposedMoveViewModel>(filtered);
            LoadThumbnailsForVisibleMoves(); // Załaduj miniaturki dla nowo widocznych
            OnPropertyChanged(nameof(ProposedMoves)); // Upewnij się, że UI się odświeży
        }

        private async void LoadThumbnailsForVisibleMoves()
        {
            // Aby uniknąć wielokrotnego ładowania, można by dodać flagę per ViewModel,
            // ale ImageFileEntry.LoadThumbnailAsync już ma wewnętrzną logikę
            foreach (var moveVM in ProposedMoves)
            {
                if (moveVM.SourceThumbnail == null && !moveVM.IsLoadingSourceThumbnail)
                {
                    // ZMIANA NAZWY: TargetImageDisplay
                    if (moveVM.TargetImageDisplay?.FilePath != moveVM.SourceImage.FilePath) // Nie ładuj jeśli to ten sam obraz
                    {
                        await moveVM.LoadThumbnailsAsync(); // LoadThumbnailAsync jest w ProposedMoveViewModel
                    }
                    else if (moveVM.TargetImageDisplay == null) // Jeśli nie ma TargetImageDisplay, zawsze ładuj Source
                    {
                        await moveVM.LoadThumbnailsAsync();
                    }
                }
            }
        }
    }
}