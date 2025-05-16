// Plik: Converters/CategoryProfileToCharacterNameConverter.cs
using CosplayManager.Models;
using System;
using System.Globalization;
using System.Windows.Data;

namespace CosplayManager.Converters
{
    public class CategoryProfileToCharacterNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is CategoryProfile profile && !string.IsNullOrWhiteSpace(profile.CategoryName))
            {
                var parts = profile.CategoryName.Split(new[] { " - " }, 2, StringSplitOptions.None); // Split max 2 części
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    return parts[1].Trim(); // Zwróć część po pierwszym " - "
                }
                return profile.CategoryName.Trim(); // Jeśli nie ma " - " lub część po jest pusta, zwróć całą nazwę
            }
            return string.Empty; // Lub "Nieznana Kategoria"
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}