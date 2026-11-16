using System.Diagnostics;

namespace SabhaMujic.Reliability;

/// <summary>Resource readings of the current process.</summary>
public sealed record ResourceReading(double ManagedHeapMb, double WorkingSetMb, int Threads, int Handles, int ThreadPoolThreads);

/// <summary>Process resource probes: managed heap after a full blocking GC, working set, OS threads and open handles.</summary>
public static class ProcessMetrics
{
    /// <summary>Full, compacting, blocking collection (twice, around finalizers), so the heap reading is live data only.</summary>
    public static void FullCollect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    public static ResourceReading Read(bool collect = true)
    {
        if (collect)
        {
            FullCollect();
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ResourceReading(
            GC.GetTotalMemory(false) / (1024.0 * 1024.0),
            process.WorkingSet64 / (1024.0 * 1024.0),
            process.Threads.Count,
            OpenHandles(process),
            ThreadPool.ThreadCount);
    }

    /// <summary>Open file descriptors on Linux (<c>/proc/self/fd</c>); the handle count elsewhere.</summary>
    public static int OpenHandles(Process? process = null)
    {
        if (OperatingSystem.IsLinux() && Directory.Exists("/proc/self/fd"))
        {
            try
            {
                return Directory.GetFileSystemEntries("/proc/self/fd").Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (process is not null)
        {
            return process.HandleCount;
        }

        using var current = Process.GetCurrentProcess();
        return current.HandleCount;
    }

    /// <summary>Process CPU time (user + kernel) consumed so far.</summary>
    public static TimeSpan CpuTime()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.TotalProcessorTime;
    }
}

/// <summary>Small statistics helpers for reports.</summary>
public static class Stats
{
    /// <summary>Nearest-rank percentile (p in [0, 100]) of an unsorted sample; NaN when empty.</summary>
    public static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    /// <summary>Least-squares slope of y over x; 0 with fewer than two distinct x values.</summary>
    public static double Slope(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Y);
        var sxx = points.Sum(p => (p.X - mx) * (p.X - mx));
        if (sxx <= 0)
        {
            return 0;
        }

        return points.Sum(p => (p.X - mx) * (p.Y - my)) / sxx;
    }

    public static double Round(double value, int digits = 3) => double.IsFinite(value) ? Math.Round(value, digits) : value;
}
