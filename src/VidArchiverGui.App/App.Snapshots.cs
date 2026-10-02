#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using VidArchiverGui.App.ViewModels;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.App;

/// <summary>
/// Development aid: VIDARCHIVERGUI_SNAPSHOT_DIR=&lt;dir&gt; renders each tab to a PNG offscreen and exits. Optional:
/// VIDARCHIVERGUI_SNAPSHOT_THEME (Light/Dark, or "Light,Dark" for both), _URLS (";"-separated links to add),
/// _START=&lt;n&gt; (download the first n links for real and wait for them), _COPY=1 (copy the first URL),
/// _SETUP=1 (fresh-machine run: capture, press "Fix now", capture again), _DOWNLOAD=&lt;url&gt; and _PRESET=&lt;name&gt;
/// (with _SETUP: download for real to prove the whole pipeline).
/// </summary>
public partial class App
{
    private static string? SnapshotDir => Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>Snapshot runs work on a copy of the settings, so they never change the real ones.</summary>
    private static string SnapshotSettingsFile(string settingsFile)
    {
        if (SnapshotDir is null)
        {
            return settingsFile;
        }

        var copy = Path.Combine(Path.GetTempPath(), "vidarchivergui-snapshot-settings.json");
        if (File.Exists(settingsFile))
        {
            File.Copy(settingsFile, copy, overwrite: true);
        }
        else
        {
            File.Delete(copy);
        }

        return copy;
    }

    private static void StartSnapshotsIfRequested(Window window, MainWindowViewModel vm, IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (SnapshotDir is { } dir)
        {
            vm.SettingsTab.Settings.NotifyWhenDone = false; // the window is never in front, so every finished run would notify
            window.Opened += async (_, _) => await SnapshotTabsAsync(window, vm, dir, desktop);
        }
    }

    /// <summary>Waits (up to 2 minutes) until first-run setup is done and no checklist row is still checking.</summary>
    private static async Task WaitForSetupCheckAsync(MainWindowViewModel vm)
    {
        for (var i = 0; i < 240 && (vm.Setup.Items.Count == 0 || vm.Setup.Items.Any(r => r.IsChecking) || vm.Setup.Busy.IsActive); i++)
        {
            await Task.Delay(500);
        }
    }

    private static async Task SnapshotTabsAsync(Window window, MainWindowViewModel vm, string dir, IClassicDesktopStyleApplicationLifetime desktop)
    {
        Directory.CreateDirectory(dir);
        // Preview themes without touching the saved setting.
        var themes = (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_THEME") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => Enum.TryParse<AppTheme>(t, out var theme) ? theme : (AppTheme?)null).OfType<AppTheme>().ToList();
        if (themes.Count > 0)
        {
            ((App)Current!).ApplyTheme(themes[0]);
        }

        if (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_URLS") is { Length: > 0 } urls)
        {
            vm.Downloads.UrlInput = urls.Replace(';', '\n');
            vm.Downloads.AddCommand.Execute(null);
        }
        // Ignore the mouse, so wherever the pointer happens to be doesn't show up as a hover highlight.
        if (window.Content is Control content)
        {
            content.IsHitTestVisible = false;
        }

        await Task.Delay(9000); // let tool detection and metadata lookups finish
        for (var i = 0; i < 120 && vm.Downloads.Items.Any(x => x.State == DownloadState.Resolving); i++)
        {
            await Task.Delay(500);
        }

        await WaitForSetupCheckAsync(vm);
        if (int.TryParse(Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_START"), out var start))
        {
            foreach (var item in vm.Downloads.Items.Take(start))
            {
                item.StartCommand.Execute(null);
            }

            for (var i = 0; i < 1200 && vm.Downloads.Items.Any(x => x.State is DownloadState.Queued or DownloadState.Downloading); i++)
            {
                await Task.Delay(500);
            }

            await Task.Delay(3000); // thumbnails load after a download finishes
        }

        if (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_COPY") == "1" && vm.Downloads.Items.FirstOrDefault() is { } first)
        {
            await first.CopyUrlCommand.ExecuteAsync(null);
        }

        if (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_SETUP") == "1")
        {
            // Fresh-machine run: wait for first-run setup, capture the banner, press "Fix now", capture again.
            await WaitForSetupCheckAsync(vm);

            await SaveTabAsync(window, vm, MainTab.Downloads, Path.Combine(dir, "setup-before-downloads.png"));
            await SaveTabAsync(window, vm, MainTab.Settings, Path.Combine(dir, "setup-before-settings.png"));
            await vm.Setup.FixAllCommand.ExecuteAsync(null);
            await SaveTabAsync(window, vm, MainTab.Settings, Path.Combine(dir, "setup-after-settings.png"));
            await SaveTabAsync(window, vm, MainTab.Downloads, Path.Combine(dir, "setup-after-downloads.png"));
            File.WriteAllLines(Path.Combine(dir, "setup-items.txt"), vm.Setup.Items.Select(i => $"{i.Item.Status}: {i.Name} — {i.Detail}"));

            // Optionally download for real with the default preset, to prove the whole pipeline on this machine.
            if (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_DOWNLOAD") is { Length: > 0 } url)
            {
                if (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_PRESET") is { Length: > 0 } presetName)
                {
                    vm.Downloads.SelectedPreset = vm.Downloads.Presets.FirstOrDefault(p => p.Name.StartsWith(presetName, StringComparison.OrdinalIgnoreCase));
                }

                vm.Downloads.UrlInput = url;
                vm.Downloads.AddCommand.Execute(null);
                for (var i = 0; i < 120 && vm.Downloads.Items.Any(x => x.State == DownloadState.Resolving); i++)
                {
                    await Task.Delay(500);
                }

                vm.Downloads.StartAllCommand.Execute(null);
                for (var i = 0; i < 600 && vm.Downloads.Items.Any(x => x.State is DownloadState.Queued or DownloadState.Downloading); i++)
                {
                    await Task.Delay(500);
                }

                vm.Downloads.SelectedItem = vm.Downloads.Items.FirstOrDefault();
                await SaveTabAsync(window, vm, MainTab.Downloads, Path.Combine(dir, "download-result.png"));
                File.WriteAllLines(Path.Combine(dir, "download-result.txt"), vm.Downloads.Items.Select(x =>
                    $"{x.State}: {x.Title} -> {x.Destination}{Environment.NewLine}error: {x.Error}{Environment.NewLine}{x.LogText}"));
            }
            desktop.Shutdown();
            return;
        }

        vm.About.ShowNotices = true; // proves the embedded notices load
        vm.CurrentTab = MainTab.About; // leaving the tab takes the focus off the URL box, so it has no focus border
        await Task.Delay(300);
        foreach (var theme in themes.Count > 1 ? themes.Cast<AppTheme?>() : [null])
        {
            if (theme is { } t)
            {
                ((App)Current!).ApplyTheme(t);
            }

            foreach (var tab in Enum.GetValues<MainTab>())
            {
                await SaveTabAsync(window, vm, tab, Path.Combine(dir, theme is null ? $"tab{(int)tab}.png" : $"tab{(int)tab}-{theme}.png"));
            }
        }

        desktop.Shutdown();
    }

    private static async Task SaveTabAsync(Window window, MainWindowViewModel vm, MainTab tab, string file)
    {
        vm.CurrentTab = tab;
        await Task.Delay(700);
        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(window);
#pragma warning disable CS0618 // debug-only helper; the default PNG encoding is fine
        bitmap.Save(file);
#pragma warning restore CS0618
    }
}
#endif
