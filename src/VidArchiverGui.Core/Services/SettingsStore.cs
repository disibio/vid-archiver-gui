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
                File.Copy(FilePath, FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
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
        File.WriteAllText(tmp, json);
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
                --write-auto-sub --sub-lang en --embed-subs
                --download-archive "{archiveFile}"
                --merge-output-format mkv
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

        var downloads = Path.Combine(appVideos, "{site}", "{channel|uploader|\"Unknown\"}");

        return new AppSettings
        {
            Presets = [archive, best, compatible, audio],
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
