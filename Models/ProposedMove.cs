// Plik: Models/ProposedMove.cs
namespace CosplayManager.Models
{
    public enum ProposedMoveActionType // Zmieniono na public
    {
        CopyNew,
        OverwriteExisting,
        KeepExistingDeleteSource,
        ConflictKeepBoth
    }

    public class ProposedMove // Zmieniono na public
    {
        public ImageFileEntry SourceImage { get; set; }
        public ImageFileEntry? TargetImage { get; set; }
        public string ProposedTargetPath { get; set; }
        public double Similarity { get; set; }
        public string TargetCategoryProfileName { get; set; }
        public ProposedMoveActionType Action { get; set; }

#pragma warning disable CS8618 
        public ProposedMove() { }
#pragma warning restore CS8618

        public ProposedMove(ImageFileEntry sourceImage, ImageFileEntry? targetImage, string proposedTargetPath, double similarity, string targetCategoryProfileName, ProposedMoveActionType action)
        {
            SourceImage = sourceImage;
            TargetImage = targetImage;
            ProposedTargetPath = proposedTargetPath;
            Similarity = similarity;
            TargetCategoryProfileName = targetCategoryProfileName;
            Action = action;
        }
    }
}