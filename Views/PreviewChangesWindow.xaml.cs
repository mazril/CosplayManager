// Plik: CosplayManager/Views/PreviewChangesWindow.xaml.cs
using CosplayManager.Services;
using CosplayManager.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls; // <<< UPEWNIJ SIĘ, ŻE TA LINIA JEST OBECNA

namespace CosplayManager.Views
{
    public partial class PreviewChangesWindow : MetroWindow // <<< ZMIENIONO KLASĘ BAZOWĄ
    {
        public PreviewChangesWindow()
        {
            InitializeComponent();
        }

        public void PrepareToClose()
        {
            SimpleFileLogger.Log("PreviewChangesWindow.PrepareToClose: Rozpoczęto czyszczenie przed zamknięciem.");

            // Bezpośrednie odwołanie do kontrolki ListView z x:Name
            if (this.ProposedMovesListView is ItemsControl itemsControl)
            {
                SimpleFileLogger.Log("PreviewChangesWindow.PrepareToClose: Znaleziono ProposedMovesListView. Odłączanie ItemsSource.");
                itemsControl.ItemsSource = null;
                // itemsControl.Items.Clear(); // Opcjonalnie, ale odłączenie ItemsSource powinno wystarczyć
            }
            else
            {
                SimpleFileLogger.Log("PreviewChangesWindow.PrepareToClose: NIE znaleziono ProposedMovesListView o nazwie 'ProposedMovesListView'.");
            }

            var currentDataContext = this.DataContext;
            if (currentDataContext != null)
            {
                this.DataContext = null; // Odłącz DataContext
                SimpleFileLogger.Log("PreviewChangesWindow.PrepareToClose: DataContext okna ustawiony na null.");
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            SimpleFileLogger.Log("PreviewChangesWindow.PrepareToClose: Wywołano GC.Collect() i WaitForPendingFinalizers().");
        }

        public void SetCloseAction(PreviewChangesViewModel vm)
        {
            if (vm != null)
            {
                vm.CloseAction = (result) =>
                {
                    SimpleFileLogger.Log($"PreviewChangesWindow.CloseAction (z VM): Otrzymano result: {result}. Wywołanie PrepareToClose().");
                    PrepareToClose();

                    try
                    {
                        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal && this.IsVisible)
                        {
                            this.DialogResult = result;
                        }
                    }
                    catch (InvalidOperationException ioe)
                    {
                        SimpleFileLogger.Log($"PreviewChangesWindow.CloseAction (z VM): InvalidOperationException podczas ustawiania DialogResult: {ioe.Message}");
                    }

                    // Sprawdź, czy okno nadal jest "aktywne" w drzewie wizualnym przed próbą zamknięcia
                    if (this.IsLoaded && PresentationSource.FromVisual(this) != null)
                    {
                        if (this.IsVisible) // Dodatkowe sprawdzenie, czy jest widoczne
                        {
                            SimpleFileLogger.Log("PreviewChangesWindow.CloseAction (z VM): Zamykanie okna.");
                            this.Close();
                        }
                        else
                        {
                            SimpleFileLogger.Log("PreviewChangesWindow.CloseAction (z VM): Okno nie jest widoczne, pomijanie Close().");
                        }
                    }
                    else
                    {
                        SimpleFileLogger.Log("PreviewChangesWindow.CloseAction (z VM): Okno nie jest już załadowane/częścią drzewa wizualnego, pomijanie Close().");
                    }
                };
            }
        }
    }
}