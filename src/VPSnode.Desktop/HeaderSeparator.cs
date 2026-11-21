using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VPSnode.Desktop;

public sealed class HeaderSeparator : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return values.Length == 2 && values[0] is int index && values[1] is int count && index >= 0 && index < count - 1
            ? new Thickness(0, 0, 1, 0) : new Thickness(0);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
