using System.Globalization;

namespace VidArchiverGui.Core.Services;

/// <summary>Sizes and times as shown in a download's progress line.</summary>
public static class DisplayFormat
{
    /// <summary>e.g. "512 B", "12.3 MiB"; "?" when unknown.</summary>
    public static string Bytes(double? bytes)
    {
        if (bytes is not { } b || b < 0)
        {
            return "?";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return b.ToString(i == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[i];
    }

    /// <summary>e.g. "04:05", "1:02:03"; "--:--" when unknown.</summary>
    public static string Eta(double? seconds)
    {
        if (seconds is not { } s || s < 0)
        {
            return "--:--";
        }

        var t = TimeSpan.FromSeconds(Math.Round(s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}
