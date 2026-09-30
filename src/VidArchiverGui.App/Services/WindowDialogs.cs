using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace VidArchiverGui.App.Services;

public interface IDialogs
{
    Task<string?> PickFolderAsync(string title, string? startPath = null);
    Task<string?> PickFileAsync(string title);

    /// <summary>Picks a JSON file to open.</summary>
    Task<string?> PickJsonFileAsync(string title);

    /// <summary>Asks where to save a JSON file; null if cancelled.</summary>
    Task<string?> SaveJsonFileAsync(string title, string suggestedName);
    Task OpenFolderAsync(string path);
    Task CopyTextAsync(string text);

    /// <summary>Asks a yes/no question; true if the user picked <paramref name="yes"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string yes, string no);

    /// <summary>Whether the app's window is the one in front.</summary>
    bool IsWindowActive { get; }

    /// <summary>A desktop notification (best effort).</summary>
    void Notify(string title, string message);
}

public sealed class WindowDialogs(Window window) : IDialogs
{
    private readonly SystemNotifier _notifier = new(window);

    public bool IsWindowActive => window.IsActive;

    public void Notify(string title, string message) => _notifier.Show(title, message);

    /// <summary>Removes anything the notifier left behind (the Windows tray icon).</summary>
    public void CleanUp() => _notifier.RemoveWindowsIcon();

    public async Task<bool> ConfirmAsync(string title, string message, string yes, string no)
    {
        var yesButton = new Button { Content = yes };
        var noButton = new Button { Content = no, IsDefault = true, IsCancel = true };
        noButton.Classes.Add("accent");
        var dialog = new Window
        {
            Title = title,
            Icon = window.Icon,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                MaxWidth = 460,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { yesButton, noButton },
                    },
                },
            },
        };
        yesButton.Click += (_, _) => dialog.Close(true);
        noButton.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(window);
    }

    public async Task<string?> PickFolderAsync(string title, string? startPath = null)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (NearestExistingDirectory(startPath) is { } start)
        {
            options.SuggestedStartLocation = await window.StorageProvider.TryGetFolderFromPathAsync(start);
        }

        var result = await window.StorageProvider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title)
    {
        var result = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    private static readonly FilePickerFileType[] JsonTypes =
    [
        new("JSON files") { Patterns = ["*.json"], MimeTypes = ["application/json"] },
        FilePickerFileTypes.All,
    ];

    public async Task<string?> PickJsonFileAsync(string title)
    {
        var result = await window.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = JsonTypes });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveJsonFileAsync(string title, string suggestedName)
    {
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = JsonTypes,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task OpenFolderAsync(string path)
    {
        if (NearestExistingDirectory(path) is { } dir)
        {
            await window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir));
        }
    }

    public async Task CopyTextAsync(string text)
    {
        if (window.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            await clipboard.FlushAsync(); // keep it on the clipboard after the app closes
        }
    }

    /// <summary>Destinations often don't exist until the first download, so walk up to the closest existing parent.</summary>
    private static string? NearestExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            while (dir is not null && !dir.Exists)
            {
                dir = dir.Parent;
            }

            return dir?.FullName;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
