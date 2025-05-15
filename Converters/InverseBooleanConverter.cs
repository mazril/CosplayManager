// Plik: Converters/InverseBooleanConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;

namespace CosplayManager.Converters // Upewnij się, że ta przestrzeń nazw jest poprawna
{
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }
    }
}
