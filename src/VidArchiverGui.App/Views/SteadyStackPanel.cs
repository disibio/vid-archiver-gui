using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace VidArchiverGui.App.Views;

/// <summary>
/// A <see cref="VirtualizingStackPanel"/> that stays put when a row on screen changes height (a download starting,
/// failing or finishing). Avalonia's panel then forgets where its rows are and guesses again from the average row
/// height, which with short finished rows among tall failed ones lands somewhere else in the list. Here the rows just
/// below the changed one move, as in a plain stack.
/// </summary>
/// <remarks>
/// It does this by telling the panel's private list of row heights about the change before the panel compares it.
/// Should a newer Avalonia rename those fields, this does nothing and the old jumping is back (a test catches that).
/// </remarks>
public class SteadyStackPanel : VirtualizingStackPanel
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo? RealizedField = typeof(VirtualizingStackPanel).GetField("_realizedElements", Private);
    private static readonly FieldInfo? ElementsField = RealizedField?.FieldType.GetField("_elements", Private);
    private static readonly FieldInfo? SizesField = RealizedField?.FieldType.GetField("_sizes", Private);

    /// <summary>Whether this Avalonia still has the fields used here.</summary>
    private static bool Works => ElementsField?.FieldType == typeof(List<Control?>) && SizesField?.FieldType == typeof(List<double>);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Works && RealizedField!.GetValue(this) is { } realized
            && ElementsField!.GetValue(realized) is List<Control?> elements && SizesField!.GetValue(realized) is List<double> sizes)
        {
            var horizontal = Orientation == Orientation.Horizontal;
            for (var i = 0; i < Math.Min(elements.Count, sizes.Count); i++)
            {
                if (elements[i] is { } row)
                {
                    sizes[i] = horizontal ? row.DesiredSize.Width : row.DesiredSize.Height;
                }
            }
        }

        return base.MeasureOverride(availableSize);
    }
}
