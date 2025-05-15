// Plik: ViewModels/SplitProfileViewModel.cs
using CosplayManager.Models;
using CosplayManager.ViewModels.Base;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input; // Dla ICommand

namespace CosplayManager.ViewModels
{
    // Pomocnicza klasa do wyświetlania obrazu w oknie podziału
    public class SplittableImageViewModel : ObservableObject
    {
        private ImageFileEntry _originalImageEntry;
        public ImageFileEntry OriginalImageEntry
        {
            get => _originalImageEntry;
            set => SetProperty(ref _originalImageEntry, value);
        }

        private string _thumbnailPath; // Można by użyć BitmapImage jak w ProposedMoveViewModel
        public string ThumbnailPath
        {
            get => _thumbnailPath;
            set => SetProperty(ref _thumbnailPath, value);
        }

        public string FileName => OriginalImageEntry?.FileName ?? "Brak nazwy";

        public SplittableImageViewModel(ImageFileEntry imageEntry)
        {
            OriginalImageEntry = imageEntry;
            // Na razie uproszczone - bezpośrednia ścieżka. W bardziej zaawansowanej wersji generowalibyśmy miniatury.
            ThumbnailPath = imageEntry.FilePath;
        }
    }

    public class SplitProfileViewModel : ObservableObject
    {
        private CategoryProfile _originalProfile;
        public CategoryProfile OriginalProfile
        {
            get => _originalProfile;
            set => SetProperty(ref _originalProfile, value);
        }

        public string OriginalProfileName => OriginalProfile?.CategoryName ?? "Nieznany profil";

        private ObservableCollection<SplittableImageViewModel> _group1Images;
        public ObservableCollection<SplittableImageViewModel> Group1Images
        {
            get => _group1Images;
            set => SetProperty(ref _group1Images, value);
        }

        private ObservableCollection<SplittableImageViewModel> _group2Images;
        public ObservableCollection<SplittableImageViewModel> Group2Images
        {
            get => _group2Images;
            set => SetProperty(ref _group2Images, value);
        }

        private string _newProfile1Name;
        public string NewProfile1Name
        {
            get => _newProfile1Name;
            set => SetProperty(ref _newProfile1Name, value);
        }

        private string _newProfile2Name;
        public string NewProfile2Name
        {
            get => _newProfile2Name;
            set => SetProperty(ref _newProfile2Name, value);
        }

        public ICommand ConfirmSplitCommand { get; }
        public ICommand CancelSplitCommand { get; }

        public Action<bool?>? CloseAction { get; set; }


        public SplitProfileViewModel(CategoryProfile originalProfile, List<ImageFileEntry> initialGroup1Images, List<ImageFileEntry> initialGroup2Images, string suggestedName1, string suggestedName2)
        {
            _originalProfile = originalProfile;
            Group1Images = new ObservableCollection<SplittableImageViewModel>(initialGroup1Images.Select(img => new SplittableImageViewModel(img)));
            Group2Images = new ObservableCollection<SplittableImageViewModel>(initialGroup2Images.Select(img => new SplittableImageViewModel(img)));
            NewProfile1Name = suggestedName1;
            NewProfile2Name = suggestedName2;

            ConfirmSplitCommand = new RelayCommand(param => OnConfirmSplit(), param => CanConfirmSplit());
            CancelSplitCommand = new RelayCommand(param => OnCancelSplit());
        }

        private bool CanConfirmSplit()
        {
            // Prosta walidacja: obie grupy muszą mieć obrazy i nazwy nie mogą być puste
            return Group1Images.Any() && Group2Images.Any() &&
                   !string.IsNullOrWhiteSpace(NewProfile1Name) &&
                   !string.IsNullOrWhiteSpace(NewProfile2Name) &&
                   NewProfile1Name != NewProfile2Name; // Nazwy nowych profili muszą być różne
        }

        private void OnConfirmSplit()
        {
            // Tutaj docelowo będzie logika zwracająca dane do MainWindowViewModel
            // Na razie tylko zamykamy okno z wynikiem true
            CloseAction?.Invoke(true);
        }

        private void OnCancelSplit()
        {
            CloseAction?.Invoke(false);
        }

        // TODO: Dodać metody do przenoszenia obrazów między Group1Images i Group2Images,
        // które będą wywoływane np. przez drag&drop lub przyciski w UI.
        // Te metody powinny również wywoływać (ConfirmSplitCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}