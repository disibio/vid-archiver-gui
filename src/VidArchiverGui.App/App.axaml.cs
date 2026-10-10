using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using VidArchiverGui.App.Services;
using VidArchiverGui.App.ViewModels;
using VidArchiverGui.App.Views;
using VidArchiverGui.Core.Models;
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
            settingsFile = SnapshotSettingsFile(settingsFile);
#endif
            var store = new SettingsStore(settingsFile);
            _settingsLock = store.TryLock();
            if (_settingsLock is null)
            {
                desktop.MainWindow = AlreadyOpenWindow();
                base.OnFrameworkInitializationCompleted();
                return;
            }

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
            window.KeepPlacement(settings);
            var dialogs = new WindowDialogs(window);
            var host = new AppHost(settings, store, dialogs);

            // Last line of defence: log unexpected UI-thread errors and keep running instead of closing the app.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                CrashLog.Write(e.Exception);
                host.SetStatus($"Unexpected error (details in {CrashLog.FileName}): {e.Exception.Message}");
            };

            var vm = new MainWindowViewModel(host);
            window.DataContext = vm;
            window.Opened += async (_, _) =>
            {
                if (store.CorruptCopy is { } copy && await dialogs.ConfirmAsync("Settings couldn't be read",
                        $"Your settings file couldn't be read, so the app has started with the default settings. The old file was kept as {copy}.",
                        "Show file", "OK"))
                {
                    await dialogs.ShowFileAsync(copy);
                }

                await vm.InitializeAsync();
            };
            ConfirmQuitWhileDownloading(window, vm, dialogs);
            AcceptDroppedLinks(window, vm);
            desktop.MainWindow = window;
#if DEBUG
            StartSnapshotsIfRequested(window, vm, desktop);
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Held for as long as the app runs (see SettingsStore.TryLock).
    private static IDisposable? _settingsLock;

    /// <summary>What a second copy shows, instead of the app, when one using the same settings is already open.</summary>
    private static Window AlreadyOpenWindow()
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = "Vid Archiver GUI",
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://VidArchiverGui/Assets/icon.ico"))),
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                MaxWidth = 460,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Vid Archiver GUI is already open. It may be minimized or on another desktop. Only one copy " +
                               "can use the same settings and download list at a time.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    ok,
                },
            },
        };
        ok.Click += (_, _) => window.Close();
        return window;
    }

    private void ApplyTheme(AppTheme theme) => RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default, // follow the OS setting, including live changes
    };

    /// <summary>Asks before cancelling running downloads, unless the OS is shutting down (no one to answer).</summary>
    private static void ConfirmQuitWhileDownloading(Window window, MainWindowViewModel vm, WindowDialogs dialogs)
    {
        var quitConfirmed = false;
        window.Closing += async (_, e) =>
        {
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
    }

    /// <summary>Links dragged from a browser (or anywhere) onto the window are added like pasted ones.</summary>
    private static void AcceptDroppedLinks(Window window, MainWindowViewModel vm)
    {
        DragDrop.SetAllowDrop(window, true);
        window.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.DataTransfer.Contains(DataFormat.Text) ? DragDropEffects.Copy : DragDropEffects.None);
        window.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetText() is { Length: > 0 } text)
            {
                vm.CurrentTab = MainTab.Downloads;
                vm.Downloads.AddUrls(text);
                e.Handled = true;
            }
        });
    }
}
