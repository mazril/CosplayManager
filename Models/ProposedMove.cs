// Plik: Models/ProposedMove.cs
namespace CosplayManager.Models
{
    public enum ProposedMoveActionType
    {
        CopyNew,             // Kopiuj jako nowy plik (bo nie ma konfliktu lub znaczącego duplikatu)
        OverwriteExisting,   // Nadpisz istniejący plik, bo źródło jest lepsze
        KeepExistingDeleteSource, // Zachowaj istniejący plik (jest lepszy/taki sam), usuń źródło
        ConflictKeepBoth    // Konflikt, gdzie nie można automatycznie zdecydować - zapisz źródło obok
    }

    public class ProposedMove
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