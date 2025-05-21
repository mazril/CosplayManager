using System;
using System.Globalization;
using System.Windows.Data;

namespace CosplayManager.Converters
{
    public class TypeCheckConverter : IValueConverter
    {
        public static TypeCheckConverter Instance { get; } = new TypeCheckConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter is not Type expectedType)
            {
                return false;
            }
            return expectedType.IsInstanceOfType(value);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}