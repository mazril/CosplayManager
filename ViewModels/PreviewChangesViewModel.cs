// Plik: CosplayManager/ViewModels/PreviewChangesViewModel.cs
using CosplayManager.Services;
using CosplayManager.ViewModels.Base;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CosplayManager.Models;

namespace CosplayManager.ViewModels
{
    public class PreviewChangesViewModel : ObservableObject // Zmieniono na public
    {
        // ... (reszta kodu bez zmian, zakładając, że ProposedMoveViewModel jest public)
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
        public ICommand RefreshCommand { get; }

        private readonly List<Models.ProposedMove> _initialProposedMoves;

        public PreviewChangesViewModel(List<Models.ProposedMove> initialProposedMoves, double initialThreshold)
        {
            _initialProposedMoves = initialProposedMoves ?? new List<Models.ProposedMove>();
            _currentSimilarityThreshold = initialThreshold;

            ProposedMovesList = new ObservableCollection<ProposedMoveViewModel>();
            ApplyThresholdAndRefresh();

            ConfirmCommand = new RelayCommand(param => OnConfirm(), param => CanConfirm());
            CancelCommand = new RelayCommand(param => OnCancel());
            RefreshCommand = new RelayCommand(param => ApplyThresholdAndRefresh());
        }

        private void PopulateViewModelList(IEnumerable<Models.ProposedMove> moves)
        {
            ProposedMovesList.Clear();
            if (moves != null)
            {
                foreach (var move in moves)
                {
                    var vm = new ProposedMoveViewModel(move)
                    {
                        IsApprovedForMove = true
                    };
                    ProposedMovesList.Add(vm);
                }
            }
        }

        private void ApplyThresholdAndRefresh()
        {
            SimpleFileLogger.Log($"PreviewChangesViewModel: Refreshing proposed moves with threshold {CurrentSimilarityThreshold:F2}");
            var filteredMoves = _initialProposedMoves
                .Where(move => move.Similarity >= CurrentSimilarityThreshold)
                .ToList();
            PopulateViewModelList(filteredMoves);
        }

        private bool CanConfirm()
        {
            return ProposedMovesList.Any(p => p.IsApprovedForMove);
        }

        public List<Models.ProposedMove> GetApprovedMoves()
        {
            var approvedRawMoves = new List<Models.ProposedMove>();
            if (ProposedMovesList != null)
            {
                foreach (var vm in ProposedMovesList.Where(p => p.IsApprovedForMove))
                {
                    approvedRawMoves.Add(new Models.ProposedMove
                    {
                        SourceImage = vm.SourceImage,
                        TargetImage = vm.TargetImage,
                        ProposedTargetPath = vm.ProposedTargetPath,
                        Similarity = vm.Similarity,
                        Action = vm.Action,
                        TargetCategoryProfileName = vm.TargetCategoryProfileName
                    });
                }
            }
            SimpleFileLogger.Log($"PreviewChangesViewModel: GetApprovedMoves przygotowało {approvedRawMoves.Count} ruchów.");
            return approvedRawMoves;
        }


        public Action<bool?>? CloseAction { get; set; }

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