namespace CosplayManager.Models
{
    public class ProgressReport
    {
        public string? OperationName { get; set; }
        public int ProcessedItems { get; set; }
        public int TotalItems { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public bool IsIndeterminate { get; set; }
        public double Percentage => TotalItems > 0 && !IsIndeterminate ? ((double)ProcessedItems / TotalItems) * 100.0 : 0.0;

        public ProgressReport(string? operationName = null, string statusMessage = "", int processedItems = 0, int totalItems = 0, bool isIndeterminate = false)
        {
            OperationName = operationName;
            StatusMessage = statusMessage;
            ProcessedItems = processedItems;
            TotalItems = totalItems;
            IsIndeterminate = isIndeterminate;

            if (TotalItems <= 0 && !isIndeterminate)
            {
                IsIndeterminate = true;
            }
        }
    }
}