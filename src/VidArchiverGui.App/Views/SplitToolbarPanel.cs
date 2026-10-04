using Avalonia;
using Avalonia.Controls;

namespace VidArchiverGui.App.Views;

/// <summary>
/// Two children on one line, the first at the left and the second at the right. When the window is too narrow for
/// both, the second moves onto a line of its own below, still at the right, instead of running into the first.
/// </summary>
public class SplitToolbarPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<SplitToolbarPanel, double>(nameof(Spacing), 12);

    static SplitToolbarPanel() => AffectsMeasure<SplitToolbarPanel>(SpacingProperty);

    /// <summary>The least gap between the two on one line, and between the lines once split.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private bool Split(Size size) =>
        Children.Count == 2 && Children[0].DesiredSize.Width + Spacing + Children[1].DesiredSize.Width > size.Width;

    protected override Size MeasureOverride(Size availableSize)
    {
        var any = new Size(double.PositiveInfinity, availableSize.Height);
        foreach (var child in Children)
        {
            child.Measure(any);
        }

        if (Children.Count != 2)
        {
            return Children.Count == 0 ? default : Children[0].DesiredSize;
        }

        var (left, right) = (Children[0].DesiredSize, Children[1].DesiredSize);
        return Split(availableSize)
            ? new Size(Math.Max(left.Width, right.Width), left.Height + Spacing + right.Height)
            : new Size(left.Width + Spacing + right.Width, Math.Max(left.Height, right.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2)
        {
            foreach (var child in Children)
            {
                child.Arrange(new Rect(child.DesiredSize));
            }

            return finalSize;
        }

        var (left, right) = (Children[0].DesiredSize, Children[1].DesiredSize);
        if (Split(finalSize))
        {
            Children[0].Arrange(new Rect(0, 0, finalSize.Width, left.Height));
            Children[1].Arrange(new Rect(Math.Max(0, finalSize.Width - right.Width), left.Height + Spacing, right.Width, right.Height));
        }
        else
        {
            var height = Math.Max(left.Height, right.Height);
            Children[0].Arrange(new Rect(0, 0, left.Width, height));
            Children[1].Arrange(new Rect(finalSize.Width - right.Width, 0, right.Width, height));
        }

        return finalSize;
    }
}
