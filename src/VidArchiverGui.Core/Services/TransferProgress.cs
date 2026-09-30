using System.Globalization;

namespace VidArchiverGui.Core.Services;

/// <summary>How far a tool download has got, for showing to the user.</summary>
public sealed record TransferProgress(string File, long Received, long? Total, double BytesPerSecond)
{
    /// <summary>Still connecting: nothing has been received yet.</summary>
    public bool Connecting { get; init; }

    /// <summary>0–1, or null when the server didn't say how big the file is.</summary>
    public double? Fraction => Total > 0 ? (double)Received / Total.Value : null;

    public override string ToString()
    {
        if (Connecting)
        {
            return $"Connecting to download {File}…";
        }

        var amount = Total > 0 ? $"{Megabytes(Received)} of {Megabytes(Total.Value)} MB" : $"{Megabytes(Received)} MB";
        var speed = BytesPerSecond > 0 ? $" ({Megabytes((long)BytesPerSecond)} MB/s)" : "";
        return $"Downloading {File}: {amount}{speed}";
    }

    private static string Megabytes(long bytes) => (bytes / 1_048_576.0).ToString("0.0", CultureInfo.CurrentCulture);
}
