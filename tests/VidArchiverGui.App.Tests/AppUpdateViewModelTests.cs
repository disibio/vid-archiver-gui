using VidArchiverGui.App.ViewModels;

namespace VidArchiverGui.App.Tests;

public sealed class AppUpdateViewModelTests : IDisposable
{
    private readonly TestHost _t = new();
    private int _asked;

    public void Dispose() => _t.Dispose();

    private AppUpdateViewModel Create(string? latestTag = "v1.2.0", bool updatedElsewhere = false) =>
        new(_t.Host, "1.1.0", updatedElsewhere, _ =>
        {
            _asked++;
            return Task.FromResult(latestTag);
        });

    [Fact]
    public async Task Shows_a_newer_release_with_a_link_to_it()
    {
        var vm = Create();

        await vm.CheckIfDueAsync();

        Assert.True(vm.ShowBanner);
        Assert.Equal("1.2.0", vm.AvailableVersion);
        Assert.EndsWith("/releases/tag/v1.2.0", vm.ReleaseUrl);
        Assert.NotNull(_t.Settings.LastAppUpdateCheck);
    }

    [Fact]
    public async Task Doesnt_ask_github_when_the_check_is_turned_off()
    {
        _t.Settings.CheckForAppUpdates = false;
        var vm = Create();

        await vm.CheckIfDueAsync();

        Assert.Equal(0, _asked);
        Assert.False(vm.ShowBanner);
    }

    [Fact]
    public async Task Doesnt_ask_github_on_builds_that_something_else_updates()
    {
        var vm = Create(updatedElsewhere: true);

        await vm.CheckIfDueAsync();

        Assert.Equal(0, _asked);
        Assert.False(vm.ShowBanner);
    }

    [Fact]
    public async Task Doesnt_ask_again_within_a_day()
    {
        _t.Settings.LastAppUpdateCheck = DateTimeOffset.Now.AddHours(-1);
        var vm = Create();

        await vm.CheckIfDueAsync();

        Assert.Equal(0, _asked);
    }

    [Fact]
    public async Task Stop_checking_turns_the_setting_off_and_hides_the_banner()
    {
        var vm = Create();
        await vm.CheckIfDueAsync();

        vm.StopCheckingCommand.Execute(null);

        Assert.False(_t.Settings.CheckForAppUpdates);
        Assert.False(vm.ShowBanner);
    }

    [Fact]
    public async Task Turning_the_setting_off_hides_a_banner_already_shown()
    {
        var vm = Create();
        await vm.CheckIfDueAsync();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _t.Settings.CheckForAppUpdates = false;

        Assert.False(vm.ShowBanner);
        Assert.Contains(nameof(vm.ShowBanner), changed);
    }

    [Fact]
    public async Task A_skipped_version_isnt_mentioned_again()
    {
        var vm = Create();
        await vm.CheckIfDueAsync();

        vm.SkipVersionCommand.Execute(null);

        Assert.False(vm.ShowBanner);
        Assert.Equal("1.2.0", _t.Settings.SkippedAppVersion);

        _t.Settings.LastAppUpdateCheck = null;
        var next = Create();
        await next.CheckIfDueAsync();
        Assert.False(next.ShowBanner);
    }

    [Fact]
    public async Task A_failed_check_is_silent_and_tried_again_next_start()
    {
        var vm = new AppUpdateViewModel(_t.Host, "1.1.0", false, _ => throw new HttpRequestException("offline"));

        await vm.CheckIfDueAsync();

        Assert.False(vm.ShowBanner);
        Assert.Null(_t.Settings.LastAppUpdateCheck);
        Assert.Empty(_t.Statuses);
    }
}
