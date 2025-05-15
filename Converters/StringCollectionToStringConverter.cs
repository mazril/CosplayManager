using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace CosplayManager.Converters
{
    public class StringCollectionToStringConverter : IValueConverter
    {
        private const char Separator = ';';

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is IEnumerable<string> collection)
            {
                return string.Join(Separator + " ", collection);
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str)
            {
                var collection = new ObservableCollection<string>(
                    str.Split(new[] { Separator }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(s => s.Trim())
                       .Where(s => !string.IsNullOrEmpty(s))
                );
                return collection;
            }
            return new ObservableCollection<string>();
        }
    }
}