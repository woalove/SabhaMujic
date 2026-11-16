using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SabhaMujic.Reliability;

/// <summary>Writes a <see cref="SoakReport"/> as JSON (full data) and a markdown summary next to it.</summary>
public static class SoakReportWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static void Write(SoakReport report, string jsonPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, Json));
        File.WriteAllText(Path.ChangeExtension(jsonPath, ".md"), Markdown(report));
    }

    public static string ToJson(object value) => JsonSerializer.Serialize(value, Json);

    public static string Markdown(SoakReport r)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# SabhaMujic soak report").AppendLine();
        sb.AppendLine(c, $"- Started: {r.StartedAt:u}; machine: {r.Machine}");
        sb.AppendLine(c, $"- Simulated: **{r.AchievedSimHours:F2} h** of {r.TargetSimHours:F2} h target{(r.EndedEarly ? " (ENDED EARLY: wall-clock cap)" : string.Empty)}; " +
                         $"wall {r.WallMinutes:F1} min (setup {r.SetupSeconds:F0} s)");
        sb.AppendLine(c, $"- Options: `{ToJson(r.Options).ReplaceLineEndings(" ")}`");
        sb.AppendLine(c, $"- Library at start: {r.LibraryTracksAtStart} tracks, {r.AnalysesReadyAtStart} analysed");
        sb.AppendLine(c, $"- Playing (or loading/crossfading) {r.PlayingFraction:P1} of the time; {r.StateTransitions} state transitions");
        sb.AppendLine(c, $"- Event lag (server snapshot to client handler, {r.EventsReceived} events): p50 {r.EventLagP50Ms:F2} ms, p95 {r.EventLagP95Ms:F2} ms, " +
                         $"p99 {r.EventLagP99Ms:F2} ms, max {r.EventLagMaxMs:F1} ms");
        sb.AppendLine(c, $"- Estimated render underruns: {r.EstimatedUnderrunFrames} frames ({r.EstimatedUnderrunPercent:F4} % of measured frames); " +
                         $"mixer events dropped: {r.MixerDroppedEvents}");
        sb.AppendLine(c, $"- Client convergence checks: {r.ConvergenceChecks}, failures: {r.ConvergenceFailures}");
        sb.AppendLine(c, $"- Invariant violations: **{r.Violations.Count}**; unexpected log entries: **{r.UnexpectedLogCount}**").AppendLine();

        sb.AppendLine("## Growth (after warm-up)").AppendLine();
        sb.AppendLine("| Metric | First | Last | Max | Slope per simulated hour |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var g in r.Growth)
        {
            sb.AppendLine(c, $"| {g.Metric} ({g.Unit}) | {g.First:0.##} | {g.Last:0.##} | {g.Max:0.##} | {g.SlopePerSimHour:+0.###;-0.###;0} |");
        }

        sb.AppendLine().AppendLine("## Invariant violations").AppendLine();
        if (r.Violations.Count == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            foreach (var (kind, count) in r.ViolationCounts.OrderByDescending(kv => kv.Value))
            {
                sb.AppendLine(c, $"- **{kind}** × {count}");
            }

            sb.AppendLine();
            foreach (var v in r.Violations.Take(30))
            {
                sb.AppendLine(c, $"  - sim {v.SimSeconds:F0} s — {v.Kind}: {v.Detail}");
            }
        }

        sb.AppendLine().AppendLine("## Restarts").AppendLine();
        if (r.Restarts.Count == 0)
        {
            sb.AppendLine("None.");
        }

        foreach (var x in r.Restarts)
        {
            sb.AppendLine(c, $"- at sim {x.SimSeconds:F0} s: stop {x.StopSeconds:F2} s, start {x.StartSeconds:F2} s, queue restored {x.QueueRestored}, " +
                             $"settings restored {x.SettingsRestored}, status after restore {x.StatusAfterRestore}, pipe clients reconnected {x.ClientsReconnectedSeconds:F2} s after the stop began ({x.Detail})");
        }

        sb.AppendLine().AppendLine("## Workload").AppendLine();
        sb.AppendLine("| Action | OK | Failures |");
        sb.AppendLine("|---|---|---|");
        foreach (var a in r.Actions)
        {
            sb.AppendLine(c, $"| {a.Action} | {a.Ok} | {string.Join(", ", a.Failures.Select(f => $"{f.Key} × {f.Value}"))} |");
        }

        sb.AppendLine().AppendLine(c, $"Events: {string.Join(", ", r.Events.Select(e => $"{e.Key} × {e.Value}"))}").AppendLine();

        sb.AppendLine("## Log entries (warning and above, or with an exception)").AppendLine();
        sb.AppendLine("| Expected | Level | Category | Message | Exception | Count |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var l in r.Logs.Take(40))
        {
            sb.AppendLine(c, $"| {(l.Expected ? "yes" : "**no**")} | {l.Level} | {l.Category} | {l.Message.Replace("|", "/")} | {l.ExceptionType} | {l.Count} |");
        }

        sb.AppendLine().AppendLine("## Samples (every ~10th)").AppendLine();
        sb.AppendLine("| Sim h | Wall s | Heap MB | WS MB | Threads | FDs | Status | Queue | Tracks | Pipe | Hub | Subs | Lag p95 ms | Underrun frames |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        var step = Math.Max(1, r.Samples.Count / 50);
        for (var i = 0; i < r.Samples.Count; i += step)
        {
            var s = r.Samples[i];
            sb.AppendLine(c, $"| {s.SimSeconds / 3600:F2} | {s.WallSeconds:F0} | {s.ManagedHeapMb:F1} | {s.WorkingSetMb:F0} | {s.Threads} | {s.Handles} | {s.Status} | " +
                             $"{s.QueueLength} | {s.LibraryTracks} | {s.PipeConnections} | {s.RemoteConnections} | {s.EventSubscribers} | {s.EventLagP95Ms:F1} | {s.EstimatedUnderrunFrames} |");
        }

        return sb.ToString();
    }
}
