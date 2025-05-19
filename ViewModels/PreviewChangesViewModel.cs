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
        private ObservableCollection<ProposedMoveViewModel> _proposedMovesList;
        public ObservableCollection<ProposedMoveViewModel> ProposedMovesList
        {
            get => _proposedMovesList;
            set => SetProperty(ref _proposedMovesList, value);
        }

        private double _currentSimilarityThreshold;
        public double CurrentSimilarityThreshold
        {
            get => _currentSimilarityThreshold;
            set
            {
                if (SetProperty(ref _currentSimilarityThreshold, value))
                {
                    ApplyThresholdAndRefresh();
                }
            }
        }

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ApproveAllCommand { get; }
        public ICommand DisapproveAllCommand { get; }
        // RefreshCommand może być zbędny, jeśli filtrowanie działa automatycznie

        private readonly List<ProposedMoveViewModel> _allMovesMasterList;

        // Publiczna właściwość do ustawienia z zewnątrz (z PreviewChangesWindow.xaml.cs lub SplitProfileWindow.xaml.cs)
        public Action<bool?>? CloseAction { get; set; }

        private readonly List<Models.ProposedMove> _initialProposedMoves;

        public PreviewChangesViewModel(List<Models.ProposedMove> initialProposedMoves, double initialThreshold)
        {
            _initialProposedMoves = initialProposedMoves ?? new List<Models.ProposedMove>();
            _currentSimilarityThreshold = initialThreshold;

            _allMovesMasterList = _initialProposedMoves.Select(move => new ProposedMoveViewModel(move)).ToList();

            ProposedMovesList = new ObservableCollection<ProposedMoveViewModel>();
            ApplyThresholdAndRefresh();

            ConfirmCommand = new RelayCommand(param => OnConfirm(), param => CanConfirm());
            CancelCommand = new RelayCommand(param => OnCancel());
            ApproveAllCommand = new RelayCommand(_ => SetAllApprovedOnVisible(true));
            DisapproveAllCommand = new RelayCommand(_ => SetAllApprovedOnVisible(false));
        }

        private void PopulateViewModelList(IEnumerable<ProposedMoveViewModel> movesVMs)
        {
            ProposedMovesList.Clear();
            if (movesVMs != null)
            {
                foreach (var vm in movesVMs)
                {
                    ProposedMovesList.Add(vm);
                }
            }
            OnPropertyChanged(nameof(ProposedMovesList)); // Upewnij się, że UI wie o zmianie
        }

        private void ApplyThresholdAndRefresh()
        {
            SimpleFileLogger.Log($"PreviewChangesViewModel: Refreshing proposed moves with threshold {CurrentSimilarityThreshold:F2}");
            var filteredVMs = _allMovesMasterList
                .Where(vm => vm.Similarity >= CurrentSimilarityThreshold)
                .ToList();
            PopulateViewModelList(filteredVMs);
        }

        private bool CanConfirm()
        {
            // Używamy IsApprovedForMove
            return ProposedMovesList.Any(p => p.IsApprovedForMove);
        }

        public List<Models.ProposedMove> GetApprovedMoves()
        {
            // Używamy IsApprovedForMove
            var approvedRawMoves = ProposedMovesList
                .Where(vm => vm.IsApprovedForMove)
                .Select(vm => vm.OriginalMove)
                .ToList();

            SimpleFileLogger.Log($"PreviewChangesViewModel: GetApprovedMoves przygotowało {approvedRawMoves.Count} ruchów.");
            return approvedRawMoves;
        }

        private void SetAllApprovedOnVisible(bool approved)
        {
            foreach (var vm in ProposedMovesList)
            {
                vm.IsApprovedForMove = approved; // Używamy IsApprovedForMove
            }
        }

        private void OnConfirm()
        {
            SimpleFileLogger.Log("PreviewChangesViewModel: Confirm button clicked. ViewModel przekaże 'true' do CloseAction.");
            CloseAction?.Invoke(true);
        }

        private void OnCancel()
        {
            SimpleFileLogger.Log("PreviewChangesViewModel: Cancel button clicked. ViewModel przekaże 'false' do CloseAction.");
            CloseAction?.Invoke(false);
        }
    }
}