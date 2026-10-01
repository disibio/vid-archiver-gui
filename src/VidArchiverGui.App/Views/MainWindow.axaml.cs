using Avalonia.Controls;

namespace VidArchiverGui.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SizeToScreen();
    }

    private const double GrowWidth = 0.6, GrowHeight = 0.75, MaxStartWidth = 1600, MaxStartHeight = 1000;

    /// <summary>
    /// Opens at the size set in MainWindow.axaml on a typical screen (1920×1080 and below), somewhat bigger on a large
    /// one, and shrunk to fit on a small one, where the default would put the bottom under the taskbar. It's centred
    /// after this, so it stays fully on screen.
    /// </summary>
    private void SizeToScreen()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
        var width = Math.Clamp(area.Width * GrowWidth, Width, Math.Max(Width, MaxStartWidth));
        var height = Math.Clamp(area.Height * GrowHeight, Height, Math.Max(Height, MaxStartHeight));
        // Leave room for the title bar and borders, which Width and Height don't include.
        Width = Math.Max(MinWidth, Math.Min(width, area.Width - 16));
        Height = Math.Max(MinHeight, Math.Min(height, area.Height - 48));
    }
}
