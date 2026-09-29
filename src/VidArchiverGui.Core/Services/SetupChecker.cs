using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

public enum SetupStatus
{
    Ok,
    Warning,
    Missing,
    /// <summary>Not known yet: the check is still running (or waiting for something else to finish).</summary>
    Checking,
}

public enum SetupFix
{
    None,
    InstallDownloader,
    InstallDeno,
    InstallFfmpeg,
    /// <summary>A folder is missing: create it, or choose another one instead.</summary>
    MissingFolder,
    CopyCommand,
}

/// <summary>One requirement on the setup checklist.</summary>
public sealed record SetupItem(string Name, SetupStatus Status, string Detail, string Why)
{
    public SetupFix Fix { get; init; } = SetupFix.None;

    /// <summary>Shell command for <see cref="SetupFix.CopyCommand"/>.</summary>
    public string? Command { get; init; }

    /// <summary>Folder for <see cref="SetupFix.MissingFolder"/>.</summary>
    public string? Folder { get; init; }

    /// <summary>The missing folder's drive is there, so the folder can be created (not so for an unplugged disk).</summary>
    public bool CanCreateFolder { get; init; }

    /// <summary>The missing folder is one the app chose itself (under its own Videos folder), not the user.</summary>
    public bool IsAppFolder { get; init; }

    // Missing folders the user chose are left to them (create it, or pick another); only the app's own is created automatically.
    public bool CanAutoFix => Fix is SetupFix.InstallDownloader or SetupFix.InstallDeno or SetupFix.InstallFfmpeg
        || Fix == SetupFix.MissingFolder && IsAppFolder && CanCreateFolder;
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

    /// <summary>The checklist's sections in display order, with the row name shown while each is still being checked.</summary>
    public IReadOnlyList<(string Section, string Name)> Plan()
    {
        List<(string, string)> plan = [(DownloaderSection, settings.DefaultEngine.Name), (FfmpegSection, "ffmpeg")];
        if (settings.DefaultEngine.Flavor == EngineFlavor.YtDlp)
        {
            plan.Add((DenoSection, DenoName));
        }

        plan.Add((DrivesSection, DrivesName));
        plan.Add((ArchiveSection, "Archive folders"));
        return plan;
    }

    public const string DownloaderSection = "downloader", FfmpegSection = "ffmpeg", DenoSection = "deno", DrivesSection = "drives", ArchiveSection = "archive";
    private const string DenoName = "deno (JavaScript runtime)", DrivesName = "Destination drives";

    /// <summary>
    /// Runs the checks. <paramref name="activity"/> says what a section is doing right now (e.g. which program it's
    /// waiting on); <paramref name="done"/> delivers each section's rows as soon as they're known.
    /// </summary>
    public async Task<IReadOnlyList<SetupItem>> CheckAsync(
        Action<string, string>? activity = null, Action<string, IReadOnlyList<SetupItem>>? done = null, CancellationToken ct = default)
    {
        var items = new List<SetupItem>();
        void Done(string section, params SetupItem[] rows)
        {
            items.AddRange(rows);
            done?.Invoke(section, rows);
        }

        var engine = settings.DefaultEngine;
        var path = ToolManager.LocatePath(engine);
        var exeName = Path.GetFileName(path ?? engine.ExecutablePath ?? "yt-dlp");

        // 1. The downloader itself.
        if (path is null)
        {
            Done(DownloaderSection, new SetupItem(engine.Name, SetupStatus.Missing,
                engine.IsManaged ? "Not installed yet." : $"Executable not found: {engine.ExecutablePath}. Fix it under Downloaders below.",
                DownloaderWhy) { Fix = engine.IsManaged ? SetupFix.InstallDownloader : SetupFix.None });
        }
        else
        {
            activity?.Invoke(DownloaderSection, $"Running \"{exeName} --version\"…");
            var version = await tools.GetVersionAsync(path, ct);
            Done(DownloaderSection, version is not null
                ? new SetupItem(engine.Name, SetupStatus.Ok, $"{version}  —  {path}", DownloaderWhy)
                : new SetupItem(engine.Name, SetupStatus.Warning,
                    $"Found at {path}, but \"{exeName} --version\" failed or didn't answer within {ToolManager.QueryTimeout.TotalSeconds:0} s.", DownloaderWhy));
        }

        // Ask yt-dlp what it can actually see (it's the final word on ffmpeg and the JS runtime).
        YtDlpProbe? probe = null;
        if (path is not null && engine.Flavor == EngineFlavor.YtDlp)
        {
            var asking = $"Asking yt-dlp which helper tools it can find (\"{exeName} -v\")…";
            activity?.Invoke(FfmpegSection, asking);
            activity?.Invoke(DenoSection, asking);
            probe = await ProbeAsync(tools.Resolve(engine), ct);
        }

        // 2. ffmpeg.
        var ffmpeg = tools.ResolveFfmpeg();
        Done(FfmpegSection, probe?.HasFfmpeg ?? ffmpeg is not null
            ? new SetupItem("ffmpeg", SetupStatus.Ok, ffmpeg ?? "found by the downloader", FfmpegWhy)
            : FfmpegMissing());

        // 3. deno (yt-dlp only; youtube-dl doesn't use one).
        if (engine.Flavor == EngineFlavor.YtDlp)
        {
            var deno = ToolManager.ResolveDeno();
            var rows = new List<SetupItem>();
            if (probe?.HasJsRuntime ?? deno is not null)
            {
                rows.Add(new SetupItem(DenoName, SetupStatus.Ok,
                    $"{probe?.JsRuntimes ?? "deno"}  —  {deno ?? "found by the downloader"}", DenoWhy));
            }
            else
            {
                rows.Add(new SetupItem(DenoName, SetupStatus.Missing,
                    ToolManager.HasOtherJsRuntime ? "Node.js/Bun is installed, but yt-dlp only uses deno by default." : "Not found.",
                    DenoWhy) { Fix = SetupFix.InstallDeno });
            }

            if (probe?.HasEjs == false)
            {
                rows.Add(new SetupItem("yt-dlp JavaScript components (yt-dlp-ejs)", SetupStatus.Warning,
                    "This yt-dlp build doesn't include yt-dlp-ejs. Use an app-installed yt-dlp, or reinstall it with the command shown.",
                    DenoWhy) { Fix = SetupFix.CopyCommand, Command = "pip install -U \"yt-dlp[default]\"" });
            }

            Done(DenoSection, [.. rows]);
        }

        // 4. Destination drives (e.g. an external E: drive that isn't plugged in).
        activity?.Invoke(DrivesSection, "Looking for the destination drives…");
        Done(DrivesSection, CheckDestinationDrives());

        // 5. Folders for --download-archive files.
        activity?.Invoke(ArchiveSection, "Looking for the archive folders…");
        Done(ArchiveSection, [.. CheckArchiveFolders()]);

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
        timeout.CancelAfter(ToolManager.QueryTimeout);
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
            ? new SetupItem(DrivesName, SetupStatus.Ok, "All destination drives are available.", why)
            : new SetupItem(DrivesName, SetupStatus.Warning, string.Join("; ", problems.Distinct()) + ".", why);
    }

