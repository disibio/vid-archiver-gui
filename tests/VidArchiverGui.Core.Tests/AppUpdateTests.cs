using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

/// <summary>Deciding when to look for a new version of the app, and whether the latest release is one to mention.</summary>
public sealed class AppUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("v1.2.0", "1.1.0", null, "1.2.0")]
    [InlineData("1.2.0", "1.1.0", null, "1.2.0")]
    [InlineData("v1.1.0", "1.1.0", null, null)] // the same version
    [InlineData("v1.0.1", "1.1.0", null, null)] // older than this build
    [InlineData("v1.2.0", "1.1.0", "1.2.0", null)] // skipped by the user
    [InlineData("v1.3.0", "1.1.0", "1.2.0", "1.3.0")] // a later one than the skipped one
    [InlineData("v1.2.0", "?", null, null)] // this build's version is unknown
    [InlineData("nightly", "1.1.0", null, null)]
    [InlineData(null, "1.1.0", null, null)]
    public void Only_a_newer_release_that_wasnt_skipped_is_mentioned(string? tag, string current, string? skipped, string? expected)
    {
        Assert.Equal(expected, AppUpdate.NewerVersion(tag, current, skipped));
    }

    [Fact]
    public void Checks_at_most_once_a_day()
    {
        Assert.True(AppUpdate.IsDue(enabled: true, updatedElsewhere: false, lastCheck: null, Now));
        Assert.False(AppUpdate.IsDue(true, false, Now.AddHours(-23), Now));
        Assert.True(AppUpdate.IsDue(true, false, Now.AddHours(-24), Now));
        Assert.True(AppUpdate.IsDue(true, false, Now.AddDays(3), Now)); // the clock was moved back
    }

    [Fact]
    public void Never_checks_when_turned_off_or_when_something_else_updates_the_app()
    {
        Assert.False(AppUpdate.IsDue(enabled: false, updatedElsewhere: false, lastCheck: null, Now));
        Assert.False(AppUpdate.IsDue(enabled: true, updatedElsewhere: true, lastCheck: null, Now));
    }

    [Fact]
    public void Links_to_the_releases_page_for_that_version()
    {
        Assert.Equal("https://github.com/disibio/vid-archiver-gui/releases/tag/v1.2.0", AppUpdate.ReleasePage("1.2.0"));
    }
}
