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
        public bool EnableDebugLogging { get; set; } = false;
        public bool AutoLoadThumbnailsInEditor { get; set; } = true; // <<< NOWA WŁAŚCIWOŚĆ (domyślnie włączone)

        // USUNIĘTE WŁAŚCIWOŚCI:
        // public string PythonExecutablePath { get; set; } = string.Empty;
        // public string ClipServerScriptPath { get; set; } = string.Empty;
    }
}