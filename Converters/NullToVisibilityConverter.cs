using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kumo.Converters;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNotNull = value is not null;
        var wantsNotNull = string.Equals(parameter?.ToString(), "NotNull", StringComparison.OrdinalIgnoreCase);
        return (wantsNotNull ? isNotNull : !isNotNull) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
