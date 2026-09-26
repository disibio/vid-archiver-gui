using System.Text;

namespace VidArchiverGui.Core.Services;

public static class ArgumentParser
{
    /// <summary>
    /// Splits a command-line style string into arguments. Double and single quotes group text; backslashes are
    /// literal (so Windows paths work unescaped). Lines starting with # are comments. A leading "yt-dlp" token
    /// is dropped so a whole pasted command works.
    /// </summary>
    public static List<string> Split(string? text)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return args;
        }

        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (quote is null && rawLine.TrimStart().StartsWith('#'))
            {
                continue;
            }

            foreach (var c in rawLine)
            {
                if (quote is { } q)
                {
                    if (c == q)
                    {
                        quote = null;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                    inToken = true;
                }
                else if (char.IsWhiteSpace(c))
                {
                    Flush();
                }
                else
                {
                    current.Append(c);
                    inToken = true;
                }
            }

            if (quote is not null)
            {
                current.Append('\n');
            }
            else
            {
                Flush();
            }
        }
        if (quote is not null && current.Length > 0 && current[^1] == '\n')
        {
            current.Length--; // unterminated quote: don't keep the newline we added after the last line
        }

        Flush();

        if (args.Count > 0 && Path.GetFileNameWithoutExtension(args[0]).Equals("yt-dlp", StringComparison.OrdinalIgnoreCase))
        {
            args.RemoveAt(0);
        }

        return args;

        void Flush()
        {
            if (!inToken)
            {
                return;
            }

            args.Add(current.ToString());
            current.Clear();
            inToken = false;
        }
    }

    // Options that affect how a site is accessed. These are forwarded to the metadata lookup so that
    // routing sees the same page the download will (logins, cookies, proxies, ...).
    private static readonly HashSet<string> AccessOptionsWithValue = new(StringComparer.Ordinal)
    {
        "--cookies", "--cookies-from-browser", "--proxy", "--geo-verification-proxy", "--xff",
        "-u", "--username", "-p", "--password", "--netrc-location", "--netrc-cmd", "--video-password",
        "--user-agent", "--referer", "--add-header", "--extractor-args", "--impersonate", "--source-address",
        "--client-certificate", "--client-certificate-key", "--client-certificate-password", "--config-locations",
    };

    private static readonly HashSet<string> AccessFlags = new(StringComparer.Ordinal)
    {
        "-n", "--netrc", "-4", "--force-ipv4", "-6", "--force-ipv6", "--no-check-certificates", "--legacy-server-connect",
        "--ignore-config", "--no-config",
    };

    /// <summary>Value of the last occurrence of an option, in either "--opt value" or "--opt=value" form.</summary>
    public static string? GetOptionValue(IReadOnlyList<string> args, string option)
    {
        string? value = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == option && i + 1 < args.Count)
            {
                value = args[++i];
            }
            else if (args[i].StartsWith(option + "=", StringComparison.Ordinal))
            {
                value = args[i][(option.Length + 1)..];
            }
        }
        return value;
    }

    private static readonly string[] CookieOptions = ["--cookies", "--cookies-from-browser"];

    /// <summary>
    /// The preset's arguments with the cookie options chosen in the app (if any) in place of the preset's own
    /// --cookies/--cookies-from-browser/--no-cookies*, so the two are never merged.
    /// </summary>
    public static IReadOnlyList<string> WithCookies(IReadOnlyList<string> presetArgs, IReadOnlyList<string> cookieArgs) =>
        cookieArgs.Count > 0 ? [.. RemoveCookieOptions(presetArgs), .. cookieArgs] : presetArgs;

    private static List<string> RemoveCookieOptions(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--no-cookies" or "--no-cookies-from-browser")
            {
                continue;
            }

            if (CookieOptions.Contains(a))
            {
                i++; // skip the value too
                continue;
            }
            if (CookieOptions.Any(o => a.StartsWith(o + "=", StringComparison.Ordinal)))
            {
                continue;
            }

            result.Add(a);
        }
        return result;
    }

    public static bool HasCookieOptions(IReadOnlyList<string> args) =>
        args.Any(a => CookieOptions.Any(o => a == o || a.StartsWith(o + "=", StringComparison.Ordinal)));

    public static List<string> ExtractAccessOptions(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var name = a.Split('=', 2)[0];
            if (AccessFlags.Contains(a))
            {
                result.Add(a);
            }
            else if (AccessOptionsWithValue.Contains(name))
            {
                result.Add(a);
                if (!a.Contains('=') && i + 1 < args.Count)
                {
                    result.Add(args[++i]);
                }
            }
        }
        return result;
    }
}
