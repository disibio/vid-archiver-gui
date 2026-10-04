using Avalonia;
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
        LogBox.PropertyChanged += OnLogTextChanged;
        Queue.AddHandler(GotFocusEvent, (_, e) => _queueFocusMethod = e.NavigationMethod);
        Queue.AddHandler(LostFocusEvent, OnQueueLostFocus);
    }

    private NavigationMethod _queueFocusMethod;

    /// <summary>
    /// Start, Pause and Cancel hide themselves once pressed, which would leave nothing focused and send the next Tab
    /// back to the top of the window. The focus goes to their row instead, outlined only if the button was reached
    /// with the keyboard.
    /// </summary>
    private void OnQueueLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is not Control { IsEffectivelyVisible: false } hidden || hidden.FindAncestorOfType<ListBoxItem>() is not { } row)
        {
            return;
        }

        // After the hiding has finished, and only if nothing else took the focus meanwhile.
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null && row.IsEffectivelyVisible)
            {
                row.Focus(_queueFocusMethod == NavigationMethod.Pointer ? NavigationMethod.Pointer : NavigationMethod.Directional);
            }
        });
    }

    private object? _logItem;

    /// <summary>
    /// Keeps the newest log lines in view: scrolled to the bottom and back to the left edge, for a newly selected item
    /// or when already at the bottom. Someone scrolled up to read stays where they are.
    /// </summary>
    private void OnLogTextChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.TextProperty || LogBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll)
        {
            return;
        }

        var item = (DataContext as DownloadsViewModel)?.SelectedItem;
        var follow = !ReferenceEquals(item, _logItem) || scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1;
        _logItem = item;
        if (follow)
        {
            // After layout, so the extent includes the new lines.
            Dispatcher.UIThread.Post(() => scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)), DispatcherPriority.Loaded);
        }
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
