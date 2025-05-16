// Plik: ViewModels/ModelDisplayViewModel.cs
using CosplayManager.Models;
using CosplayManager.ViewModels.Base;
using System.Collections.ObjectModel;
using System.Linq;

namespace CosplayManager.ViewModels
{
    public class ModelDisplayViewModel : ObservableObject // Zmieniono na public
    {
        private string _modelName = string.Empty;
        public string ModelName
        {
            get => _modelName;
            set => SetProperty(ref _modelName, value);
        }

        private ObservableCollection<CategoryProfile> _characterProfiles;
        public ObservableCollection<CategoryProfile> CharacterProfiles
        {
            get => _characterProfiles;
            set
            {
                if (SetProperty(ref _characterProfiles, value))
                {
                    OnPropertyChanged(nameof(HasCharacterProfiles));
                }
            }
        }

        public bool HasCharacterProfiles => CharacterProfiles != null && CharacterProfiles.Any();

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        private int _pendingSuggestionsCount;
        public int PendingSuggestionsCount
        {
            get => _pendingSuggestionsCount;
            set
            {
                if (SetProperty(ref _pendingSuggestionsCount, value))
                {
                    OnPropertyChanged(nameof(HasPendingSuggestions));
                }
            }
        }

        public bool HasPendingSuggestions => PendingSuggestionsCount > 0;

        public ModelDisplayViewModel(string modelName)
        {
            ModelName = modelName;
            CharacterProfiles = new ObservableCollection<CategoryProfile>();
            IsExpanded = false;
            PendingSuggestionsCount = 0;
        }

        public void AddCharacterProfile(CategoryProfile profile)
        {
            if (profile != null && !CharacterProfiles.Any(p => p.CategoryName.Equals(profile.CategoryName)))
            {
                CharacterProfiles.Add(profile);
                var sortedList = CharacterProfiles.OrderBy(p => GetCharacterNameFromCategoryProfile(p)).ToList();
                CharacterProfiles.Clear();
                foreach (var item in sortedList)
                {
                    CharacterProfiles.Add(item);
                }
                OnPropertyChanged(nameof(HasCharacterProfiles));
            }
        }

        public string GetCharacterNameFromCategoryProfile(CategoryProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.CategoryName)) return "N/A";
            var parts = profile.CategoryName.Split(new[] { " - " }, System.StringSplitOptions.None);
            return parts.Length > 1 ? parts[1].Trim() : profile.CategoryName;
        }
    }
}