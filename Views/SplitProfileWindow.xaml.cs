// Plik: Views/SplitProfileWindow.xaml.cs
using CosplayManager.ViewModels;
using MahApps.Metro.Controls; // Jeśli używasz MetroWindow
using System.Windows;

namespace CosplayManager.Views
{
    public partial class SplitProfileWindow : MetroWindow // lub Window, jeśli nie używasz MahApps
    {
        public SplitProfileWindow()
        {
            InitializeComponent();
        }

        // Metoda do ustawienia akcji zamknięcia z ViewModelu
        public void SetCloseAction(SplitProfileViewModel vm)
        {
            if (vm != null)
            {
                vm.CloseAction = (result) =>
                {
                    try
                    {
                        // Ustaw DialogResult tylko jeśli okno jest modalne i widoczne
                        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal && this.IsVisible)
                        {
                            this.DialogResult = result;
                        }
                    }
                    catch (System.InvalidOperationException)
                    {
                        // Może się zdarzyć, jeśli okno jest już zamykane inaczej
                    }

                    // Standardowe zamknięcie, jeśli nie jest już zamknięte przez DialogResult
                    if (this.IsLoaded && PresentationSource.FromVisual(this) != null && this.IsVisible)
                    {
                        this.Close();
                    }
                };
            }
        }
    }
}