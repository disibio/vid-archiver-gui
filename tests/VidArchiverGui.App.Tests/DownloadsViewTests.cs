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

    private void Show()
    {
        _window = new Window { Width = 1150, Height = 780, Content = new DownloadsView { DataContext = _vm } };
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
    }));

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
}
