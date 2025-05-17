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

        // Ta metoda jest wywoływana z MainWindowViewModel, aby przekazać akcję zamknięcia DO ViewModelu tego okna
        public void SetViewModelCloseAction(PreviewChangesViewModel vm)
        {
            if (vm != null)
            {
                vm.CloseAction = (dialogResult) =>
                {
                    SimpleFileLogger.Log($"PreviewChangesWindow: Akcja CloseAction z ViewModelu została wywołana z wynikiem: {dialogResult}.");
                    try
                    {
                        // Ustaw DialogResult okna, co spowoduje jego zamknięcie
                        // To powinno być bezpieczne do wywołania, nawet jeśli jest już zamykane.
                        if (this.IsVisible) // Tylko jeśli okno jest nadal widoczne/aktywne jako dialog
                        {
                            Application.Current.Dispatcher.Invoke(() => this.DialogResult = dialogResult);
                        }
                    }
                    catch (InvalidOperationException ioe)
                    {
                        SimpleFileLogger.LogError($"PreviewChangesWindow: InvalidOperationException podczas ustawiania DialogResult: {ioe.Message}. Okno mogło nie zostać poprawnie zamknięte.", ioe);
                        // Awaryjne zamknięcie, jeśli ustawienie DialogResult zawiedzie, a okno jest nadal widoczne
                        Application.Current.Dispatcher.Invoke(() => { if (this.IsVisible && this.IsLoaded) this.Close(); });
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
                // Dodatkowe czyszczenie w ViewModelu, jeśli jest potrzebne
                // np. vm.CleanupBeforeClose();
            }
            this.DataContext = null;
            SimpleFileLogger.Log("PreviewChangesWindow.PrepareToCloseWindowResources: DataContext okna ustawiony na null.");
        }

        private void PreviewChangesWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SimpleFileLogger.Log($"PreviewChangesWindow.Closing: Zdarzenie zamykania okna. DialogResult: {this.DialogResult}");
            PrepareToCloseWindowResources();
        }
    }
}