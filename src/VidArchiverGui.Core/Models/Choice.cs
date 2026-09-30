namespace VidArchiverGui.Core.Models;

/// <summary>
/// An entry in a drop-down that stores an id (a preset, downloader or cookie source). <see cref="Id"/> null means
/// "keep what was chosen" or "use the default", depending on the list.
/// </summary>
public sealed record Choice(string? Id, string Name);
