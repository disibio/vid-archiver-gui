using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace VidArchiverGui.Core.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessHelper
{
    public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        // Child tools (yt-dlp looking for deno/ffmpeg) must search the same places we do.
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, SearchPath());
        if (AppPaths.IsFlatpak)
        {
            // yt-dlp's --cookies-from-browser finds Chrome-family profiles through XDG_CONFIG_HOME, which the sandbox
            // points at its own folder.
            psi.Environment["XDG_CONFIG_HOME"] = AppPaths.UserConfigDir;
        }
        return psi;
    }

    /// <summary>
    /// PATH plus the usual install locations on macOS/Linux. Apps started from Finder or a desktop launcher don't
    /// inherit the shell's PATH, so Homebrew (/opt/homebrew/bin) and ~/.local/bin would otherwise be invisible.
    /// </summary>
    public static IReadOnlyList<string> SearchPath()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim('"'))
            .ToList();
        if (!OperatingSystem.IsWindows())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin",
                Path.Combine(home, ".local", "bin"), Path.Combine(home, ".deno", "bin")]);
        }
        return dirs.Distinct().ToList();
    }

    /// <summary>The first <paramref name="name"/> executable in <see cref="SearchPath"/>, or null.</summary>
    public static string? FindOnPath(string name)
    {
        foreach (var dir in SearchPath())
        {
            var candidate = Path.Combine(dir, AppPaths.ExeName(name));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Runs a process to completion, killing its whole tree if cancelled.</summary>
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(exe, args) };
        process.Start();
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using (KillOnCancel(process, ct))
        {
            await process.WaitForExitAsync(ct);
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Kills the process tree the moment <paramref name="ct"/> is cancelled, synchronously inside Cancel(). Killing in
    /// a continuation instead would never happen when the app is closing, leaving yt-dlp/ffmpeg running.
    /// </summary>
    public static CancellationTokenRegistration KillOnCancel(Process process, CancellationToken ct) =>
        ct.Register(() => TryKill(process));

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
