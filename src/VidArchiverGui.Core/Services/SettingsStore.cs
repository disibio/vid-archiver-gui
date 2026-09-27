using System.Text.Json;
using System.Text.Json.Serialization;
using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Keep quotes, <, + and non-ASCII readable so settings.json is pleasant to edit by hand.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    public AppSettings Load()
    {
        AppSettings? settings = null;
        if (File.Exists(Path))
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(ReadWithRetry(Path), Options);
            }
            catch (JsonException)
            {
                // Keep the unreadable file for the user to inspect rather than silently overwriting it.
                File.Copy(Path, Path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
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

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, Path, overwrite: true);
    }

    public static AppSettings CreateDefaults()
    {
        // Per-user, per-OS locations: Videos on Windows/Linux, Movies on macOS.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos))
        {
            videos = System.IO.Path.Combine(home, "Videos");
        }

        var archiveFile = System.IO.Path.Combine(videos, "Vid Archiver GUI", "archive.txt");

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

        var downloads = System.IO.Path.Combine(videos, "Vid Archiver GUI", "{site}", "{channel|uploader|\"Unknown\"}");

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
                    Destination = System.IO.Path.Combine(MusicFolder(home), "Internet Archive", "{title}"),
                    PresetId = audio.Id,
                    Conditions = [new RuleCondition { Field = MatchField.Domain, Operator = MatchOperator.Equals, Value = "archive.org" }],
                },
            ],
        };
    }

    private static string MusicFolder(string home) =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) is { Length: > 0 } music ? music : System.IO.Path.Combine(home, "Music");

    private static void Normalize(AppSettings s)
    {
        if (s.Presets.Count == 0)
        {
            foreach (var p in CreateDefaults().Presets)
            {
                s.Presets.Add(p);
            }
        }

        if (s.FindPreset(s.DefaultPresetId) is null)
        {
            s.DefaultPresetId = s.Presets[0].Id;
        }

        if (string.IsNullOrWhiteSpace(s.FallbackDestination))
        {
            s.FallbackDestination = CreateDefaults().FallbackDestination;
        }

        s.MaxConcurrentDownloads = Math.Clamp(s.MaxConcurrentDownloads, 1, 10);
        NormalizeEngines(s);
        NormalizeCookies(s);
    }

    private static void NormalizeCookies(AppSettings s)
    {
        // Choices pointing at a removed cookie source fall back to "no cookies" / "keep".
        if (!Cookies.Exists(s, s.LastCookieId))
        {
            s.LastCookieId = null;
        }

        foreach (var rule in s.Rules.Where(r => !Cookies.Exists(s, r.CookieId)))
        {
            rule.CookieId = null;
        }
    }

    private static void NormalizeEngines(AppSettings s)
    {
        // Built-ins always come first and their definitions come from code (so fixes to repos/names apply).
        var custom = s.Engines.Where(e => e.GitHubRepo is null && !string.IsNullOrWhiteSpace(e.ExecutablePath)).ToList();
        s.Engines.Clear();
        foreach (var e in Engine.CreateBuiltIns().Concat(custom))
        {
            s.Engines.Add(e);
        }

        if (s.FindEngine(s.DefaultEngineId) is null)
        {
            s.DefaultEngineId = Engine.StableId;
        }

        foreach (var p in s.Presets.Where(p => p.EngineId is not null && s.FindEngine(p.EngineId) is null))
        {
            p.EngineId = null;
        }
    }
}
