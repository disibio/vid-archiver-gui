using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

public enum SetupStatus
{
    Ok,
    Warning,
    Missing,
}

public enum SetupFix
{
    None,
    InstallDownloader,
    InstallDeno,
    InstallFfmpeg,
    CreateFolder,
    CopyCommand,
}

/// <summary>One requirement on the setup checklist.</summary>
public sealed record SetupItem(string Name, SetupStatus Status, string Detail, string Why)
{
    public SetupFix Fix { get; init; } = SetupFix.None;

    /// <summary>Shell command for <see cref="SetupFix.CopyCommand"/>.</summary>
    public string? Command { get; init; }

    /// <summary>Folder for <see cref="SetupFix.CreateFolder"/>.</summary>
    public string? Folder { get; init; }

    public bool CanAutoFix => Fix is SetupFix.InstallDownloader or SetupFix.InstallDeno or SetupFix.InstallFfmpeg or SetupFix.CreateFolder;
}

/// <summary>What yt-dlp itself reports it can use, from the header of <c>yt-dlp -v</c>.</summary>
public sealed record YtDlpProbe(bool? HasFfmpeg, string? JsRuntimes, bool? HasEjs)
{
    public bool? HasJsRuntime => JsRuntimes is null ? null : !JsRuntimes.Equals("none", StringComparison.OrdinalIgnoreCase);

    public static YtDlpProbe Parse(string output)
    {
        bool? ffmpeg = null, ejs = null;
        string? js = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("[debug] exe versions:", StringComparison.Ordinal))
            {
                ffmpeg = line.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("[debug] JS runtimes:", StringComparison.Ordinal))
            {
                js = line["[debug] JS runtimes:".Length..].Trim();
            }
            else if (line.StartsWith("[debug] Optional libraries:", StringComparison.Ordinal))
            {
                ejs = line.Contains("yt_dlp_ejs", StringComparison.Ordinal);
            }
        }
        return new YtDlpProbe(ffmpeg, js, ejs);
    }
}

/// <summary>Works out what's needed for downloads to work on this machine, and how to fix what's missing.</summary>
public sealed class SetupChecker(AppSettings settings, ToolManager tools)
{
    private const string DownloaderWhy = "Does the actual downloading.";
    private const string FfmpegWhy = "Merges separate video and audio (e.g. MKV output) and embeds thumbnails, subtitles and metadata.";
    private const string DenoWhy = "yt-dlp uses it to solve some sites' JavaScript challenges: without it formats can be missing and some downloads fail.";

    public async Task<IReadOnlyList<SetupItem>> CheckAsync(CancellationToken ct = default)
    {
        var items = new List<SetupItem>();
        var engine = settings.DefaultEngine;
        var path = ToolManager.LocatePath(engine);

        // 1. The downloader itself.
        if (path is null)
        {
            items.Add(new SetupItem(engine.Name, SetupStatus.Missing,
                engine.IsManaged ? "Not installed yet." : $"Executable not found: {engine.ExecutablePath}. Fix it under Downloaders below.",
                DownloaderWhy) { Fix = engine.IsManaged ? SetupFix.InstallDownloader : SetupFix.None });
        }
        else
        {
            items.Add(new SetupItem(engine.Name, SetupStatus.Ok, $"{await tools.GetVersionAsync(path, ct) ?? "installed"}  —  {path}", DownloaderWhy));
        }

        // Ask yt-dlp what it can actually see (it's the final word on ffmpeg and the JS runtime).
        var probe = path is not null && engine.Flavor == EngineFlavor.YtDlp ? await ProbeAsync(tools.Resolve(engine), ct) : null;

        // 2. ffmpeg.
        var ffmpeg = tools.ResolveFfmpeg();
        if (probe?.HasFfmpeg ?? ffmpeg is not null)
        {
            items.Add(new SetupItem("ffmpeg", SetupStatus.Ok, ffmpeg ?? "found by the downloader", FfmpegWhy));
        }
        else
        {
            items.Add(FfmpegMissing());
        }

        // 3. deno (yt-dlp only; youtube-dl doesn't use one).
        if (engine.Flavor == EngineFlavor.YtDlp)
        {
            var deno = ToolManager.ResolveDeno();
            if (probe?.HasJsRuntime ?? deno is not null)
            {
                items.Add(new SetupItem("deno (JavaScript runtime)", SetupStatus.Ok,
                    $"{probe?.JsRuntimes ?? "deno"}  —  {deno ?? "found by the downloader"}", DenoWhy));
            }
            else
            {
                items.Add(new SetupItem("deno (JavaScript runtime)", SetupStatus.Missing,
                    ToolManager.HasOtherJsRuntime ? "Node.js/Bun is installed, but yt-dlp only uses deno by default." : "Not found.",
                    DenoWhy) { Fix = SetupFix.InstallDeno });
            }

            if (probe?.HasEjs == false)
            {
                items.Add(new SetupItem("yt-dlp JavaScript components (yt-dlp-ejs)", SetupStatus.Warning,
                    "This yt-dlp build doesn't include yt-dlp-ejs. Use an app-installed yt-dlp, or reinstall it with the command shown.",
                    DenoWhy) { Fix = SetupFix.CopyCommand, Command = "pip install -U \"yt-dlp[default]\"" });
            }
        }

        // 4. Destination drives (e.g. an external E: drive that isn't plugged in).
        items.Add(CheckDestinationDrives());

        // 5. Folders for --download-archive files.
        items.AddRange(CheckArchiveFolders());

        return items;
    }

