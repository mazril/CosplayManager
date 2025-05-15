// Plik: Utils/MathUtils.cs (lub w innym odpowiednim miejscu, np. Services)
// Upewnij się, że przestrzeń nazw jest zgodna z Twoim projektem
using System;

namespace CosplayManager.Utils // Przykładowa przestrzeń nazw
{
    public static class MathUtils
    {
        public static double CalculateCosineSimilarity(float[] vectorA, float[] vectorB)
        {
            if (vectorA == null || vectorB == null)
            {
                throw new ArgumentNullException(vectorA == null ? nameof(vectorA) : nameof(vectorB), "Wektory nie mogą być null.");
            }

            if (vectorA.Length != vectorB.Length)
            {
                throw new ArgumentException("Wektory muszą mieć taką samą długość.");
            }

            if (vectorA.Length == 0)
            {
                // Podobieństwo dwóch pustych wektorów można zdefiniować różnie.
                // 1.0 jeśli są "identycznie puste", 0.0 jeśli nie ma informacji do porównania.
                // NaN lub wyjątek, jeśli to sytuacja błędna.
                // Dla bezpieczeństwa, rzućmy wyjątek lub zwróćmy 0, jeśli to nie powinno się zdarzyć.
                return 0.0; // Lub throw new ArgumentException("Wektory nie mogą być puste.");
            }

            double dotProduct = 0.0;
            double magnitudeA = 0.0;
            double magnitudeB = 0.0;

            for (int i = 0; i < vectorA.Length; i++)
            {
                dotProduct += vectorA[i] * vectorB[i];
                magnitudeA += vectorA[i] * vectorA[i];
                magnitudeB += vectorB[i] * vectorB[i];
            }

            magnitudeA = Math.Sqrt(magnitudeA);
            magnitudeB = Math.Sqrt(magnitudeB);

            if (magnitudeA == 0.0 || magnitudeB == 0.0)
            {
                // Jeśli jeden z wektorów ma zerową długość, podobieństwo kosinusowe jest niezdefiniowane
                // lub można przyjąć 0, jeśli wektory są różne (jeden zerowy, drugi nie).
                // Jeśli oba są zerowe, można by zwrócić 1.0.
                // W przypadku wektorów cech, zerowa magnituda jest rzadka.
                // Jeśli magnitudeA i magnitudeB są oba 0, a dotProduct też jest 0, to 0/0 -> NaN.
                // Jeśli jeden jest 0, a drugi nie, to dotProduct/0 -> Infinity/NaN.
                // Bezpieczniej zwrócić 0, jeśli którykolwiek jest zerowy, chyba że oba są zerowe.
                if (magnitudeA == magnitudeB) // Oba są 0
                    return 1.0; // Można argumentować, że są idealnie podobne (oba są "niczym") lub 0.0
                else
                    return 0.0; // Jeden jest zerowy, drugi nie - brak podobieństwa.
            }

            return dotProduct / (magnitudeA * magnitudeB);
        }
    }
}