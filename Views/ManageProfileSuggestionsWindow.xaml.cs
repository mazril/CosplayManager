using CosplayManager.ViewModels;
using MahApps.Metro.Controls;
using System; // Dla Action
using System.Windows;

namespace CosplayManager.Views
{
    public partial class ManageProfileSuggestionsWindow : MetroWindow
    {
        public ManageProfileSuggestionsWindow()
        {
            InitializeComponent();
        }

        // Metoda publiczna do ustawienia akcji zamknięcia ViewModelu
        public void SetViewModelCloseAction(ManageProfileSuggestionsViewModel vm)
        {
            if (vm != null)
            {
                vm.CloseAction = (dialogResult) =>
                {
                    // Logowanie, jeśli potrzebne
                    // SimpleFileLogger.Log($"ManageProfileSuggestionsWindow: Akcja CloseAction z ViewModelu została wywołana z wynikiem: {dialogResult}.");
                    try
                    {
                        // Ustaw DialogResult tylko jeśli okno jest modalne i widoczne
                        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal && this.IsVisible)
                        {
                            Application.Current.Dispatcher.Invoke(() => this.DialogResult = dialogResult);
                        }
                        // Jeśli nie jest modalne, ale wciąż widoczne i załadowane, zamknij normalnie
                        // Ta część jest ważna, jeśli okno nie jest ShowDialog()
                        else if (this.IsLoaded && this.IsVisible)
                        {
                            Application.Current.Dispatcher.Invoke(() => this.Close());
                        }
                    }
                    catch (InvalidOperationException ioe)
                    {
                        // Log error
                        // SimpleFileLogger.LogError($"ManageProfileSuggestionsWindow: InvalidOperationException podczas ustawiania DialogResult lub zamykania: {ioe.Message}.", ioe);
                        // Awaryjne zamknięcie
                        Application.Current.Dispatcher.Invoke(() => { if (this.IsLoaded && this.IsVisible) this.Close(); });
                    }
                };
            }
        }
    }
}