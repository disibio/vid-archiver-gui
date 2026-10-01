using Avalonia.Controls;

namespace VidArchiverGui.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
    }

    /// <summary>
    /// The default size is taller than a 1366×768 laptop's usable area, which would put the bottom of the window
    /// under the taskbar; shrink it to fit (it's centred after this, so it stays fully on screen).
    /// </summary>
    private void FitToScreen()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
        // Leave room for the title bar and borders, which Width and Height don't include.
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width - 16));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height - 48));
    }
}
