using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Otopark.Client.Converters;

public sealed class BoolToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x2E, 0xCC, 0x71));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xE7, 0x4C, 0x3C));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? SuccessBrush : ErrorBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
