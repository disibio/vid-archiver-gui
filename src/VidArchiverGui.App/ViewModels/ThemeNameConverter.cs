using System.Globalization;
using Avalonia.Data.Converters;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.App.ViewModels;

public sealed class ThemeNameConverter : IValueConverter
{
    public static ThemeNameConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AppTheme.System => "System default",
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => value?.ToString(),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
