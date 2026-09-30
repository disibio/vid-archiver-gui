namespace VidArchiverGui.Core.Services;

/// <summary>
/// Whether a newer version of the app itself has been released. Only ever tells the user: nothing is downloaded or
/// installed. Builds that something else keeps up to date (the Microsoft Store, Flatpak, the AUR package) never check.
/// </summary>
public static class AppUpdate
{
    public const string Repo = "disibio/vid-archiver-gui";

    /// <summary>
    /// A file with this name next to the program turns the check off for good, for packages whose package manager
    /// does the updating (see packaging/aur/PKGBUILD).
    /// </summary>
    public const string NoCheckMarker = "no-update-check.txt";

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>Whether something other than the user keeps this copy up to date.</summary>
    public static bool UpdatedElsewhere =>
        AppPaths.IsPackaged || AppPaths.IsFlatpak || File.Exists(Path.Combine(AppContext.BaseDirectory, NoCheckMarker));

    public static string ReleasePage(string version) => $"https://github.com/{Repo}/releases/tag/v{version}";

    /// <summary>Whether it's time to ask GitHub again.</summary>
    public static bool IsDue(bool enabled, bool updatedElsewhere, DateTimeOffset? lastCheck, DateTimeOffset now) =>
        enabled && !updatedElsewhere && (lastCheck is null || now - lastCheck >= CheckInterval || lastCheck > now);

    /// <summary>
    /// The released version to tell the user about ("1.2.0" for the tag "v1.2.0"), or null when it isn't newer than
    /// <paramref name="current"/> or is the one they chose to skip. Also null if this build's version is unknown, rather
    /// than offering every release.
    /// </summary>
    public static string? NewerVersion(string? latestTag, string current, string? skipped)
    {
        var latest = latestTag?.Trim().TrimStart('v', 'V');
        return latest is { Length: > 0 } && Version.TryParse(current, out _) && ToolManager.IsNewer(latest, current) && latest != skipped
            ? latest
            : null;
    }

    /// <summary>The newest published release's tag. Drafts and pre-releases aren't included.</summary>
    public static Task<string?> GetLatestTagAsync(CancellationToken ct = default) => ReleaseDownloader.GetLatestTagAsync(Repo, ct);
}
