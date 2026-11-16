using System.Collections.Concurrent;
using System.Text;
using Dapper;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Reliability;
using Xunit.Abstractions;

namespace SabhaMujic.Server.Tests.Reliability;

/// <summary>
/// Authorization under load: volunteers (and unauthenticated connections) hammer every admin operation, including the
/// admin's own in-flight upload ids, while the admin imports. Zero successes, only Forbidden/Unauthenticated, and no side
/// effects beyond the admin's imports.
/// </summary>
public sealed class AuthorizationStressTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Volunteers_hammering_admin_operations_during_an_admin_import_get_zero_successes_and_cause_no_side_effects()
    {
        await using var h = await ReliabilityHost.StartAsync(speed: 10);
        var existing = await h.ImportAsync(SoakWav.Through(5, 1), "Existing");
        var category = await h.Admin.Admin.CreateCategoryAsync("Bhajan");
        var device = await h.App.CreateSession(ClientKind.Desktop, "test").CreatePairingCodeAsyncAs(h);
        await h.CreateVolunteersAsync(6);
        await h.WaitForAnalysisAsync();
        var users = await h.Admin.Admin.GetUsersAsync();
        var volunteerId = users.First(u => u.Role == Role.Volunteer).Id;
        var before = await SnapshotAsync(h);

        var attackers = new List<PipeSabhaClient>();
        for (var i = 0; i < 6; i++)
        {
            attackers.Add(await h.ConnectVolunteerAsync(i));
        }

        attackers.Add(await h.ConnectAsync()); // never signs in
        attackers.Add(await h.ConnectAsync());

        // The admin imports three tracks, slowly, publishing its upload ids for the attackers to aim at.
        var uploadIds = new ConcurrentBag<Guid>();
        var imported = new ConcurrentBag<Guid>();
        var stop = new CancellationTokenSource();
        var adminWork = Task.Run(async () =>
        {
            for (var n = 0; n < 3; n++)
            {
                var bytes = SoakWav.Through(15, 100 + n); // ~1.4 MB: two chunks
                var id = await h.Admin.Admin.BeginImportAsync($"admin{n}.wav", bytes.Length);
                uploadIds.Add(id);
                for (var offset = 0; offset < bytes.Length; offset += AdminLimits.MaxChunkBytes)
                {
                    await Task.Delay(150);
                    await h.Admin.Admin.UploadChunkAsync(id, offset, bytes[offset..Math.Min(bytes.Length, offset + AdminLimits.MaxChunkBytes)]);
                }

                imported.Add((await h.Admin.Admin.CompleteImportAsync(id, new ImportMetadata($"Admin {n}", null, category.Id))).TrackId);
            }
        });

        var successes = new ConcurrentQueue<string>();
        var codes = new ConcurrentDictionary<ErrorCode, int>();
        var attempts = 0;
        var attack = attackers.Select((client, index) => Task.Run(async () =>
        {
            var random = new Random(index);
            while (!stop.IsCancellationRequested)
            {
                var upload = uploadIds.IsEmpty ? Guid.NewGuid() : uploadIds.ElementAt(random.Next(uploadIds.Count));
                var (name, call) = AdminCall(client, random, existing, category.Id, volunteerId, device, upload);
                Interlocked.Increment(ref attempts);
                try
                {
                    await call();
                    successes.Enqueue(name);
                }
                catch (SabhaMujicException ex)
                {
                    codes.AddOrUpdate(ex.Code, 1, (_, n) => n + 1);
                }
            }
        })).ToList();

        await adminWork;
        await stop.CancelAsync();
        await Task.WhenAll(attack);

        output.WriteLine($"{attempts} attempts: {string.Join(", ", codes.Select(kv => $"{kv.Key} × {kv.Value}"))}");
        Assert.Empty(successes);
        Assert.True(attempts > 200, $"only {attempts} attempts");
        Assert.All(codes.Keys, code => Assert.True(code is ErrorCode.Forbidden or ErrorCode.Unauthenticated, $"unexpected {code}"));

        // The admin's imports all went through, and nothing else changed.
        Assert.Equal(3, imported.Count);
        var after = await SnapshotAsync(h, ignoreTracks: imported.ToHashSet());
        Assert.Equal(before, after);
        Assert.Empty(h.UnexpectedErrors());
    }

    private static (string Name, Func<Task> Call) AdminCall(PipeSabhaClient c, Random random, Guid track, Guid category, Guid user, Guid device, Guid upload) =>
        random.Next(19) switch
        {
            0 => ("CreateCategory", () => c.Admin.CreateCategoryAsync("Hacked " + random.Next())),
            1 => ("RenameCategory", () => c.Admin.RenameCategoryAsync(category, "Hacked")),
            2 => ("DeleteCategory", () => c.Admin.DeleteCategoryAsync(category)),
            3 => ("ReorderCategories", () => c.Admin.ReorderCategoriesAsync([category])),
            4 => ("UpdateTrack", () => c.Admin.UpdateTrackAsync(new TrackUpdateDto(track, "Hacked", null, null))),
            5 => ("DeleteTrack", () => c.Admin.DeleteTrackAsync(track)),
            6 => ("SetLoopOverride", () => c.Admin.SetLoopOverrideAsync(track, new LoopRegionDto(0.5, 3, 0.1, 1, true))),
            7 => ("ReanalyzeTrack", () => c.Admin.ReanalyzeTrackAsync(track)),
            8 => ("GetUsers", () => c.Admin.GetUsersAsync()),
            9 => ("CreateUser", () => c.Admin.CreateUserAsync("intruder" + random.Next(), Role.Admin, "secret-123")),
            10 => ("SetUserSecret", () => c.Admin.SetUserSecretAsync(user, "taken-over")),
            11 => ("DeleteUser", () => c.Admin.DeleteUserAsync(user)),
            12 => ("GetDevices", () => c.Admin.GetDevicesAsync()),
            13 => ("CreatePairingCode", () => c.Admin.CreatePairingCodeAsync(Role.Admin)),
            14 => ("RevokeDevice", () => c.Admin.RevokeDeviceAsync(device)),
            15 => ("BeginImport", () => c.Admin.BeginImportAsync("evil.wav", 1000)),
            16 => ("UploadChunk (admin's upload)", () => c.Admin.UploadChunkAsync(upload, 0, new byte[16])),
            17 => ("CompleteImport (admin's upload)", () => c.Admin.CompleteImportAsync(upload, new ImportMetadata("Stolen", null, null))),
            _ => ("CancelImport (admin's upload)", () => c.Admin.CancelImportAsync(upload)),
        };

    /// <summary>Every table except the audit log, sessions and playback state (logins and attempts legitimately touch them).</summary>
    private static async Task<string> SnapshotAsync(ReliabilityHost h, IReadOnlySet<Guid>? ignoreTracks = null)
    {
        var sb = new StringBuilder();
        await using var conn = await h.Host.Database.OpenConnectionAsync();
        var tables = await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('audit_log', 'sessions', 'playback_state', 'playback_queue') ORDER BY name;");
        var ignored = ignoreTracks?.Select(id => id.ToString()).ToHashSet() ?? [];
        foreach (var table in tables)
        {
            foreach (var row in await conn.QueryAsync($"SELECT * FROM \"{table}\" ORDER BY 1;"))
            {
                var values = (IDictionary<string, object>)row;
                if (values.Values.Any(v => v is string s && ignored.Contains(s)))
                {
                    continue; // rows of the admin's new tracks (track, analysis)
                }

                sb.Append(table).Append(": ").AppendJoin(';', values.Select(kv => $"{kv.Key}={kv.Value}")).AppendLine();
            }
        }

        sb.AppendLine("store: " + string.Join(',', Directory.EnumerateFiles(h.Host.Store.RootDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Data.Storage.ProtectedStore.StagingFolderName, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(f => ignoreTracks is null || !IsNewFile(h, f!, ignoreTracks))
            .Order()));
        return sb.ToString();
    }

    private static bool IsNewFile(ReliabilityHost h, string fileName, IReadOnlySet<Guid> tracks) =>
        tracks.Any(id => h.App.Tracks.GetFileInfoAsync(id).GetAwaiter().GetResult()?.FileKey == fileName);
}

internal static class PairingHelper
{
    /// <summary>Creates a paired device directly (admin session), returning its id, so RevokeDevice has a real target.</summary>
    public static async Task<Guid> CreatePairingCodeAsyncAs(this Api.ApiSession session, ReliabilityHost h)
    {
        await session.LoginAsync(ReliabilityHost.AdminUser, ReliabilityHost.AdminSecret);
        var code = await session.CreatePairingCodeAsync(Role.Volunteer);
        var phone = h.App.CreateSession(ClientKind.Remote, "192.168.1.30");
        var paired = await phone.PairAsync(code.Code, "Volunteer phone");
        return paired.Device.Id;
    }
}
