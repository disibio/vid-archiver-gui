using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.Services;

/// <summary>crash.log in the data folder: unexpected errors, for the user to look at or send with a bug report.</summary>
public static class CrashLog
{
    public const string FileName = "crash.log";

    /// <summary>Appends <paramref name="e"/>. Never throws: it's called while something has already gone wrong.</summary>
    public static void Write(Exception e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(Path.Combine(AppPaths.DataDir, FileName), $"[{DateTime.Now:O}] {e}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
