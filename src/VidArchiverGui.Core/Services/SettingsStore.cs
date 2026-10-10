using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

public sealed class SettingsStore(string path)
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Keep quotes, <, + and non-ASCII readable so settings.json is pleasant to edit by hand.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath { get; } = path;

    /// <summary>Where <see cref="Load"/> kept a settings file it couldn't read, before starting with the defaults.</summary>
    public string? CorruptCopy { get; private set; }

    /// <summary>
    /// Claims the settings for this process, or returns null if another copy of the app already has. Two copies using
    /// the same settings would overwrite each other's changes and download the same unfinished downloads at once. Dispose
    /// the result to let go; the system also lets go when the process ends, even if it crashes.
    /// </summary>
    public IDisposable? TryLock()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            return new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e) when (IsInUse(e))
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Couldn't lock it for some other reason, such as a read-only folder: carry on rather than refuse to start
            // (saving will say what's wrong).
            return Stream.Null;
        }
    }

    /// <summary>
    /// Whether the file is locked by another process: a sharing or lock violation on Windows; elsewhere .NET reports
    /// the lock being taken (EWOULDBLOCK) with the error number as the HResult.
    /// </summary>
    private static bool IsInUse(IOException e) => (e.HResult & 0xFFFF) switch
    {
        32 or 33 => OperatingSystem.IsWindows(),
        11 => OperatingSystem.IsLinux(),
        35 => OperatingSystem.IsMacOS(),
        _ => false,
    };

    public AppSettings Load()
    {
        AppSettings? settings = null;
        if (File.Exists(FilePath))
        {
            try
            {
                var json = ReadWithRetry(FilePath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                _lastWritten = json; // so the first save only writes if loading changed something
            }
            catch (JsonException)
            {
                // Keep the unreadable file for the user to inspect rather than silently overwriting it.
                CorruptCopy = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(FilePath, CorruptCopy, overwrite: true);
            }
        }

        settings ??= CreateDefaults();
        Normalize(settings);
        return settings;
    }

    /// <summary>
    /// Sync tools and virus scanners briefly lock files. Falling back to defaults instead would overwrite the user's
    /// settings on the next save, so a file that stays unreadable is an error.
    /// </summary>
    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200);
            }
        }
    }

    // What was last written, so saving often (see AppHost) only touches the file when something changed.
    private string? _lastWritten;

    /// <summary>Writes the settings if they differ from what was last written. Returns whether it wrote.</summary>
    public bool SaveIfChanged(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, Options);
        if (json == _lastWritten)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        using (var file = File.Create(tmp))
        {
            file.Write(Encoding.UTF8.GetBytes(json));
            // On the disk before it replaces the old file, so a power cut leaves the old settings or the new ones,
            // never an empty file.
            file.Flush(flushToDisk: true);
        }

        File.Move(tmp, FilePath, overwrite: true);
        _lastWritten = json;
        return true;
    }

    public static AppSettings CreateDefaults()
    {
        // Per-user, per-OS locations: Videos on Windows/Linux, Movies on macOS.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appVideos = AppPaths.AppVideosFolder;
        var archiveFile = Path.Combine(appVideos, "archive.txt");

        var archive = new Preset
        {
            Name = "Archive (≤1440p MKV)",
            Arguments = $"""
                -f "bestvideo[height<=?1440]+bestaudio/best"
                --write-comments --write-thumbnail --write-link --write-description --write-info-json
                --embed-thumbnail --embed-metadata --embed-chapters
                # --write-subs keeps the subtitle file next to the video after embedding it (yt-dlp deletes it
                # otherwise) and prefers real subtitles over automatic ones.
                --write-subs --write-auto-sub --sub-lang en --embed-subs
                --download-archive "{archiveFile}"
                --merge-output-format mkv
                # Videos that come as one file (like .ogv or .mpg on archive.org) are repackaged into MKV too, so the
                # thumbnail can be embedded. Nothing is re-encoded; genpts fills in timestamps old MPEG files lack.
                --remux-video mkv --postprocessor-args "VideoRemuxer+ffmpeg_i:-fflags +genpts"
                --mtime
                """,
        };
        var best = new Preset
        {
            Name = "Best quality (MKV)",
            Arguments = """
                -f "bestvideo+bestaudio/best"
                --embed-metadata --embed-chapters --embed-thumbnail
                --merge-output-format mkv
                # Videos that come as one file (like .ogv or .mpg on archive.org) are repackaged into MKV too, so the
                # thumbnail can be embedded. Nothing is re-encoded; genpts fills in timestamps old MPEG files lack.
                --remux-video mkv --postprocessor-args "VideoRemuxer+ffmpeg_i:-fflags +genpts"
                """,
        };
        // H.264 + AAC in MP4 plays almost everywhere (QuickTime, iPhone, TVs, editors), unlike MKV; no re-encoding.
        var compatible = new Preset
        {
            Name = "Compatible (MP4)",
            Arguments = """
                -f "bv*[vcodec^=avc1]+ba[ext=m4a]/b[ext=mp4]/b"
                --embed-metadata --embed-chapters --embed-thumbnail
                --merge-output-format mp4
                """,
        };
        var audio = new Preset
        {
            Name = "Audio only (MP3)",
            Arguments = """
                -x --audio-format mp3 --audio-quality 0
                --embed-thumbnail --embed-metadata
                """,
        };

        // Saves the subtitles uploaded for videos already downloaded, in every language. Most videos have none, so it
        // usually finds nothing; automatic captions are left out, as YouTube offers machine translations into 100+
        // languages. No --download-archive, since it would skip every video that's already archived.
        var subtitles = new Preset
        {
            Name = "Uploaded subtitles only (all languages)",
            Arguments = """
                --skip-download --no-overwrites
                --write-subs --sub-langs "all,-live_chat"
                """,
        };

        var downloads = Path.Combine(appVideos, "{site}", "{channel|uploader|\"Unknown\"}");

        return new AppSettings
        {
            Presets = [archive, best, compatible, audio, subtitles],
            DefaultPresetId = archive.Id,
            FallbackDestination = downloads,
            Rules =
            [
                new RoutingRule
                {
                    Name = "Example: Archive.org → Music (audio)",
                    Enabled = false,
                    Destination = Path.Combine(MusicFolder(home), "Internet Archive", "{title}"),
                    PresetId = audio.Id,
                    Conditions = [new RuleCondition { Field = MatchField.Domain, Operator = MatchOperator.Equals, Value = "archive.org" }],
                },
            ],
        };
    }

    private static string MusicFolder(string home) =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) is { Length: > 0 } music ? music : Path.Combine(home, "Music");

    private static void Normalize(AppSettings s)
    {
        var defaults = CreateDefaults();
        if (s.Presets.Count == 0)
        {
            foreach (var p in defaults.Presets)
            {
                s.Presets.Add(p);
            }
        }

        if (string.IsNullOrWhiteSpace(s.FallbackDestination))
        {
            s.FallbackDestination = defaults.FallbackDestination;
        }

        s.MaxConcurrentDownloads = Math.Clamp(s.MaxConcurrentDownloads, 1, 10);

        // Built-ins always come first and their definitions come from code (so fixes to repos/names apply).
        var custom = s.Downloaders.Where(e => e.GitHubRepo is null && !string.IsNullOrWhiteSpace(e.ExecutablePath)).ToList();
        s.Downloaders.Clear();
        foreach (var e in Downloader.CreateBuiltIns().Concat(custom))
        {
            s.Downloaders.Add(e);
        }

        s.RemoveDanglingReferences();
    }
}
