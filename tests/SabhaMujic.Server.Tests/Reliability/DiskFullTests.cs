using System.Diagnostics;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Reliability;
using Xunit.Abstractions;

namespace SabhaMujic.Server.Tests.Reliability;

/// <summary>
/// Runs only where a small tmpfs can be mounted (Linux, root with CAP_SYS_ADMIN, e.g. the cloud container); skipped
/// elsewhere (GitHub runners, Windows). The probe mounts and unmounts a 1 MB tmpfs once at discovery.
/// </summary>
public sealed class TmpfsFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Unavailable = new(Probe);

    public TmpfsFactAttribute()
    {
        if (Unavailable.Value is { } reason)
        {
            Skip = "Needs a mountable tmpfs (Linux root with CAP_SYS_ADMIN): " + reason;
        }
    }

    private static string? Probe()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root" || !File.Exists("/bin/mount"))
        {
            return "not Linux root";
        }

        var dir = Path.Combine(Path.GetTempPath(), "sabhamujic-tmpfs-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (!DiskFullTests.Run("mount", "-t", "tmpfs", "-o", "size=1m", "tmpfs", dir))
            {
                return "mount failed";
            }

            DiskFullTests.Run("umount", dir);
            return null;
        }
        finally
        {
            Directory.Delete(dir);
        }
    }
}

/// <summary>Disk-full simulation: the whole data root lives on a 24 MB tmpfs that the test fills up.</summary>
public sealed class DiskFullTests(ITestOutputHelper output)
{
    [TmpfsFact]
    public async Task A_full_disk_fails_imports_and_saves_cleanly_while_playback_continues_and_everything_recovers()
    {
        var root = Path.Combine(Path.GetTempPath(), "sabhamujic-diskfull-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Assert.True(Run("mount", "-t", "tmpfs", "-o", "size=24m", "tmpfs", root), "mount tmpfs");
        try
        {
            await using var h = await ReliabilityHost.StartAsync(speed: 5, root: Path.Combine(root, "data-root"));
            var track = await h.ImportAsync(SoakWav.Loopable(30, 81), "Before full"); // ~2.9 MB
            await h.WaitForAnalysisAsync();
            await h.Admin.Playback.EnqueueAsync([track], playNext: false);
            await h.Admin.Playback.SetLoopModeAsync(LoopMode.SmartLoop);
            await h.Admin.Playback.ResumeAsync();
            await AssertEx.Eventually(() => h.Host.Engine.GetState().Status == PlaybackStatus.Playing, "playing");

            // Fill the disk completely.
            var filler = Path.Combine(root, "filler.bin");
            FillDisk(filler);

            // An import cannot be staged or stored: it fails with Internal and the "storage busy or full" message (R-7);
            // like the desktop app, the test then cancels the upload.
            var wav = SoakWav.Through(30, 82);
            var import = await ImportCancellingOnFailureAsync(h, wav, "While full");
            Assert.NotNull(import);
            Assert.Equal((ErrorCode.Internal, ServiceMessages.StorageUnavailable), (import.Code, import.Message));
            output.WriteLine($"Import on a full disk failed with {import.Code}: {import.Message}");
            Assert.Equal(0, h.Host.Uploads.ActiveCount);
            var tracks = (await h.App.Tracks.QueryAsync(new TrackQuery())).Items;
            Assert.Single(tracks);

            // Queue edits still work in memory; the save fails (logged) instead of crashing anything.
            await h.Admin.Playback.EnqueueAsync([track], playNext: false);
            await AssertEx.Eventually(() => h.Logs.Entries.Any(l => l.Message.StartsWith("Saving the playback state failed", StringComparison.Ordinal)),
                "save failure logged", 15_000);
            var saveFailure = h.Logs.Entries.First(l => l.Message.StartsWith("Saving the playback state failed", StringComparison.Ordinal));
            output.WriteLine($"Save on a full disk: {saveFailure.ExceptionType}: {saveFailure.ExceptionMessage}");
            var position = h.Host.Engine.GetState().PositionSeconds;
            await AssertEx.Eventually(() => h.Host.Engine.GetState().PositionSeconds != position, "still playing from memory");
            Assert.Equal(PlaybackStatus.Playing, h.Host.Engine.GetState().Status);

            // Space comes back: imports and saves work again, and the staging folder is empty.
            File.Delete(filler);
            await h.Admin.Playback.EnqueueAsync([track], playNext: false);
            await AssertEx.Eventually(() => new SqlitePlaybackStateRepository(h.Host.Database).LoadQueueAsync().GetAwaiter().GetResult().Items.Count == 3,
                "saved after recovery", 15_000);
            var after = await h.ImportAsync(wav, "After full");
            Assert.NotNull(await h.App.Tracks.GetAsync(after));
            Assert.Equal(0, h.Host.Uploads.ActiveCount);
            Assert.Empty(Directory.EnumerateFiles(h.Host.Paths.Uploads));
            Assert.Empty(Directory.EnumerateFiles(h.Host.Store.StagingDirectory));
        }
        finally
        {
            Run("umount", "-l", root);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    internal static bool Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void FillDisk(string path)
    {
        var block = new byte[64 * 1024];
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
        try
        {
            while (true)
            {
                stream.Write(block);
            }
        }
        catch (IOException)
        {
            // ENOSPC: full.
        }

        // Leave the last partial block out of the way too.
        try
        {
            while (true)
            {
                stream.WriteByte(0);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Imports like the desktop app: on any failure the upload is cancelled on the server. Returns the failure.</summary>
    private static async Task<SabhaMujicException?> ImportCancellingOnFailureAsync(ReliabilityHost h, byte[] wav, string title)
    {
        Guid? id = null;
        try
        {
            id = await h.Admin.Admin.BeginImportAsync(title.Replace(' ', '_') + ".wav", wav.Length);
            for (var offset = 0; offset < wav.Length; offset += AdminLimits.MaxChunkBytes)
            {
                await h.Admin.Admin.UploadChunkAsync(id.Value, offset, wav[offset..Math.Min(wav.Length, offset + AdminLimits.MaxChunkBytes)]);
            }

            await h.Admin.Admin.CompleteImportAsync(id.Value, new ImportMetadata(title, null, null));
            return null;
        }
        catch (SabhaMujicException ex)
        {
            if (id is { } uploadId)
            {
                try
                {
                    await h.Admin.Admin.CancelImportAsync(uploadId);
                }
                catch (SabhaMujicException cancel) when (cancel.Code == ErrorCode.NotFound)
                {
                    // Already gone (CompleteImport consumes the upload even when it fails).
                }
            }

            return ex;
        }
    }
}
