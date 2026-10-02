namespace VidArchiverGui.Core.Models;

/// <summary>Where a download in the list is, from reading its info to done.</summary>
public enum DownloadState
{
    Resolving,
    Ready,
    Queued,
    Downloading,
    Completed,
    Skipped,
    Failed,
    Cancelled,

    /// <summary>Stopped by the user to carry on later; partly downloaded files are kept, and Start resumes them.</summary>
    Paused,
}
