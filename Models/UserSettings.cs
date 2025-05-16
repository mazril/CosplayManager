// Plik: Models/UserSettings.cs
using System.Collections.Generic;
using System.ComponentModel;

namespace CosplayManager.Models
{
    public class UserSettings // Zmieniono na public
    {
        public string LibraryRootPath { get; set; } = string.Empty;
        public string SourceFolderNamesInput { get; set; } = "Mix,Mieszane,Unsorted,Downloaded";
        public double SuggestionSimilarityThreshold { get; set; } = 0.85;
    }
}