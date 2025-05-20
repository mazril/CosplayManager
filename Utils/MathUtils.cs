// Plik: Utils/MathUtils.cs
using CosplayManager.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CosplayManager.Utils
{
    public static class MathUtils // Upewnij się, że klasa jest statyczna, jeśli zawiera tylko metody statyczne
    {
        public static float[]? CalculateAverageEmbedding(List<float[]> embeddings)
        {
            if (embeddings == null || !embeddings.Any() || embeddings.Any(e => e == null))
            {
                // Logowanie, że lista jest pusta lub zawiera null embeddingi
                if (embeddings == null) SimpleFileLogger.Log("MathUtils.CalculateAverageEmbedding: Lista embeddingów jest null.");
                else if (!embeddings.Any()) SimpleFileLogger.Log("MathUtils.CalculateAverageEmbedding: Lista embeddingów jest pusta.");
                else SimpleFileLogger.Log("MathUtils.CalculateAverageEmbedding: Lista embeddingów zawiera null-e.");
                return null;
            }

            // Sprawdzenie, czy wszystkie embeddingi mają tę samą długość
            int? dimension = null;
            foreach (var embedding in embeddings)
            {
                if (embedding == null) // Powtórne sprawdzenie na wszelki wypadek, choć powyżej już jest
                {
                    SimpleFileLogger.LogWarning("MathUtils.CalculateAverageEmbedding: Napotkano null embedding wewnątrz listy. Pomijam.");
                    continue;
                }
                if (dimension == null)
                {
                    dimension = embedding.Length;
                }
                else if (embedding.Length != dimension.Value)
                {
                    SimpleFileLogger.LogError("MathUtils.CalculateAverageEmbedding: Embeddingi mają różne wymiary.", null);
                    throw new ArgumentException("Wszystkie embeddingi muszą mieć ten sam wymiar.");
                }
            }

            if (dimension == null || dimension.Value == 0) // Jeśli wszystkie były null lub puste
            {
                SimpleFileLogger.LogWarning("MathUtils.CalculateAverageEmbedding: Brak poprawnych embeddingów do uśrednienia (wymiar 0 lub wszystkie były null).");
                return null;
            }


            float[] averageEmbedding = new float[dimension.Value];
            int validEmbeddingsCount = 0;

            foreach (var embedding in embeddings)
            {
                if (embedding != null && embedding.Length == dimension.Value) // Dodatkowe sprawdzenie
                {
                    for (int i = 0; i < dimension.Value; i++)
                    {
                        averageEmbedding[i] += embedding[i];
                    }
                    validEmbeddingsCount++;
                }
            }

            if (validEmbeddingsCount == 0)
            {
                SimpleFileLogger.LogWarning("MathUtils.CalculateAverageEmbedding: Brak poprawnych embeddingów po pętli (validEmbeddingsCount = 0).");
                return null; // Lub zwróć pustą tablicę, w zależności od logiki
            }


            for (int i = 0; i < dimension.Value; i++)
            {
                averageEmbedding[i] /= validEmbeddingsCount;
            }

            return averageEmbedding;
        }

        public static double CalculateCosineSimilarity(float[] vecA, float[] vecB)
        {
            if (vecA == null || vecB == null || vecA.Length != vecB.Length || vecA.Length == 0)
            {
                // SimpleFileLogger.LogWarning("CalculateCosineSimilarity: Nieprawidłowe wektory wejściowe.");
                return 0.0; // Lub rzuć wyjątek, w zależności od oczekiwanego zachowania
            }

            double dotProduct = 0.0;
            double normA = 0.0;
            double normB = 0.0;

            for (int i = 0; i < vecA.Length; i++)
            {
                dotProduct += vecA[i] * vecB[i];
                normA += vecA[i] * vecA[i];
                normB += vecB[i] * vecB[i];
            }

            if (normA == 0 || normB == 0) return 0.0; // Unikaj dzielenia przez zero

            return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }
    }
}