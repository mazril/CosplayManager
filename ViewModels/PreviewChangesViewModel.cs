// Plik: CosplayManager/ViewModels/PreviewChangesViewModel.cs
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
        public ICommand RefreshCommand { get; }

        private readonly List<Models.ProposedMove> _initialProposedMoves;
        // Pole _approvedMovesForProcessing nie jest już tutaj potrzebne,
        // GetApprovedMoves będzie budować listę dynamicznie z ProposedMovesList.
        // Jednakże, skoro PrepareToClose() w code-behind czyści ItemsSource i DataContext,
        // GetApprovedMoves musi być wywołane w MainWindowViewModel PO ShowDialog()
        // ale PRZED tym jak UI zostanie zniszczone. To jest delikatne.

        // Bezpieczniejsze podejście: GetApprovedMoves buduje listę z ProposedMovesList
        // tak jak było, a MainWindowViewModel wywołuje je PO zamknięciu okna.
        // Czyszczenie UI w PrepareToClose() powinno wystarczyć do zwolnienia zasobów.

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
                    var vm = new ProposedMoveViewModel(move.SourceImage, move.TargetImage, move.ProposedTargetPath, move.Similarity)
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
            // Ta metoda jest wywoływana przez MainWindowViewModel PO tym, jak ShowDialog() się zakończy.
            // W tym momencie UI okna PreviewChangesWindow mogło już zostać zniszczone lub jest w trakcie.
            // Ale ViewModel (ten obiekt) nadal powinien istnieć i mieć dane.
            var approvedRawMoves = new List<Models.ProposedMove>();
            if (ProposedMovesList != null) // Dodatkowe sprawdzenie
            {
                foreach (var vm in ProposedMovesList.Where(p => p.IsApprovedForMove))
                {
                    approvedRawMoves.Add(new Models.ProposedMove
                    {
                        SourceImage = vm.SourceImage,
                        TargetImage = vm.TargetImage,
                        ProposedTargetPath = vm.ProposedTargetPath,
                        Similarity = vm.Similarity
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
            // Nie ma potrzeby czyszczenia ProposedMovesList tutaj. To zrobi PrepareToClose() w oknie.
            // Metoda GetApprovedMoves zostanie wywołana przez MainWindowViewModel po zamknięciu okna.
            CloseAction?.Invoke(true);
        }

        private void OnCancel()
        {
            SimpleFileLogger.Log("PreviewChangesViewModel: Cancel button clicked. ViewModel przekaże 'false' do CloseAction.");
            CloseAction?.Invoke(false);
        }
    }
}