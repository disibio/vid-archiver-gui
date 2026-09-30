using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VidArchiverGui.App.ViewModels;

public sealed record Credit(string Name, string Purpose, string License, string Url);

public partial class AboutViewModel : ObservableObject
{
    public string AppName => "Vid Archiver GUI";

    public string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
        .Split('+')[0]; // drop the source-revision suffix

    public string BuildSummary => Services.BuildInfo.Summary;

    public string Copyright => "Copyright 2026 The Vid Archiver GUI contributors";

    public const string AiNotice =
        "Vid Archiver GUI is written with Claude Opus 5.5.";

    public const string ProjectUrl = "https://github.com/disibio/vid-archiver-gui";
    public const string IssuesUrl = ProjectUrl + "/issues";

    public const string InspiredByUrl = "https://github.com/oleksis/youtube-dl-gui";

    public IReadOnlyList<Credit> Bundled { get; } =
    [
        new("Avalonia", "Cross-platform UI framework", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
        new("CommunityToolkit.Mvvm", "MVVM helpers", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("SkiaSharp / HarfBuzzSharp", "Rendering and text shaping", "MIT", "https://github.com/mono/SkiaSharp"),
        new("ANGLE", "Graphics layer on Windows", "BSD-3-Clause", "https://chromium.googlesource.com/angle/angle"),
        new("Inter", "UI font", "SIL OFL 1.1", "https://github.com/rsms/inter"),
        new("MicroCom.Runtime", "COM interop", "MIT", "https://github.com/kekekeks/MicroCom"),
        new("Tmds.DBus.Protocol", "D-Bus (Linux)", "MIT", "https://github.com/tmds/Tmds.DBus"),
        new(".NET", "Runtime (self-contained builds)", "MIT", "https://github.com/dotnet/runtime"),
    ];

    public IReadOnlyList<Credit> Downloaded { get; } =
    [
        new("yt-dlp", "The downloader (stable, nightly, master)", "Unlicense", "https://github.com/yt-dlp/yt-dlp"),
        new("youtube-dl", "Alternative downloader", "Unlicense", "https://github.com/ytdl-org/youtube-dl"),
        new("FFmpeg", "Merging, converting, embedding (yt-dlp/FFmpeg-Builds)", "GPL-3.0", "https://ffmpeg.org"),
    ];

    [ObservableProperty] private bool _showLicense;
    [ObservableProperty] private bool _showNotices;
    [ObservableProperty] private string _licenseText = "";
    [ObservableProperty] private string _noticesText = "";

    // The full texts are large, so only load them when their section is opened.
    partial void OnShowLicenseChanged(bool value)
    {
        if (value && LicenseText.Length == 0)
        {
            LicenseText = ReadResource("VidArchiverGui.LICENSE");
        }
    }

    partial void OnShowNoticesChanged(bool value)
    {
        if (value && NoticesText.Length == 0)
        {
            NoticesText = ReadResource("VidArchiverGui.NOTICE") + "\n\n" + ReadResource("VidArchiverGui.THIRD-PARTY-NOTICES.txt");
        }
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(AboutViewModel).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return $"({name} is missing from this build.)";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
