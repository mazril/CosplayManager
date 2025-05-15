// Plik: Converters/CategoryProfileToCharacterNameConverter.cs
using CosplayManager.Models; // Dla CategoryProfile
using System;
using System.Globalization;
using System.Windows.Data;

namespace CosplayManager.Converters
{
    public class CategoryProfileToCharacterNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is CategoryProfile profile)
            {
                if (string.IsNullOrWhiteSpace(profile.CategoryName)) return "N/A";
                var parts = profile.CategoryName.Split(new[] { " - " }, System.StringSplitOptions.None);
                // Zwróć część po " - " jeśli istnieje, w przeciwnym razie całą nazwę (lub tylko pierwszą część, jeśli to modelka bez postaci)
                return parts.Length > 1 ? parts[1].Trim() : (parts.Length == 1 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0].Trim() : profile.CategoryName);
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}