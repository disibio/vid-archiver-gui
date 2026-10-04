using VidArchiverGui.App.ViewModels;

namespace VidArchiverGui.App.Tests;

public sealed class BusyStatusViewModelTests
{
    [Fact]
    public Task A_job_reports_how_it_went_in_the_same_words_wherever_it_runs() => Headless.Run(async () =>
    {
        var busy = new BusyStatusViewModel();
        var status = new List<string>();

        var done = await busy.RunAndReportAsync("Installing tool", (_, _) => Task.FromResult("tool 1.0 installed.\nAll set."), status.Add);
        Assert.Equal(new JobResult(JobOutcome.Succeeded, "tool 1.0 installed.\nAll set."), done);
        Assert.Equal("All set.", status[^1]); // the status bar has room for one line

        var failed = await busy.RunAndReportAsync("Installing tool", (_, _) => throw new IOException("disk full"), status.Add);
        Assert.Equal(new JobResult(JobOutcome.Failed, "Installing tool failed: disk full"), failed);
        Assert.Equal("Installing tool failed: disk full", status[^1]);

        var cancelled = await busy.RunAndReportAsync("Installing tool", async (_, ct) =>
        {
            busy.CancelCommand.Execute(null);
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        }, status.Add);
        Assert.Equal(new JobResult(JobOutcome.Cancelled, "Installing tool: cancelled."), cancelled);
        Assert.Equal("Installing tool: cancelled.", status[^1]);
    });

    [Fact]
    public Task A_quiet_job_leaves_its_failure_out_of_the_status_bar() => Headless.Run(async () =>
    {
        var busy = new BusyStatusViewModel();
        var status = new List<string>();

        var failed = await busy.RunAndReportAsync("Checking for updates", (_, _) => throw new IOException("offline"), status.Add, quietOnError: true);

        Assert.Equal("Checking for updates failed: offline", failed?.Message);
        Assert.DoesNotContain(status, s => s.Contains("offline", StringComparison.Ordinal));
    });

    [Fact]
    public Task A_second_job_does_not_run_while_one_is_running() => Headless.Run(async () =>
    {
        var busy = new BusyStatusViewModel();
        var release = new TaskCompletionSource<string>();
        var first = busy.RunAndReportAsync("First", (_, _) => release.Task, _ => { });

        var ran = false;
        Assert.Null(await busy.RunAndReportAsync("Second", (_, _) => { ran = true; return Task.FromResult(""); }, _ => { }));
        Assert.False(ran);

        release.SetResult("first done.");
        Assert.Equal("first done.", (await first)?.Message);
    });
}
