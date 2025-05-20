// Plik: Models/EmbeddingCacheEntry.cs
using System;

namespace CosplayManager.Models
{
    public class EmbeddingCacheEntry
    {
        public float[]? Embedding { get; set; }
        public DateTime LastModifiedUtc { get; set; }
        public long FileSize { get; set; }
    }
}