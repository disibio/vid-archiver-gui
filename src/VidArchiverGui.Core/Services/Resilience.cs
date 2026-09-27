using VidArchiverGui.Core.Models;

namespace VidArchiverGui.Core.Services;

public enum FailureKind
{
    Unknown,
    /// <summary>Timeouts, dropped connections, 5xx: worth retrying the same downloader after a pause.</summary>
    Network,
    /// <summary>The site is limiting this IP/account. Retrying right away makes it worse.</summary>
    RateLimited,
    /// <summary>Bot check, age gate, members-only...: needs cookies.</summary>
    NeedsLogin,
    /// <summary>The chosen browser's cookies couldn't be read.</summary>
    CookiesUnreadable,
    /// <summary>Private, removed, geo-blocked, not yet live, unsupported URL: no downloader will help.</summary>
    Unavailable,
    /// <summary>Extraction broke, usually because the site changed; another downloader (e.g. nightly) may already handle it.</summary>
    SiteChanged,
}

/// <summary>The outcome of <see cref="Resilience.RunAsync"/>. <see cref="Error"/> is null on success.</summary>
public sealed record FallbackResult(Engine Engine, string? Error, FailureKind Kind);

/// <summary>Classifies downloader errors and retries them the way that has a chance of working.</summary>
public static class Resilience
{
    /// <summary>Pauses before retrying a network error with the same downloader.</summary>
    public static readonly TimeSpan[] NetworkRetryDelays = [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

    // Checked in this order; the first list with a match decides. Messages are yt-dlp's (and youtube-dl's) error texts.
    private static readonly (FailureKind Kind, string[] Patterns)[] Patterns =
    [
        (FailureKind.CookiesUnreadable, ["cookie database", "cookies database", "failed to decrypt", "could not decrypt",
            "failed to load cookies", "unsupported browser", "cookies from browser", "keyring"]),
        (FailureKind.RateLimited, ["http error 429", "too many requests", "rate-limit", "rate limit", "try again later"]),
        (FailureKind.Unavailable, ["private video", "video unavailable", "has been removed", "has been terminated", "no longer available",
            "not available in your country", "geo restrict", "geo-restrict", "unsupported url", "http error 404", "http error 410",
            "this live event will begin", "premieres in", "does not exist", "is not a valid url", "copyright"]),
        (FailureKind.NeedsLogin, ["sign in", "not a bot", "login required", "log in", "requires authentication", "members-only",
            "members only", "join this channel", "age-restricted", "age restricted", "inappropriate for some users", "--cookies",
            "premium"]),
        (FailureKind.Network, ["timed out", "timeout", "connection reset", "connection refused", "connection aborted",
            "remote end closed", "getaddrinfo", "name resolution", "network is unreachable", "incompleteread", "incomplete read",
            "http error 500", "http error 502", "http error 503", "http error 504", "unable to connect", "eof occurred",
            "ssl:", "temporary failure", "connectionerror"]),
        (FailureKind.SiteChanged, ["unable to extract", "nsig", "signature", "please report this issue", "requested format is not available",
            "no video formats found", "only images are available", "failed to parse", "jsondecodeerror", "keyerror", "attributeerror",
            "typeerror", "indexerror", "po token", "challenge"]),
    ];

    public static FailureKind Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return FailureKind.Unknown;
        }

        var m = message.Replace('’', '\''); // YouTube writes "you’re"
        foreach (var (kind, patterns) in Patterns)
        {
            if (patterns.Any(p => m.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                return kind;
            }
        }
        // Anything else raised by an extractor ("[youtube] abc: ...") is most likely the site having changed.
        return m.TrimStart().StartsWith('[') && !m.Contains("postprocess", StringComparison.OrdinalIgnoreCase)
            ? FailureKind.SiteChanged
            : FailureKind.Unknown;
    }

