// Plik: Converters/SplitSuggestionToColorConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CosplayManager.Converters
{
    public class SplitSuggestionToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool hasSuggestion && hasSuggestion)
            {
                return Brushes.Purple; // Kolor dla sugestii podziału
            }
            return Brushes.Black; // Domyślny kolor
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}