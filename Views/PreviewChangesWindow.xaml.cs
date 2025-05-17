// Plik: Views/PreviewChangesWindow.xaml.cs
using CosplayManager.Services;
using CosplayManager.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls; // Dla ItemsControl
using MahApps.Metro.Controls;

namespace CosplayManager.Views
{
    public partial class PreviewChangesWindow : MetroWindow
    {
        public PreviewChangesWindow()
        {
            InitializeComponent();
            this.Closing += PreviewChangesWindow_Closing; // Dodajemy obsługę zdarzenia Closing
        }

        // Metoda do ustawienia akcji zamykania z MainWindowViewModel
        // Ta metoda powinna być wywoływana z MainWindowViewModel PO utworzeniu instancji PreviewChangesWindow
        // a PRZED jej wyświetleniem (ShowDialog).
        public void SetViewModelCloseAction(PreviewChangesViewModel vm)
        {
            if (vm != null)
            {
                // Ustawiamy właściwość CloseAction w ViewModelu, aby ViewModel mógł wywołać zamknięcie okna
                vm.CloseAction = (dialogResult) =>
                {
                    SimpleFileLogger.Log($"PreviewChangesWindow: CloseAction z ViewModelu wywołane z wynikiem: {dialogResult}.");
                    try
                    {
                        // Ustawiamy DialogResult okna, co spowoduje jego zamknięcie
                        // Należy to robić w wątku UI, jeśli ViewModel wywołuje to z innego wątku
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            this.DialogResult = dialogResult;
                            // Samo ustawienie DialogResult powinno zamknąć okno dialogowe.
                            // Wywołanie this.Close() tutaj może być zbędne lub nawet powodować problemy, jeśli DialogResult już to robi.
                        });
                    }
                    catch (InvalidOperationException ioe)
                    {
                        SimpleFileLogger.LogError($"PreviewChangesWindow: InvalidOperationException podczas ustawiania DialogResult: {ioe.Message}. Okno może nie zostać poprawnie zamknięte.", ioe);
                        // Awaryjne zamknięcie, jeśli ustawienie DialogResult zawiedzie
                        Application.Current.Dispatcher.Invoke(() => { if (this.IsVisible) this.Close(); });
                    }
                };
            }
        }

        // Metoda do czyszczenia zasobów przed faktycznym zamknięciem okna
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
                // Jeśli ViewModel ma jakieś zasoby do zwolnienia, można to zrobić tutaj
                // np. vm.Cleanup(); 
                // Na razie ViewModel sam zarządza swoją logiką
            }
            this.DataContext = null;
            SimpleFileLogger.Log("PreviewChangesWindow.PrepareToCloseWindowResources: DataContext okna ustawiony na null.");

            // Agresywne czyszczenie pamięci - używać ostrożnie i tylko jeśli są problemy z pamięcią
            // GC.Collect();
            // GC.WaitForPendingFinalizers();
            // SimpleFileLogger.Log("PreviewChangesWindow.PrepareToCloseWindowResources: Wywołano GC.Collect().");
        }

        // Obsługa zdarzenia Closing okna
        private void PreviewChangesWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SimpleFileLogger.Log("PreviewChangesWindow.Closing: Zdarzenie zamykania okna.");
            PrepareToCloseWindowResources(); // Wyczyść zasoby
        }
    }
}