using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VidArchiverGui.App.ViewModels;
using VidArchiverGui.App.Views;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.App.Tests;

/// <summary>
/// The Downloads tab itself in a headless window: what the keyboard does. Set VIDARCHIVERGUI_TEST_SCREENSHOTS to a
/// folder to also get pictures of it.
/// </summary>
[Collection(nameof(DownloadListTests))]
public sealed class DownloadsViewTests : DownloadListTests
{
    private Window _window = null!;
    private ListBox _list = null!;

    private void Show(double width = 1150)
    {
        _window = new Window { Width = width, Height = 780, Content = new DownloadsView { DataContext = _vm } };
        _window.Show();
        _list = _window.GetVisualDescendants().OfType<ListBox>().Single();
    }

    private async Task<ListBoxItem> FocusRow(int index)
    {
        await Settle();
        var row = (ListBoxItem)_list.ContainerFromIndex(index)!;
        row.Focus(NavigationMethod.Tab);
        Assert.Same(row, Focused);
        return row;
    }

    private object? Focused => _window.FocusManager?.GetFocusedElement();

    private void Press(Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None, string? symbol = null)
    {
        _window.KeyPress(key, modifiers, physical, symbol);
        _window.KeyRelease(key, modifiers, physical, symbol);
    }

    /// <summary>Lets layout and anything posted to the UI thread run.</summary>
    private static async Task Settle()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Picture_of_the_list() => Headless.Run(async () =>
    {
        Show();
        _t.Settings.DefaultPreset.Arguments = "--write-thumbnail";
        var done = await AddAsync("https://fake.test/video/Finished with a thumbnail");
        done.StartCommand.Execute(null);
        await Headless.WaitUntil(() => done.Thumbnail is not null, "the thumbnail shows");
        var paused = await AddAsync("https://fake.test/playlist/4");
        await Slowly(async () =>
        {
            paused.StartCommand.Execute(null);
            await Headless.WaitUntil(() => paused.Progress > 30, "it's into the second item");
            paused.PauseCommand.Execute(null);
            await Headless.WaitUntil(() => paused.State == DownloadState.Paused, "it pauses");
        });
        await AddAsync("https://fake.test/video/Ready to start");
        await Settle();
        Headless.Capture(_window, "list");
    });