    public async Task<YtDlpProbe?> ProbeAsync(ResolvedEngine engine, CancellationToken ct = default)
    {
        List<string> args = ["-v"];
        if (tools.ResolveFfmpeg() is { } ffmpeg)
        {
            args.AddRange(["--ffmpeg-location", ffmpeg]);
        }

        args.AddRange(engine.ExtraArgs);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // With no URL yt-dlp prints its debug header and then exits with an error; the header is all we need.
            var r = await ProcessHelper.RunAsync(engine.Path, args, timeout.Token);
            var probe = YtDlpProbe.Parse(r.StdErr + "\n" + r.StdOut);
            return probe.HasFfmpeg is null && probe.JsRuntimes is null ? null : probe;
        }
        catch (Exception e) when (e is OperationCanceledException && !ct.IsCancellationRequested || e is System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private SetupItem FfmpegMissing()
    {
        var item = new SetupItem("ffmpeg", SetupStatus.Missing, "Not found.", FfmpegWhy);
        if (tools.CanDownloadFfmpeg)
        {
            return item with { Fix = SetupFix.InstallFfmpeg };
        }

        if (FfmpegInstallCommand() is { } command)
        {
            return item with { Detail = "Not found. Install it with the command below.", Fix = SetupFix.CopyCommand, Command = command };
        }

        return item with
        {
            Detail = OperatingSystem.IsMacOS()
                ? "Not found. Install Homebrew from https://brew.sh, then run: brew install ffmpeg"
                : "Not found. Install ffmpeg with your system's package manager.",
        };
    }

    /// <summary>The install command for this system's package manager, if one is recognised.</summary>
    public static string? FfmpegInstallCommand()
    {
        if (OperatingSystem.IsMacOS())
        {
            return ToolManager.FindOnPath("brew") is not null ? "brew install ffmpeg" : null;
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        if (ToolManager.FindOnPath("apt-get") is not null)
        {
            return "sudo apt install ffmpeg";
        }

        if (ToolManager.FindOnPath("dnf") is not null)
        {
            return "sudo dnf install ffmpeg";
        }

        if (ToolManager.FindOnPath("pacman") is not null)
        {
            return "sudo pacman -S ffmpeg";
        }

        if (ToolManager.FindOnPath("zypper") is not null)
        {
            return "sudo zypper install ffmpeg";
        }

        return null;
    }

    private SetupItem CheckDestinationDrives()
    {
        const string why = "Downloads are saved under these folders; a missing drive (e.g. an unplugged external disk) makes them fail.";
        var destinations = new List<(string Owner, string Path)> { ("the fallback folder", settings.FallbackDestination) };
        destinations.AddRange(settings.Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Destination))
            .Select(r => ($"rule \"{r.Name}\"", r.Destination)));

        var problems = new List<string>();
        foreach (var (owner, template) in destinations)
        {
            var expanded = PathTemplate.ExpandHome(template);
            var root = Path.IsPathFullyQualified(expanded) ? Path.GetPathRoot(expanded) : null;
            if (root is null)
            {
                problems.Add($"{owner} is not a full path");
            }
            else if (!Directory.Exists(root))
            {
                problems.Add($"{root} is not available (used by {owner})");
            }
        }

        return problems.Count == 0
            ? new SetupItem("Destination drives", SetupStatus.Ok, "All destination drives are available.", why)
            : new SetupItem("Destination drives", SetupStatus.Warning, string.Join("; ", problems.Distinct()) + ".", why);
    }

    private IEnumerable<SetupItem> CheckArchiveFolders()
    {
        const string why = "Presets that use --download-archive record finished downloads there; yt-dlp can't create the file if its folder is missing.";
        var any = false;
        var missing = new List<SetupItem>();
        foreach (var preset in settings.Presets)
        {
            if (ArgumentParser.GetOptionValue(ArgumentParser.Split(preset.Arguments), "--download-archive") is not { Length: > 0 } file)
            {
                continue;
            }

            any = true;
            var folder = Path.GetDirectoryName(Path.GetFullPath(PathTemplate.ExpandHome(file)));
            if (folder is not null && !Directory.Exists(folder))
            {
                missing.Add(new SetupItem($"Archive folder ({preset.Name})", SetupStatus.Warning, $"{folder} doesn't exist.", why)
                    { Fix = SetupFix.CreateFolder, Folder = folder });
            }
        }

        if (missing.Count > 0)
        {
            return missing.DistinctBy(m => m.Folder, StringComparer.OrdinalIgnoreCase);
        }

        return any ? [new SetupItem("Archive folders", SetupStatus.Ok, "Every preset's archive folder exists.", why)] : [];
    }
}
