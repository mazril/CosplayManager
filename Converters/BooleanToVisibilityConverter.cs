// Plik: Converters/BooleanToVisibilityConverter.cs
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CosplayManager.Converters
{
    public class BooleanToVisibilityConverter : IValueConverter // Upewnij się, że jest public
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = false;
            if (value is bool b)
            {
                boolValue = b;
            }

            if (parameter != null && parameter.ToString().Equals("Inverse", StringComparison.OrdinalIgnoreCase))
            {
                boolValue = !boolValue;
            }

            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                bool boolValue = visibility == Visibility.Visible;
                if (parameter != null && parameter.ToString().Equals("Inverse", StringComparison.OrdinalIgnoreCase))
                {
                    boolValue = !boolValue;
                }
                return boolValue;
            }
            return false;
        }
    }
}