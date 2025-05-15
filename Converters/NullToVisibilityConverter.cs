using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CosplayManager.Converters // Upewnij się, że przestrzeň nazw jest poprawna
{
    public class NullToVisibilityConverter : IValueConverter // Upewnij się, że klasa jest publiczna
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}