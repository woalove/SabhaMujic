using SabhaMujic.Reliability;
using Xunit.Abstractions;

namespace SabhaMujic.Server.Tests.Reliability;

/// <summary>
/// The soak harness (shared with <c>tests/SabhaMujic.Soak</c>) for 30 simulated minutes at 120x: the full randomized
/// workload over real pipe and SignalR clients, a host restart halfway, continuous invariant checks and leak thresholds.
/// The 8-hour run and its numbers are in docs/QA_REPORT.md.
/// </summary>
public sealed class MiniSoakTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Thirty_simulated_minutes_keep_every_invariant_and_do_not_leak()
    {
        await using var runner = new SoakRunner(new SoakOptions
        {
            SimulatedHours = 0.5,
            Speed = 120,
            Seed = 7,
            TrackCount = 8,
            MinTrackSeconds = 15,
            MaxTrackSeconds = 60,
            PipeClients = 3,
            RemoteClients = 1,
            RestartAtFraction = 0.5,
            MeanActionIntervalSimSeconds = 15,
            WarmupFraction = 0.25,
            ConvergenceEverySamples = 5,
            AnalysisWait = TimeSpan.FromSeconds(30),
            MaxWallTime = TimeSpan.FromSeconds(75),
            Log = output.WriteLine,
        });
        var report = await runner.RunAsync();
        output.WriteLine(SoakReportWriter.Markdown(report).Split("## Samples")[0]);

        Assert.True(report.AchievedSimHours >= 0.25, $"only {report.AchievedSimHours} simulated hours in the time allowed");
        Assert.True(report.Violations.Count == 0, string.Join(Environment.NewLine, report.Violations.Select(v => $"{v.Kind}: {v.Detail}")));
        Assert.True(report.UnexpectedLogCount == 0, string.Join(Environment.NewLine, report.Logs.Where(l => !l.Expected).Select(l => $"{l.Level} {l.Category}: {l.Message} {l.ExceptionType}")));
        Assert.True(report.ConvergenceChecks >= 2, "client convergence was checked");
        var restart = Assert.Single(report.Restarts);
        Assert.True(restart.QueueRestored && restart.SettingsRestored, restart.Detail);

        // Leak thresholds after warm-up (generous: one test process, other tests running in parallel).
        var heap = report.Growth.Single(g => g.Metric.StartsWith("Managed heap", StringComparison.Ordinal));
        var handles = report.Growth.Single(g => g.Metric.StartsWith("Open handles", StringComparison.Ordinal));
        var threads = report.Growth.Single(g => g.Metric.StartsWith("OS threads", StringComparison.Ordinal));
        Assert.True(heap.Last - heap.First < 48, $"heap grew {heap.First} -> {heap.Last} MB");
        Assert.True(handles.Last - handles.First < 60, $"handles grew {handles.First} -> {handles.Last}");
        Assert.True(threads.Last - threads.First < 30, $"threads grew {threads.First} -> {threads.Last}");
        Assert.True(report.Samples.Count >= 10, $"{report.Samples.Count} samples");
    }
}