    /// <summary>What to do next, shown under the error. Null when the message says it all.</summary>
    public static string? Advice(FailureKind kind, bool usedCookies) => kind switch
    {
        FailureKind.NeedsLogin when !usedCookies =>
            "The site wants a login. Pick cookies for this item (e.g. Firefox, logged in to the site) and press Start.",
        FailureKind.NeedsLogin =>
            "Still asked to sign in with these cookies. Log in to the site in that browser again (or export a fresh cookies.txt) and retry.",
        FailureKind.CookiesUnreadable =>
            "Couldn't read the browser's cookies. Close that browser and retry, use Firefox, or export a cookies.txt (Settings → Cookies).",
        FailureKind.RateLimited =>
            "The site is limiting requests from you. Wait a while (it can take an hour), turn on Download gently, and use 1 simultaneous download.",
        FailureKind.SiteChanged =>
            "The site may have changed. Try again after the downloaders update, or add a newer downloader on the Settings tab.",
        _ => null,
    };

    /// <summary>
    /// Other downloaders worth trying when <paramref name="primary"/> can't handle a site: same flavor (presets are
    /// written for one flavor), in the Settings list order, so stable → nightly → master → custom forks.
    /// App-managed ones are included even if not installed yet; <see cref="RunAsync"/> installs them on demand.
    /// </summary>
    public static IReadOnlyList<Engine> FallbackEngines(AppSettings settings, Engine primary) =>
        settings.Engines
            .Where(e => e.Id != primary.Id && e.Flavor == primary.Flavor && (e.IsManaged || ToolManager.LocatePath(e) is not null))
            .ToList();

    /// <summary>
    /// Runs <paramref name="attempt"/> (which returns null on success, otherwise the error message) with
    /// <paramref name="primary"/>, retrying network errors after a pause and, when <see cref="AppSettings.AutoFallback"/>
    /// is on, moving to the next of the <see cref="FallbackEngines"/> when extraction is broken.
    /// </summary>
    public static async Task<FallbackResult> RunAsync(
        ToolManager tools, AppSettings settings, Engine primary,
        Func<ResolvedEngine, CancellationToken, Task<string?>> attempt, Action<string> status, CancellationToken ct,
        IReadOnlyList<TimeSpan>? networkRetryDelays = null)
    {
        networkRetryDelays ??= NetworkRetryDelays;
        List<Engine> engines = settings.AutoFallback ? [primary, .. FallbackEngines(settings, primary)] : [primary];
        FallbackResult? last = null;
        var tried = new List<string>();

        foreach (var engine in engines)
        {
            ResolvedEngine resolved;
            if (engine == primary)
            {
                resolved = tools.Resolve(engine); // a missing primary is reported as-is
            }
            else
            {
                try
                {
                    if (engine.IsManaged && !ToolManager.IsInstalledByApp(engine) && ToolManager.LocatePath(engine) is null)
                    {
                        status($"Installing {engine.Name} to try it…");
                        await tools.EnsureInstalledAsync(engine, ct,
                            new Progress<TransferProgress>(p => status($"Installing {engine.Name} to try it: {p}")));
                    }
                    resolved = tools.Resolve(engine);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    status($"Skipping {engine.Name}: {e.Message}");
                    continue;
                }
                status($"{string.Join(", ", tried)} couldn't handle this; trying {engine.Name}…");
            }
            tried.Add(engine.Name);

            for (var retry = 0; ; retry++)
            {
                var error = await attempt(resolved, ct);
                if (error is null)
                {
                    return new FallbackResult(engine, null, FailureKind.Unknown);
                }

                var kind = Classify(error);
                last = new FallbackResult(engine, error, kind);
                if (kind != FailureKind.Network || retry >= networkRetryDelays.Count)
                {
                    break;
                }

                var delay = networkRetryDelays[retry];
                status($"Network problem ({error}); retrying in {delay.TotalSeconds:0} s…");
                await Task.Delay(delay, ct);
            }

            if (last.Kind != FailureKind.SiteChanged)
            {
                return last;
            }
        }

        return last! with
        {
            Error = tried.Count > 1 ? $"{last!.Error} (tried {string.Join(", ", tried)})" : last!.Error,
        };
    }
}
