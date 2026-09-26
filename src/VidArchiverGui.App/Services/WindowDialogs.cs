using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace VidArchiverGui.App.Services;

public interface IDialogs
{
    Task<string?> PickFolderAsync(string title, string? startPath = null);
    Task<string?> PickFileAsync(string title);
    Task OpenFolderAsync(string path);
    Task CopyTextAsync(string text);
}

public sealed class WindowDialogs(Window window) : IDialogs
{
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
