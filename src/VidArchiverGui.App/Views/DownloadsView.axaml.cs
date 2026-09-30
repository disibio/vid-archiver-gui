using Avalonia.Controls;
using Avalonia.Input;
using VidArchiverGui.App.ViewModels;

namespace VidArchiverGui.App.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView()
    {
        InitializeComponent();
        // A height remembered from a bigger window is capped here rather than overwritten, so it comes back when
        // the window is big again.
        SizeChanged += (_, _) => LogBox.MaxHeight = MaxLogHeight;
    }

    /// <summary>Leaves the download list at least ~200 px.</summary>
    private double MaxLogHeight => Math.Max(80, Bounds.Height - 420);

    /// <summary>Dragging the grip above the log up makes the log taller.</summary>
    private void OnLogResize(object? sender, VectorEventArgs e)
    {
        if (DataContext is DownloadsViewModel vm)
        {
            // Start from the height actually shown, which may be capped below the remembered one.
            vm.Settings.LogHeight = Math.Clamp(Math.Min(vm.Settings.LogHeight, MaxLogHeight) - e.Vector.Y, 80, MaxLogHeight);
        }
    }
}
