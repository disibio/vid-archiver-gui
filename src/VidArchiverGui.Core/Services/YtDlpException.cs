namespace VidArchiverGui.Core.Services;

/// <summary>The downloader can't be run or reported an error; the message is written to be shown to the user.</summary>
public sealed class YtDlpException(string message) : Exception(message);
