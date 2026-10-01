using System.Globalization;
using Avalonia.Data.Converters;

namespace VidArchiverGui.App.ViewModels;

/// <summary>A share of a width, e.g. "0.45" of a row, so a limit grows with the window.</summary>
public sealed class FractionConverter : IValueConverter
{
    public static FractionConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)
            ? width * fraction
            : double.PositiveInfinity;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
