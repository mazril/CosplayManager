using CosplayManager.Models;
using CosplayManager.Services;
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
                if (string.IsNullOrWhiteSpace(profile.CategoryName))
                {
                    SimpleFileLogger.LogWarning($"CategoryProfileToCharacterNameConverter: Otrzymano CategoryProfile z pustą nazwą (CategoryName is null or whitespace). Profil: {profile}");
                    return "[Pusta Nazwa Kategorii]";
                }

                var parts = profile.CategoryName.Split(new[] { " - " }, 2, StringSplitOptions.None);
                string characterName;
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    characterName = parts[1].Trim();
                }
                else
                {
                    characterName = profile.CategoryName.Trim();
                    SimpleFileLogger.Log($"CategoryProfileToCharacterNameConverter: CategoryName '{profile.CategoryName}' nie zawiera ' - ' lub część po myślniku jest pusta. Zwracam całą nazwę: '{characterName}'.");
                }
                return characterName;
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}