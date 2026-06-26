using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NtfyDesktop.Features.Rules.Editor;

/// <summary>Visible when the bound value is non-null; Collapsed when null. Use the
/// "Invert" parameter to flip (Visible when null — for empty-state prompts).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null;
        if (parameter is "Invert") hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
