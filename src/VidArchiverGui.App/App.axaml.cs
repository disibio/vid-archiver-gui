using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using VidArchiverGui.Core.Models;
using Avalonia.Markup.Xaml;
using VidArchiverGui.App.Services;
using VidArchiverGui.App.ViewModels;
using VidArchiverGui.App.Views;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsFile = AppPaths.SettingsFile;
#if DEBUG
            // Snapshot runs work on a copy, so they never change the real settings.
            if (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_DIR") is { Length: > 0 })
            {
                var copy = Path.Combine(Path.GetTempPath(), "vidarchivergui-snapshot-settings.json");
                if (File.Exists(settingsFile))
                {
                    File.Copy(settingsFile, copy, overwrite: true);
                }
                else
                {
                    File.Delete(copy);
                }

                settingsFile = copy;
            }
#endif
            var store = new SettingsStore(settingsFile);
            var settings = store.Load();
            ApplyTheme(settings.Theme);
            settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppSettings.Theme))
                {
                    ApplyTheme(settings.Theme);
                }
            };

            var window = new MainWindow();
            var dialogs = new WindowDialogs(window);
            var host = new AppHost(settings, store, dialogs);

            // Last line of defence: log unexpected UI-thread errors and keep running instead of closing the app.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    File.AppendAllText(Path.Combine(AppPaths.DataDir, "crash.log"), $"[{DateTime.Now:O}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
                }
                catch (IOException) { }
                host.SetStatus($"Unexpected error (details in crash.log): {e.Exception.Message}");
            };
            var vm = new MainWindowViewModel(host);

            window.DataContext = vm;
            window.Opened += async (_, _) => await vm.InitializeAsync();
            var quitConfirmed = false;
            window.Closing += async (_, e) =>
            {
                // Ask before cancelling running downloads, unless Windows is shutting down (no one to answer).
                var active = vm.Downloads.ActiveCount;
                if (!quitConfirmed && active > 0 && e.CloseReason is not (WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown))
                {
                    e.Cancel = true;
                    var what = active == 1 ? "1 download is" : $"{active} downloads are";
                    if (await dialogs.ConfirmAsync("Quit Vid Archiver GUI?",
                            $"{what} still running or waiting. If you quit, they'll be back in the list next time you open the app, " +
                            "and partly downloaded files carry on where they stopped.",
                            "Quit", "Keep downloading"))
                    {
                        quitConfirmed = true;
                        window.Close();
                    }

                    return;
                }

                vm.Shutdown();
                dialogs.CleanUp();
            };
            // Links dragged from a browser (or anywhere) onto the window are added like pasted ones.
            DragDrop.SetAllowDrop(window, true);
            window.AddHandler(DragDrop.DragOverEvent, (_, e) =>
                e.DragEffects = e.DataTransfer.Contains(DataFormat.Text) ? DragDropEffects.Copy : DragDropEffects.None);
            window.AddHandler(DragDrop.DropEvent, (_, e) =>
            {
                if (e.DataTransfer.TryGetText() is { Length: > 0 } text)
                {
                    vm.SelectedTab = 0;
                    vm.Downloads.AddUrls(text);
                    e.Handled = true;
                }
            });
            desktop.MainWindow = window;
#if DEBUG
            // Development aid: VIDARCHIVERGUI_SNAPSHOT_DIR=<dir> renders each tab to a PNG offscreen and exits.
            if (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_DIR") is { Length: > 0 } snapDir)
            {
                window.Opened += async (_, _) => await SnapshotTabsAsync(window, vm, snapDir, desktop);
            }
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(AppTheme theme) => RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default, // follow the OS setting, including live changes
    };

#if DEBUG
    /// <summary>Waits (up to 2 minutes) until first-run setup is done and no checklist row is still checking.</summary>
    private static async Task WaitForSetupCheckAsync(MainWindowViewModel vm)
    {
        for (var i = 0; i < 240 && (vm.Setup.Items.Count == 0 || vm.Setup.Items.Any(r => r.IsChecking) || vm.Tools.IsBusy); i++)
        {
            await Task.Delay(500);
        }
    }

    private static async Task SnapshotTabsAsync(Window window, MainWindowViewModel vm, string dir, IClassicDesktopStyleApplicationLifetime desktop)
    {
        Directory.CreateDirectory(dir);
        // Preview a theme without touching the saved setting.
        if (Enum.TryParse<AppTheme>(Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_THEME"), out var theme))
        {
            ((App)Current!).ApplyTheme(theme);
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
        if (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_COPY") == "1" && vm.Downloads.Items.FirstOrDefault() is { } first)
        {
            await first.CopyUrlCommand.ExecuteAsync(null);
        }

        if (Environment.GetEnvironmentVariable("VIDARCHIVERGUI_SNAPSHOT_SETUP") == "1")
        {
            // Fresh-machine run: wait for first-run setup, capture the banner, press "Fix now", capture again.
            await WaitForSetupCheckAsync(vm);

            await SaveTabAsync(window, vm, 0, Path.Combine(dir, "setup-before-downloads.png"));
            await SaveTabAsync(window, vm, 3, Path.Combine(dir, "setup-before-settings.png"));
            await vm.Setup.FixAllCommand.ExecuteAsync(null);
            await SaveTabAsync(window, vm, 3, Path.Combine(dir, "setup-after-settings.png"));
            await SaveTabAsync(window, vm, 0, Path.Combine(dir, "setup-after-downloads.png"));
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
                await SaveTabAsync(window, vm, 0, Path.Combine(dir, "download-result.png"));
                File.WriteAllLines(Path.Combine(dir, "download-result.txt"), vm.Downloads.Items.Select(x =>
                    $"{x.State}: {x.Title} -> {x.Destination}{Environment.NewLine}error: {x.Error}{Environment.NewLine}{x.LogText}"));
            }
            desktop.Shutdown();
            return;
        }

        vm.About.ShowNotices = true; // proves the embedded notices load
        for (var tab = 0; tab < 5; tab++)
        {
            await SaveTabAsync(window, vm, tab, Path.Combine(dir, $"tab{tab}.png"));
        }

        desktop.Shutdown();
    }

    private static async Task SaveTabAsync(Window window, MainWindowViewModel vm, int tab, string file)
    {
        vm.SelectedTab = tab;
        await Task.Delay(700);
        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(window);
#pragma warning disable CS0618 // debug-only helper; the default PNG encoding is fine
        bitmap.Save(file);
#pragma warning restore CS0618
    }
#endif
}
