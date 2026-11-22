using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NetpulseGUI.Localization;

/// <summary>Вспомогательные конвертеры, доступные через x:Static.</summary>
public static class VisibilityHelpers
{
    public static IMultiValueConverter BothTrue { get; } = new AllTrueConverter();

    private sealed class AllTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
