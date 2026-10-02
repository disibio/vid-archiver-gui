using CommunityToolkit.Mvvm.ComponentModel;

namespace VidArchiverGui.Core.Models;

/// <summary>A named set of yt-dlp arguments. The app appends the destination (-P) and progress flags itself.</summary>
public partial class Preset : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _name = "New preset";

    /// <summary>Raw yt-dlp arguments, as typed on a command line. Newlines are allowed; lines starting with # are comments.</summary>
    [ObservableProperty] private string _arguments = "";

    /// <summary>Downloader to use for this preset; null = the default downloader.</summary>
    [ObservableProperty] private string? _downloaderId;

    public Preset Clone() => new() { Name = Name + " (copy)", Arguments = Arguments, DownloaderId = DownloaderId };

    public override string ToString() => Name;
}
