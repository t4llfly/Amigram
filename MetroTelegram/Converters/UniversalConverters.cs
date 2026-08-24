using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MetroTelegram.Converters
{
    public class MessageDateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is DateTime))
            {
                return string.Empty;
            }

            DateTime date = (DateTime)value;
            DateTime now = DateTime.Now;

            if (date.Date == now.Date)
            {
                return date.ToString("HH:mm");
            }
            if (date.Date == now.Date.AddDays(-1))
            {
                return "вчера";
            }
            if (date.Year == now.Year)
            {
                return date.ToString("d MMM", culture);
            }
            return date.ToString("dd.MM.yy");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class UnreadCountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int)
            {
                int count = (int)value;
                return count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}