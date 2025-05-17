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
        public ImageFileEntry? TargetImage { get; set; } // Używamy TargetImage zgodnie z Twoim plikiem
        public string ProposedTargetPath { get; set; }
        public double Similarity { get; set; }
        public string TargetCategoryProfileName { get; set; }
        public ProposedMoveActionType Action { get; set; }

        [JsonIgnore]
        public float[]? SourceImageEmbedding { get; }

#pragma warning disable CS8618 
        public ProposedMove() { }
#pragma warning restore CS8618

        public ProposedMove(ImageFileEntry sourceImage, ImageFileEntry? targetImage, string proposedTargetPath, double similarity, string targetCategoryProfileName, ProposedMoveActionType action, float[]? sourceEmbedding = null)
        {
            SourceImage = sourceImage;
            TargetImage = targetImage; // Używamy TargetImage
            ProposedTargetPath = proposedTargetPath;
            Similarity = similarity;
            TargetCategoryProfileName = targetCategoryProfileName;
            Action = action;
            SourceImageEmbedding = sourceEmbedding;
        }
    }
}