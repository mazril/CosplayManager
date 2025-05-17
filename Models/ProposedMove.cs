// Plik: Models/ProposedMove.cs
using System.Text.Json.Serialization; // Dla JsonIgnore

namespace CosplayManager.Models
{
    public enum ProposedMoveActionType
    {
        CopyNew,
        OverwriteExisting,
        KeepExistingDeleteSource,
        ConflictKeepBoth,
        NoAction // Dodano, aby reprezentować brak akcji lub sytuację, gdy nic nie trzeba robić
    }

    public class ProposedMove
    {
        public ImageFileEntry SourceImage { get; set; }
        public ImageFileEntry? TargetImageDisplay { get; set; } // Zmieniono nazwę dla jasności, że to do wyświetlania
        public string ProposedTargetPath { get; set; }
        public double Similarity { get; set; }
        public string TargetCategoryProfileName { get; set; }
        public ProposedMoveActionType Action { get; set; }

        [JsonIgnore] // Embedding nie musi być serializowany, jeśli ProposedMove jest gdzieś zapisywany
        public float[]? SourceImageEmbedding { get; } // Embedding obrazu źródłowego

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public ProposedMove() { } // Konstruktor bezparametrowy dla przypadków, gdy jest potrzebny (np. deserializacja, choć tu niezalecane)
#pragma warning restore CS8618

        // Główny konstruktor
        public ProposedMove(
            ImageFileEntry sourceImage,
            ImageFileEntry? targetImageDisplay, // Zmieniono nazwę parametru
            string proposedTargetPath,
            double similarity,
            string targetCategoryProfileName,
            ProposedMoveActionType action,
            float[]? sourceEmbedding = null) // Dodano opcjonalny parametr embeddingu
        {
            SourceImage = sourceImage;
            TargetImageDisplay = targetImageDisplay;
            ProposedTargetPath = proposedTargetPath;
            Similarity = similarity;
            TargetCategoryProfileName = targetCategoryProfileName;
            Action = action;
            SourceImageEmbedding = sourceEmbedding; // Przypisanie embeddingu
        }
    }
}