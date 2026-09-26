using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VidArchiverGui.Core.Models;

/// <summary>Which command-line dialect a downloader speaks.</summary>
public enum EngineFlavor
{
    /// <summary>yt-dlp and its forks: supports -P, --progress-template, etc.</summary>
    YtDlp,
    /// <summary>youtube-dl and its forks: destination goes through -o, progress is parsed from the standard output.</summary>
    YoutubeDl,
}

/// <summary>A downloader program: either one the app installs from GitHub releases, or a custom executable.</summary>
public partial class Engine : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _name = "Custom downloader";
    [ObservableProperty] private EngineFlavor _flavor = EngineFlavor.YtDlp;

    /// <summary>GitHub "owner/repo" whose latest release the app installs and updates. Null for custom executables.</summary>
    [ObservableProperty] private string? _gitHubRepo;

    /// <summary>Executable for custom downloaders.</summary>
    [ObservableProperty] private string? _executablePath;

    [JsonIgnore] public bool IsManaged => GitHubRepo is not null;

    public override string ToString() => Name;

    public const string StableId = "yt-dlp";

    /// <summary>Downloaders the app knows how to install. Their definitions always come from here, not from settings.</summary>
    public static IReadOnlyList<Engine> CreateBuiltIns() =>
    [
        new() { Id = StableId, Name = "yt-dlp (stable)", GitHubRepo = "yt-dlp/yt-dlp" },
        new() { Id = "yt-dlp-nightly", Name = "yt-dlp (nightly)", GitHubRepo = "yt-dlp/yt-dlp-nightly-builds" },
        new() { Id = "yt-dlp-master", Name = "yt-dlp (master)", GitHubRepo = "yt-dlp/yt-dlp-master-builds" },
        new() { Id = "youtube-dl", Name = "youtube-dl (nightly)", GitHubRepo = "ytdl-org/ytdl-nightly", Flavor = EngineFlavor.YoutubeDl },
    ];
}
