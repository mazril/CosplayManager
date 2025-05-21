// Plik: Models/ProgressReport.cs
namespace CosplayManager.Models
{
    public class ProgressReport
    {
        public string? OperationName { get; set; }
        public int ProcessedItems { get; set; }
        public int TotalItems { get; set; }
        public string StatusMessage { get; set; } = string.Empty;

        // Zmieniono na publiczną właściwość z setterem
        public bool IsIndeterminate { get; set; }

        // Ta właściwość pozostaje tylko do odczytu, obliczana na podstawie ProcessedItems i TotalItems
        public double Percentage => TotalItems > 0 && !IsIndeterminate ? ((double)ProcessedItems / TotalItems) * 100.0 : 0.0;

        // Konstruktor do łatwiejszego tworzenia instancji
        public ProgressReport(string? operationName = null, string statusMessage = "", int processedItems = 0, int totalItems = 0, bool isIndeterminate = false)
        {
            OperationName = operationName;
            StatusMessage = statusMessage;
            ProcessedItems = processedItems;
            TotalItems = totalItems;
            IsIndeterminate = isIndeterminate;

            // Jeśli TotalItems jest 0 lub mniej, a nie zaznaczono explicite IsIndeterminate, ustaw IsIndeterminate na true
            if (TotalItems <= 0 && !isIndeterminate)
            {
                IsIndeterminate = true;
            }
        }
    }
}