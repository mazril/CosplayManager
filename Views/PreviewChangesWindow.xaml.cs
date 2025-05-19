// Plik: Views/PreviewChangesWindow.xaml.cs
using CosplayManager.Services;
using CosplayManager.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;

namespace CosplayManager.Views
{
    public partial class PreviewChangesWindow : MetroWindow
    {
        public PreviewChangesWindow()
        {
            InitializeComponent();
            this.Closing += PreviewChangesWindow_Closing;
        }

        // Metoda publiczna do ustawienia akcji zamykania z MainWindowViewModel
        public void SetViewModelCloseAction(PreviewChangesViewModel vm)
        {
            if (vm != null)
            {
                // Ustawiamy publiczną właściwość CloseAction w ViewModelu
                vm.CloseAction = (dialogResult) =>
                {
                    SimpleFileLogger.Log($"PreviewChangesWindow: Akcja CloseAction z ViewModelu została wywołana z wynikiem: {dialogResult}.");
                    try
                    {
                        // Ustawiamy DialogResult okna, co spowoduje jego zamknięcie
                        // To powinno być bezpieczne do wywołania, nawet jeśli jest już zamykane.
                        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal && this.IsVisible)
                        {
                            Application.Current.Dispatcher.Invoke(() => this.DialogResult = dialogResult);
                        }
                        // Jeśli nie jest modalne, ale wciąż widoczne, spróbuj zamknąć normalnie
                        else if (this.IsLoaded && this.IsVisible)
                        {
                            Application.Current.Dispatcher.Invoke(() => this.Close());
                        }
                    }
                    catch (InvalidOperationException ioe)
                    {
                        SimpleFileLogger.LogError($"PreviewChangesWindow: InvalidOperationException podczas ustawiania DialogResult lub zamykania: {ioe.Message}. Okno mogło nie zostać poprawnie zamknięte.", ioe);
                        // Awaryjne zamknięcie, jeśli coś pójdzie nie tak
                        Application.Current.Dispatcher.Invoke(() => { if (this.IsLoaded && this.IsVisible) this.Close(); });
                    }
                };
            }
            else
            {
                SimpleFileLogger.LogWarning("PreviewChangesWindow.SetViewModelCloseAction: Przekazany ViewModel był null.");
            }
        }

        private void PrepareToCloseWindowResources()
        {
            SimpleFileLogger.Log("PreviewChangesWindow.PrepareToCloseWindowResources: Rozpoczęto czyszczenie zasobów okna.");
            if (this.ProposedMovesListView is ItemsControl itemsControl)
            {
                itemsControl.ItemsSource = null;
                SimpleFileLogger.Log("PreviewChangesWindow.PrepareToCloseWindowResources: ItemsSource ProposedMovesListView ustawiony na null.");
            }

            if (this.DataContext is PreviewChangesViewModel vm)
            {
                // vm.CloseAction = null; // Opcjonalnie, aby zerwać referencję
            }
            this.DataContext = null;
            SimpleFileLogger.Log("PreviewChangesWindow.PrepareToCloseWindowResources: DataContext okna ustawiony na null.");
        }

        private void PreviewChangesWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SimpleFileLogger.Log($"PreviewChangesWindow.Closing: Zdarzenie zamykania okna. DialogResult przed PrepareToClose: {this.DialogResult}");
            PrepareToCloseWindowResources();
            SimpleFileLogger.Log($"PreviewChangesWindow.Closing: Zdarzenie zamykania okna zakończone. DialogResult po PrepareToClose: {this.DialogResult}");
        }
    }
}