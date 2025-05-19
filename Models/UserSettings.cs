// Plik: Models/UserSettings.cs
using System.Collections.Generic;
using System.ComponentModel;

namespace CosplayManager.Models
{
    public class UserSettings
    {
        public string LibraryRootPath { get; set; } = string.Empty;
        public string SourceFolderNamesInput { get; set; } = "Mix,Mieszane,Unsorted,Downloaded";
        public double SuggestionSimilarityThreshold { get; set; } = 0.85;
        public bool EnableDebugLogging { get; set; } = false; // <-- DODANA WŁAŚCIWOŚĆ
    }
}