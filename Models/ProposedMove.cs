// Plik: Models/ProposedMove.cs
using System.Text.Json.Serialization;

namespace CosplayManager.Models
{
    public enum ProposedMoveActionType
    {
        CopyNew,
        OverwriteExisting,
        KeepExistingDeleteSource,
        ConflictKeepBoth,
        NoAction
    }

    public class ProposedMove
    {
        public ImageFileEntry SourceImage { get; set; }
        public ImageFileEntry? TargetImageDisplay { get; set; } // Używamy tej nazwy
        public string ProposedTargetPath { get; set; }
        public double Similarity { get; set; }
        public string TargetCategoryProfileName { get; set; }
        public ProposedMoveActionType Action { get; set; }

        [JsonIgnore]
        public float[]? SourceImageEmbedding { get; }

#pragma warning disable CS8618
        public ProposedMove() { }
#pragma warning restore CS8618

        public ProposedMove(ImageFileEntry sourceImage, ImageFileEntry? targetImageDisplay, string proposedTargetPath, double similarity, string targetCategoryProfileName, ProposedMoveActionType action, float[]? sourceEmbedding = null)
        {
            SourceImage = sourceImage;
            TargetImageDisplay = targetImageDisplay; // Używamy tej nazwy
            ProposedTargetPath = proposedTargetPath;
            Similarity = similarity;
            TargetCategoryProfileName = targetCategoryProfileName;
            Action = action;
            SourceImageEmbedding = sourceEmbedding;
        }
    }
}