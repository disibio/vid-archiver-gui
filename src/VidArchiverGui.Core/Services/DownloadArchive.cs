using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

/// <summary>The --download-archive files presets use to record finished downloads, so they aren't downloaded again.</summary>
public static class DownloadArchive
{
    private const string Option = "--download-archive";

    /// <summary>The folder a preset's --download-archive file goes in, or null if it doesn't use one.</summary>
    public static string? FolderOf(Preset preset) =>
        ArgumentParser.GetOptionValue(ArgumentParser.Split(preset.Arguments), Option) is { Length: > 0 } file
            ? Path.GetDirectoryName(Path.GetFullPath(PathTemplate.ExpandHome(file)))
            : null;

    /// <summary>
    /// Points every preset whose archive file is in <paramref name="oldFolder"/> at the same file name in
    /// <paramref name="newFolder"/>. Returns the presets that changed.
    /// </summary>
    public static IReadOnlyList<Preset> MoveFolder(IEnumerable<Preset> presets, string oldFolder, string newFolder)
    {
        var changed = new List<Preset>();
        foreach (var preset in presets)
        {
            if (!string.Equals(FolderOf(preset), oldFolder, StringComparison.OrdinalIgnoreCase)
                || ArgumentParser.GetOptionValue(ArgumentParser.Split(preset.Arguments), Option) is not { } file)
            {
                continue;
            }

            // Edit the path where it's written rather than re-joining the parsed arguments, so the preset keeps its
            // layout and # comments.
            var text = preset.Arguments;
            var at = text.IndexOf(file, Math.Max(0, text.LastIndexOf(Option, StringComparison.Ordinal)), StringComparison.Ordinal);
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
