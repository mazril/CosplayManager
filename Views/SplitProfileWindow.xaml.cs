// Plik: Views/SplitProfileWindow.xaml.cs
using CosplayManager.Services; // Dodaj using, jeśli SimpleFileLogger jest używany
using CosplayManager.ViewModels;
using MahApps.Metro.Controls;
using System; // Dodaj using dla Action
using System.Windows;

namespace CosplayManager.Views
{
    public partial class SplitProfileWindow : MetroWindow
    {
        public SplitProfileWindow()
        {
            InitializeComponent();
            this.Closing += SplitProfileWindow_Closing;
        }

        // Metoda publiczna do ustawienia akcji zamknięcia ViewModelu
        public void SetViewModelCloseAction(SplitProfileViewModel vm)
        {
            if (vm != null)
            {
                vm.CloseAction = (dialogResult) =>
                {
                    SimpleFileLogger.Log($"SplitProfileWindow: Akcja CloseAction z ViewModelu została wywołana z wynikiem: {dialogResult}.");
                    try
                    {
                        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal && this.IsVisible)
                        {
                            Application.Current.Dispatcher.Invoke(() => this.DialogResult = dialogResult);
                        }
                        else if (this.IsLoaded && this.IsVisible)
                        {
                            Application.Current.Dispatcher.Invoke(() => this.Close());
                        }
                    }
                    catch (InvalidOperationException ioe)
                    {
                        SimpleFileLogger.LogError($"SplitProfileWindow: InvalidOperationException podczas ustawiania DialogResult lub zamykania: {ioe.Message}.", ioe);
                        Application.Current.Dispatcher.Invoke(() => { if (this.IsLoaded && this.IsVisible) this.Close(); });
                    }
                };
            }
            else
            {
                SimpleFileLogger.LogWarning("SplitProfileWindow.SetViewModelCloseAction: Przekazany ViewModel był null.");
            }
        }
        private void SplitProfileWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SimpleFileLogger.Log($"SplitProfileWindow.Closing: Zdarzenie zamykania okna. DialogResult: {this.DialogResult}");
            if (this.DataContext is SplitProfileViewModel vm)
            {
                // vm.CloseAction = null; // Rozważ
            }
            this.DataContext = null;
        }
    }
}