    private IEnumerable<SetupItem> CheckArchiveFolders()
    {
        const string why = "Presets that use --download-archive record finished downloads there; yt-dlp can't create the file if its folder is missing.";
        var any = false;
        var missing = new List<SetupItem>();
        foreach (var preset in settings.Presets)
        {
            if (ArchiveFolderOf(preset) is not { } folder)
            {
                continue;
            }

            any = true;
            if (!Directory.Exists(folder))
            {
                var root = Path.GetPathRoot(folder);
                var canCreate = root is { Length: > 0 } && Directory.Exists(root);
                missing.Add(new SetupItem($"Archive folder ({preset.Name})", SetupStatus.Warning,
                    canCreate ? $"{folder} doesn't exist." : $"{folder} is on {root}, which isn't available. Plug the drive in, or choose another folder.",
                    why)
                    { Fix = SetupFix.MissingFolder, Folder = folder, CanCreateFolder = canCreate, IsAppFolder = IsUnder(folder, SettingsStore.AppVideosFolder) });
            }
        }

        if (missing.Count > 0)
        {
            return missing.DistinctBy(m => m.Folder, StringComparer.OrdinalIgnoreCase);
        }

        return any ? [new SetupItem("Archive folders", SetupStatus.Ok, "Every preset's archive folder exists.", why)] : [];
    }

    private const string ArchiveOption = "--download-archive";

    public static bool IsUnder(string path, string folder)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folder a preset's --download-archive file goes in, or null if it doesn't use one.</summary>
    public static string? ArchiveFolderOf(Preset preset) =>
        ArgumentParser.GetOptionValue(ArgumentParser.Split(preset.Arguments), ArchiveOption) is { Length: > 0 } file
            ? Path.GetDirectoryName(Path.GetFullPath(PathTemplate.ExpandHome(file)))
            : null;

    /// <summary>
    /// Points every preset whose archive file is in <paramref name="oldFolder"/> at the same file name in
    /// <paramref name="newFolder"/>. Returns the presets that changed.
    /// </summary>
    public static IReadOnlyList<Preset> MoveArchiveFolder(IEnumerable<Preset> presets, string oldFolder, string newFolder)
    {
        var changed = new List<Preset>();
        foreach (var preset in presets)
        {
            if (!string.Equals(ArchiveFolderOf(preset), oldFolder, StringComparison.OrdinalIgnoreCase)
                || ArgumentParser.GetOptionValue(ArgumentParser.Split(preset.Arguments), ArchiveOption) is not { } file)
            {
                continue;
            }

            var text = preset.Arguments;
            var at = text.IndexOf(file, Math.Max(0, text.LastIndexOf(ArchiveOption, StringComparison.Ordinal)), StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var replacement = Path.Combine(newFolder, Path.GetFileName(file));
            var quoted = at > 0 && text[at - 1] is '"' or '\'';
            if (!quoted && replacement.Any(char.IsWhiteSpace))
            {
                replacement = $"\"{replacement}\"";
            }

            preset.Arguments = text[..at] + replacement + text[(at + file.Length)..];
            changed.Add(preset);
        }

        return changed;
    }
}
