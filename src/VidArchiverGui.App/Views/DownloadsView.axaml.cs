using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        Loaded += FocusUrlBoxOnce;
        Queue.AddHandler(KeyDownEvent, OnQueueKeyDown, handledEventsToo: true); // the list marks Enter handled for its selection
    }

    /// <summary>
    /// Keys for the download whose row has the focus: Delete removes it, Enter starts or resumes it, and Shift+F10 or the
    /// menu key opens its right-click menu. Only the row itself, so its folder box and drop-downs keep their keys.
    /// </summary>
    private async void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not ListBoxItem { DataContext: DownloadItemViewModel item } row)
        {
            return;
        }

        var plain = e.KeyModifiers == KeyModifiers.None;
        if (plain && (e.Key == Key.Delete || (e.Key == Key.Back && OperatingSystem.IsMacOS())))
        {
            e.Handled = true;
            var index = Queue.IndexFromContainer(row);
            if (!await item.RemoveAsync())
            {
                row.Focus(NavigationMethod.Directional);
            }
            else if (Queue.ItemCount > 0)
            {
                // Keep the keyboard in the list, on the row that took this one's place (or the new last one).
                Queue.SelectedIndex = Math.Min(index, Queue.ItemCount - 1);
                Dispatcher.UIThread.Post(() => Queue.ContainerFromIndex(Queue.SelectedIndex)?.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
            }
        }
        else if (plain && e.Key == Key.Enter && item.CanStart)
        {
            e.Handled = true;
            item.StartCommand.Execute(null);
        }
        else if ((plain && e.Key == Key.Apps) || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
        {
            // The menu belongs to the row's content, which the key doesn't reach on its own. It opens under the row
            // rather than at the mouse pointer, which could be anywhere.
            if (row.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.ContextMenu is not null) is { ContextMenu: { } menu } owner)
            {
                e.Handled = true;
                var placement = menu.Placement;
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
                menu.Closed += Restore;
                menu.Open(owner);

                void Restore(object? s, RoutedEventArgs a)
                {
                    menu.Closed -= Restore;
                    menu.Placement = placement;
                }
            }
        }
    }

    /// <summary>At launch, so a link can be pasted straight away (not on later visits to the tab).</summary>
    private void FocusUrlBoxOnce(object? sender, RoutedEventArgs e)
    {
        Loaded -= FocusUrlBoxOnce;
        // Not yet: the box can't take focus until the window has finished its first layout.
        Dispatcher.UIThread.Post(() => UrlBox.Focus(), DispatcherPriority.Loaded);
    }

    private const double MinLogHeight = 60;

    /// <summary>Leaves the download list room for about two downloads, so on a small screen the log gives way first.</summary>
    private double MaxLogHeight => Math.Max(MinLogHeight, Bounds.Height - 500);

    /// <summary>Dragging the grip above the log up makes the log taller.</summary>
    private void OnLogResize(object? sender, VectorEventArgs e)
    {
        if (DataContext is DownloadsViewModel vm)
        {
            // Start from the height actually shown, which may be capped below the remembered one.
            vm.Settings.LogHeight = Math.Clamp(Math.Min(vm.Settings.LogHeight, MaxLogHeight) - e.Vector.Y, MinLogHeight, MaxLogHeight);
        }
    }
}