    [Fact]
    public Task The_logs_last_line_is_above_its_horizontal_scroll_bar() => Headless.Run(async () =>
    {
        _t.Settings.ShowLog = true;
        Show();
        var item = await AddAsync("https://fake.test/video/" + new string('x', 150));
        _vm.SelectedItem = item;
        item.StartCommand.Execute(null);
        await Headless.WaitUntil(() => item.State == DownloadState.Completed, "it finishes");
        await Settle();

        var log = _window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "LogBox");
        var text = log.GetVisualDescendants().OfType<ScrollContentPresenter>().Single();
        var bar = log.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Horizontal);
        Assert.True(bar.IsVisible);
        Assert.True(text.Bounds.Bottom <= bar.Bounds.Top, $"text ends at {text.Bounds.Bottom}, the bar starts at {bar.Bounds.Top}");
    });

    [Fact]
    public Task Delete_removes_the_focused_download_and_keeps_the_focus_in_the_list() => Headless.Run(async () =>
    {
        Show();
        await AddAsync("https://fake.test/video/a");
        var b = await AddAsync("https://fake.test/video/b");
        var c = await AddAsync("https://fake.test/video/c");

        await FocusRow(1);
        Press(Key.Delete, PhysicalKey.Delete);
        await Settle();

        Assert.DoesNotContain(b, _vm.Items);
        Assert.Same(c, (Focused as ListBoxItem)?.DataContext); // the row that took its place

        Press(Key.Delete, PhysicalKey.Delete);
        await Settle();
        Assert.Single(_vm.Items);
        Assert.IsType<ListBoxItem>(Focused); // the one left
        Assert.Empty(_t.Dialogs.Questions); // nothing was downloading, so nothing to ask
    });

    [Fact]
    public Task Keys_typed_in_a_rows_folder_box_stay_there() => Headless.Run(async () =>
    {
        Show();
        var item = await AddAsync("https://fake.test/video/a");
        await Settle();
        var box = _list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == item.Destination);
        box.Focus();
        box.CaretIndex = 0;

        Press(Key.Delete, PhysicalKey.Delete);
        Press(Key.Enter, PhysicalKey.Enter);

        Assert.Contains(item, _vm.Items);
        Assert.Equal(DownloadState.Ready, item.State);
        Assert.Equal(_folder[1..], item.Destination); // Delete removed a character instead
    });

    [Fact]
    public Task Tab_goes_through_a_rows_own_buttons_then_on_to_the_next_row() => Headless.Run(async () =>
    {
        Show();
        var a = await AddAsync("https://fake.test/video/a");
        var b = await AddAsync("https://fake.test/video/b");
        await FocusRow(0);

        var reached = new List<string>();
        for (var i = 0; i < 20 && (Focused as ListBoxItem)?.DataContext != b; i++)
        {
            Press(Key.Tab, PhysicalKey.Tab);
            await Settle();
            if (Focused is Button { DataContext: var owner, Content: string text } && owner == a)
            {
                reached.Add(text);
            }
        }

        Assert.Same(b, (Focused as ListBoxItem)?.DataContext);
        Assert.Equal(["Start", "Copy URL", "Open folder", "✕", "Browse…", "Remember"], reached);
    });

    [Fact]
    public Task Enter_starts_the_focused_download() => Headless.Run(async () =>
    {
        Show();
        var item = await AddAsync("https://fake.test/video/a");
        await FocusRow(0);

        Press(Key.Enter, PhysicalKey.Enter);

        await Headless.WaitUntil(() => item.State == DownloadState.Completed, "it downloads");
    });

    [Fact]
    public Task Removing_a_running_download_asks_first() => Headless.Run(() => Slowly(async () =>
    {
        Show();
        var item = await AddAsync("https://fake.test/video/a");
        item.StartCommand.Execute(null);
        await Headless.WaitUntil(() => item.Progress > 0, "it's downloading");
        await FocusRow(0);
        Assert.Equal(DownloadState.Downloading, item.State);

        _t.Dialogs.Answers.Enqueue(false);
        Press(Key.Delete, PhysicalKey.Delete);
        await Settle();
        Assert.Contains(item, _vm.Items);
        Assert.Equal(DownloadState.Downloading, item.State);
        Assert.Contains("still downloading", Assert.Single(_t.Dialogs.Questions));

        _t.Dialogs.Answers.Enqueue(true);
        await item.RemoveCommand.ExecuteAsync(null); // the ✕ button asks too
        Assert.Empty(_vm.Items);
        Assert.Equal(2, _t.Dialogs.Questions.Count);
        await WaitUntilStopped(item);
        Assert.Equal(DownloadState.Cancelled, item.State);
    }, stepMs: 1000)); // about 20 seconds per video, so it's still downloading on a slow CI machine

    [Fact]
    public Task Shift_F10_opens_the_rows_menu_and_the_arrow_keys_work_in_it() => Headless.Run(async () =>
    {
        Show();
        await AddAsync("https://fake.test/video/a");
        var row = await FocusRow(0);

        Press(Key.F10, PhysicalKey.F10, RawInputModifiers.Shift);
        await Settle();
        var menu = row.GetVisualDescendants().OfType<Control>().Select(c => c.ContextMenu).Single(m => m is not null)!;
        Assert.True(menu.IsOpen);
        Headless.Capture(_window, "row-menu");

        Press(Key.Down, PhysicalKey.ArrowDown);
        Press(Key.Enter, PhysicalKey.Enter);
        await Settle();
        Assert.False(menu.IsOpen);
        Assert.Contains(_t.Statuses, s => s.StartsWith("Copied URL", StringComparison.Ordinal));
    });

    [Fact]
    public Task Alt_and_a_letter_press_a_toolbar_button_but_the_letter_alone_does_not() => Headless.Run(async () =>
    {
        Show();
        var item = await AddAsync("https://fake.test/video/a");
        await FocusRow(0);

        Press(Key.S, PhysicalKey.S, symbol: "s");
        await Settle();
        Assert.Equal(DownloadState.Ready, item.State);

        _window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        await Settle();
        Headless.Capture(_window, "alt-held");
        Press(Key.S, PhysicalKey.S, RawInputModifiers.Alt, "s");
        _window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);

        await Headless.WaitUntil(() => item.State == DownloadState.Completed, "Start all ran");
    });
    [Fact]
    public Task Starting_a_failed_download_keeps_the_list_where_it_was() => Headless.Run(async () =>
    {
        // Failed rows with their error, and much shorter finished ones among them: Avalonia's list used to guess every
        // row's position from the average height once one row changed, and jump far away.
        for (var i = 0; i < 150; i++)
        {
            _t.Settings.UnfinishedDownloads.Add(new SavedDownload { Url = "https://fake.test/video/v" + i, Error = "ERROR: [fake] v" + i + ": This video is private" });
        }

        _vm.RestoreUnfinished();
        for (var i = 0; i < 150; i += 3)
        {
            _vm.Items[i].State = DownloadState.Completed;
        }

        Show();
        var scroll = _list.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, scroll.Extent.Height * 0.7);
        await Settle();
        var row = _list.GetRealizedContainers().OfType<ListBoxItem>().OrderBy(r => r.Bounds.Top)
            .First(r => r.Bounds.Top > scroll.Offset.Y + 100 && r.DataContext is DownloadItemViewModel { State: DownloadState.Failed });
        var item = (DownloadItemViewModel)row.DataContext!;
        var start = row.GetVisualDescendants().OfType<Button>().First(b => b.IsVisible && b.Content as string == item.StartText);
        var top = row.TranslatePoint(default, _window)!.Value.Y;

        void StaysPut(string when)
        {
            var now = _list.ContainerFromItem(item)?.TranslatePoint(default, _window)?.Y;
            Assert.True(now is { } y && Math.Abs(y - top) < 1, $"{when}, the row moved from {top} to {(now?.ToString() ?? "off screen")}");
        }

        Environment.SetEnvironmentVariable("FAKEYTDLP_INFO_FAIL", "1");
        Environment.SetEnvironmentVariable("FAKEYTDLP_INFO_MS", "1000");
        try
        {
            var at = start.TranslatePoint(new Point(5, 5), _window)!.Value;
            _window.MouseDown(at, MouseButton.Left);
            _window.MouseUp(at, MouseButton.Left);
            await Settle();
            Assert.Equal(DownloadState.Resolving, item.State);
            StaysPut("Starting it");

            await WaitUntilStopped(item);
            await Settle();
            Assert.Equal(DownloadState.Failed, item.State);
            StaysPut("Failing again");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKEYTDLP_INFO_FAIL", null);
            Environment.SetEnvironmentVariable("FAKEYTDLP_INFO_MS", null);
        }
    });

    [Fact]
    public Task A_rows_button_that_hides_when_pressed_leaves_the_focus_on_its_row() => Headless.Run(() => Slowly(async () =>
    {
        Show();
        var item = await AddAsync("https://fake.test/video/a");
        var other = await AddAsync("https://fake.test/video/b");
        await Settle();
        var row = (ListBoxItem)_list.ContainerFromItem(item)!;
        var start = row.GetVisualDescendants().OfType<Button>().First(b => b.IsVisible && b.Content as string == "Start");
        start.Focus(NavigationMethod.Tab);

        Press(Key.Space, PhysicalKey.Space, symbol: " ");
        await Settle();
        Assert.False(item.CanStart);
        Assert.Same(row, Focused);

        Press(Key.Tab, PhysicalKey.Tab);
        await Settle();
        Assert.Equal("Pause", (Focused as Button)?.Content);
        Assert.Same(item, (Focused as Button)?.DataContext);

        Press(Key.Space, PhysicalKey.Space, symbol: " "); // Pause hides too
        await Settle();
        Assert.Same(row, Focused);
        Assert.NotSame(other, (Focused as Control)?.DataContext);
    }, stepMs: 1000));

    [Theory]
    [InlineData(720)]
    [InlineData(1150)]
    public Task The_toolbar_fits_the_window(double width) => Headless.Run(async () =>
    {
        Show(width);
        await AddAsync("https://fake.test/video/a");
        await Settle();
        var bounds = new Rect(_window.ClientSize);
        Rect Where(Control c) => new(c.TranslatePoint(default, _window)!.Value, c.Bounds.Size);

        var reapply = Where(_window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "_Re-apply rules"));
        var summary = Where(_window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.Contains("1 item", StringComparison.Ordinal) == true));
        var toggle = Where(_window.GetVisualDescendants().OfType<ToggleSwitch>().Single());
        Headless.Capture(_window, $"toolbar-{width}");

        Assert.True(bounds.Contains(toggle), $"the switch at {toggle} is outside {bounds}");
        Assert.False(summary.Intersects(reapply), $"the summary at {summary} overlaps the button at {reapply}");
        Assert.False(toggle.Intersects(reapply));
    });
}
