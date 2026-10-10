// A stand-in for yt-dlp, so the download list can be tested (by the App tests, or by hand as a custom downloader)
// without the network. It understands just enough of yt-dlp's options and prints the same kind of output.
//
// Links: https://fake.test/video/<name> is one video, https://fake.test/playlist/<count> is a playlist of that many.
// Add ?fail to make the download fail, or set FAKEYTDLP_INFO_FAIL=1 to make reading the info (-J) fail. With
// ?members, reading the info fails unless cookies are given.
// FAKEYTDLP_INFO_MS makes reading the info take that long.
// FAKEYTDLP_STEP_MS sets the pause between progress lines (default 20 ms). With --write-thumbnail, a 1x1 PNG is
// written beside each video, or a copy of the image FAKEYTDLP_THUMBNAIL names.
//
// Each video is downloaded as two streams (video, then audio) that are merged, like yt-dlp does with separate formats.
// Streams grow a .part file a chunk at a time, and a later run carries on from it, so stopping and starting resumes.

using System.Text;
using System.Text.Json;

const long StreamSize = 1_000_000;
const long Chunk = 100_000;

Console.OutputEncoding = Encoding.UTF8;
var stepMs = int.TryParse(Environment.GetEnvironmentVariable("FAKEYTDLP_STEP_MS"), out var ms) ? ms : 20;

var separator = Array.IndexOf(args, "--");
var options = separator < 0 ? args : args[..separator];
var url = separator >= 0 && separator + 1 < args.Length ? args[separator + 1] : null;

if (options.Contains("--version"))
{
    Console.WriteLine("2026.01.01");
    return 0;
}

if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "fake.test")
{
    Console.Error.WriteLine($"ERROR: Unsupported URL: {url}");
    return 1;
}

var segments = uri.AbsolutePath.Trim('/').Split('/');
var isPlaylist = segments[0] == "playlist";
var name = segments.Length > 1 ? Uri.UnescapeDataString(segments[1]) : "video";
var count = isPlaylist && int.TryParse(name, out var n) ? n : 1;
var titles = isPlaylist ? Enumerable.Range(1, count).Select(i => $"Fake video {i}").ToList() : [name];

if (options.Contains("-J"))
{
    if (int.TryParse(Environment.GetEnvironmentVariable("FAKEYTDLP_INFO_MS"), out var infoMs))
    {
        Thread.Sleep(infoMs);
    }

    if (uri.Query.Contains("members") && !options.Contains("--cookies") && !options.Contains("--cookies-from-browser"))
    {
        Console.Error.WriteLine("ERROR: [fake] " + name + ": Join this channel to get access to members-only content");
        return 1;
    }

    if (Environment.GetEnvironmentVariable("FAKEYTDLP_INFO_FAIL") == "1")
    {
        Console.Error.WriteLine("ERROR: [fake] " + name + ": This video is private");
        return 1;
    }

    object info = isPlaylist
        ? new Dictionary<string, object>
        {
            ["_type"] = "playlist", ["id"] = "fakelist", ["title"] = "Fake playlist", ["playlist_count"] = count,
            ["extractor_key"] = "Fake", ["extractor"] = "fake", ["webpage_url"] = url,
            ["entries"] = new[] { new Dictionary<string, object> { ["_type"] = "url", ["id"] = "v1", ["channel"] = "Fake channel" } },
        }
        : new Dictionary<string, object>
        {
            ["id"] = name, ["title"] = name, ["channel"] = "Fake channel", ["channel_id"] = "fake-channel",
            ["extractor_key"] = "Fake", ["extractor"] = "fake", ["webpage_url"] = url,
        };
    Console.WriteLine(JsonSerializer.Serialize(info));
    return 0;
}

if (uri.Query.Contains("fail"))
{
    Console.WriteLine("[fake] Extracting URL: " + url);
    Console.Error.WriteLine("ERROR: [fake] " + name + ": This video is private");
    return 1;
}

var folder = Value("-P") ?? Directory.GetCurrentDirectory();
var template = options.Contains("--progress-template");
var writeThumbnail = options.Contains("--write-thumbnail");
var printToFile = Pairs("--print-to-file").Where(p => p.Template == "after_move:filepath").Select(p => p.File).ToList();
Directory.CreateDirectory(folder);

for (var i = 0; i < titles.Count; i++)
{
    if (isPlaylist)
    {
        Console.WriteLine($"[download] Downloading item {i + 1} of {titles.Count}");
    }

    var final = Path.Combine(folder, titles[i] + ".mkv");
    if (File.Exists(final))
    {
        Console.WriteLine($"[download] {final} has already been downloaded");
    }
    else
    {
        var video = Download(Path.Combine(folder, titles[i] + ".f1.mp4"));
        var audio = Download(Path.Combine(folder, titles[i] + ".f2.m4a"));
        Console.WriteLine($"[Merger] Merging formats into \"{final}\"");
        Thread.Sleep(stepMs);
        File.WriteAllBytes(final, new byte[1024]);
        File.Delete(video);
        File.Delete(audio);
    }

    if (writeThumbnail)
    {
        if (Environment.GetEnvironmentVariable("FAKEYTDLP_THUMBNAIL") is { Length: > 0 } image)
        {
            File.Copy(image, Path.ChangeExtension(final, Path.GetExtension(image)), overwrite: true);
        }
        else
        {
            File.WriteAllBytes(Path.ChangeExtension(final, ".png"), TinyPng);
        }
    }

    foreach (var file in printToFile)
    {
        File.AppendAllText(file, final + Environment.NewLine);
    }
}

return 0;

string Download(string path)
{
    Console.WriteLine("[download] Destination: " + path);
    var part = path + ".part";
    var done = File.Exists(part) ? new FileInfo(part).Length : 0;
    if (done > 0)
    {
        Console.WriteLine($"[download] Resuming download at byte {done}");
    }

    using (var stream = new FileStream(part, FileMode.Append))
    {
        while (done < StreamSize)
        {
            Thread.Sleep(stepMs);
            stream.Write(new byte[Chunk]);
            stream.Flush();
            done += Chunk;
            Console.WriteLine(template
                ? FormattableString.Invariant($"[vidarchivergui-progress] {done}|{StreamSize}|NA|{Chunk * 10}|{(StreamSize - done) / (Chunk * 10)}")
                : FormattableString.Invariant($"[download] {100.0 * done / StreamSize:0.0}% of 976.56KiB at 976.56KiB/s ETA 00:01"));
        }
    }

    File.Move(part, path, overwrite: true);
    return path;
}

string? Value(string option)
{
    var i = Array.LastIndexOf(options, option);
    return i >= 0 && i + 1 < options.Length ? options[i + 1] : null;
}

IEnumerable<(string Template, string File)> Pairs(string option)
{
    for (var i = 0; i + 2 < options.Length; i++)
    {
        if (options[i] == option)
        {
            yield return (options[i + 1], options[i + 2]);
        }
    }
}

public static partial class Program
{
    /// <summary>A 1×1 PNG, standing in for a video's thumbnail.</summary>
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}
