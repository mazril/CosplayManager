// Plik: Models/UserSettings.cs
using System.Collections.Generic; // Jeśli SourceFolderNames będzie listą
using System.ComponentModel; // Dla INotifyPropertyChanged, jeśli model ma być dynamiczny

namespace CosplayManager.Models
{
    // Można dodać INotifyPropertyChanged, jeśli chcemy dynamicznie reagować na zmiany
    // w samym obiekcie ustawień, ale zazwyczaj wystarczy odczyt/zapis przy starcie/zamknięciu.
    public class UserSettings
    {
        public string LibraryRootPath { get; set; } = string.Empty;
        public string SourceFolderNamesInput { get; set; } = "Mix,Mieszane,Unsorted,Downloaded"; // Domyślne wartości
        public double SuggestionSimilarityThreshold { get; set; } = 0.85;

        // Można dodać inne ustawienia w przyszłości, np.:
        // public string LastUsedMixedFolderPath { get; set; } = string.Empty;
        // public bool AutoLoadLastProfiles { get; set; } = true;
    }
}