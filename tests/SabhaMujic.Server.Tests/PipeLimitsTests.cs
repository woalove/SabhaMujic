using System.IO.Pipes;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Pipe;

namespace SabhaMujic.Server.Tests;

/// <summary>BUG-03: idle signed-out pipe connections cannot lock real users out (deadlines, reserved room, per-account cap).</summary>
public sealed class PipeLimitsTests
{
    private static Func<string, PipeServerOptions> Pipe(int max = 32, TimeSpan? hello = null, TimeSpan? auth = null, int? perPrincipal = null,
        bool deadlines = true) =>
        name => new PipeServerOptions
        {
            PipeName = name,
            MaxConnections = max,
            MaxConnectionsPerPrincipal = perPrincipal,
            HelloTimeout = deadlines ? hello ?? TimeSpan.FromSeconds(10) : null,
            AuthenticationTimeout = deadlines ? auth ?? TimeSpan.FromSeconds(60) : null,
        };

    private static async Task<NamedPipeClientStream> SquatAsync(TestServer server)
    {
        var pipe = new NamedPipeClientStream(".", server.PipeOptions.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5_000);
        return pipe; // connects, never says a word
    }

    private static async Task<bool> IsClosedAsync(Stream stream, int timeoutMs = 10_000)
    {
        var buffer = new byte[64];
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                if (await stream.ReadAsync(buffer, timeout.Token) == 0)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    [Fact]
    public async Task An_authenticated_client_gets_in_while_31_idle_connections_exist()
    {
        // Deadlines off: only the reserved room protects the slots here.
        await using var server = await TestServer.StartAsync(pipe: Pipe(deadlines: false));
        var squatters = new List<NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 31; i++)
            {
                squatters.Add(await SquatAsync(server));
            }

            await AssertEx.Eventually(() => server.Pipe!.AnonymousConnectionCount == server.Pipe.MaxAnonymousConnections, "anonymous slots full");
            Assert.Equal(24, server.Pipe!.MaxAnonymousConnections); // 32 minus a quarter reserved

            var admin = await server.ConnectAsync(TestServer.AdminName, TestServer.AdminSecret).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(Role.Admin, admin.Session!.Role);
            await admin.Playback.GetStateAsync();
            Assert.True(server.Pipe.AnonymousConnectionCount <= server.Pipe.MaxAnonymousConnections);

            // The oldest squatter made room; the newest are still connected.
            Assert.True(await IsClosedAsync(squatters[0]));
            Assert.True(squatters[^1].IsConnected);
        }
        finally
        {
            foreach (var pipe in squatters)
            {
                await pipe.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task When_every_slot_is_signed_in_a_newcomer_is_refused()
    {
        await using var server = await TestServer.StartAsync(pipe: Pipe(max: 4));
        for (var i = 0; i < 4; i++)
        {
            await server.ConnectVolunteerAsync();
        }

        await AssertEx.Eventually(() => server.Pipe!.ConnectionCount == 4, "4 signed in");
        await using var raw = await SquatAsync(server);
        Assert.True(await IsClosedAsync(raw));
        Assert.Equal(4, server.Pipe!.ConnectionCount);
    }

    [Fact]
    public async Task A_connection_without_a_handshake_is_closed_after_the_hello_deadline()
    {
        await using var server = await TestServer.StartAsync(pipe: Pipe(hello: TimeSpan.FromMilliseconds(300), auth: TimeSpan.FromSeconds(60)));
        await using var squatter = await SquatAsync(server);
        var started = DateTime.UtcNow;
        Assert.True(await IsClosedAsync(squatter, 5_000));
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(4));

        // A real client completes Hello at once and stays.
        var client = await server.ConnectAsync();
        await Task.Delay(800);
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.True(await client.Auth.IsSetupRequiredAsync() is false);
    }

    [Fact]
    public async Task Signed_out_connections_are_closed_after_the_sign_in_deadline_and_signing_out_restarts_it()
    {
        var auth = TimeSpan.FromMilliseconds(600);
        await using var server = await TestServer.StartAsync(pipe: Pipe(hello: TimeSpan.FromSeconds(5), auth: auth));
        var states = new List<ConnectionState>();
        var anonymous = server.NewClient(quietReconnectAfter: TimeSpan.FromSeconds(30)); // not quiet: the drop is visible
        anonymous.ConnectionStateChanged += (_, s) =>
        {
            lock (states)
            {
                states.Add(s);
            }
        };
        await anonymous.ConnectAsync();
        await AssertEx.Eventually(() =>
        {
            lock (states)
            {
                return states.Contains(ConnectionState.Reconnecting);
            }
        }, "the idle signed-out connection is closed", 10_000);

        // Signed in: never closed.
        var volunteer = await server.ConnectVolunteerAsync();
        await Task.Delay(auth * 3);
        await volunteer.Playback.GetStateAsync();
        Assert.Equal(ConnectionState.Connected, volunteer.State);

        // After signing out, the deadline counts again from the sign-out.
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        volunteer.ConnectionStateChanged += (_, s) =>
        {
            if (s == ConnectionState.Reconnecting)
            {
                dropped.TrySetResult();
            }
        };
        var signedOutAt = DateTime.UtcNow;
        await volunteer.Auth.LogoutAsync();
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(DateTime.UtcNow - signedOutAt >= auth * 0.8, "not before the deadline");
    }

    [Fact]
    public async Task A_sign_in_screen_left_open_reconnects_quietly()
    {
        await using var server = await TestServer.StartAsync(pipe: Pipe(hello: TimeSpan.FromSeconds(5), auth: TimeSpan.FromMilliseconds(500)));
        var client = server.NewClient(quietReconnectAfter: TimeSpan.FromMilliseconds(100));
        var states = new List<ConnectionState>();
        await client.ConnectAsync();
        client.ConnectionStateChanged += (_, s) =>
        {
            lock (states)
            {
                states.Add(s);
            }
        };

        // Several deadline closes happen; the client never reports anything but Connected and can sign in at any time.
        await Task.Delay(2_000);
        lock (states)
        {
            Assert.Empty(states);
        }

        // A login sent in the instant the service closes the idle connection can fail like any lost connection (the user
        // just presses Sign in again). With a 500 ms deadline and a loaded test machine that can happen here, so retry.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.Auth.LoginAsync(TestServer.AdminName, TestServer.AdminSecret);
                break;
            }
            catch (SabhaMujicException ex) when (ex.Code == ErrorCode.Unavailable && attempt < 10)
            {
                await Task.Delay(50);
            }
        }

        await client.Playback.GetStateAsync();
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task One_account_cannot_take_more_than_its_share()
    {
        // Every test connection is the same OS account, so the per-account cap applies to all of them.
        await using var server = await TestServer.StartAsync(pipe: Pipe(perPrincipal: 3, deadlines: false));
        var signedIn = new List<PipeSabhaClient>();
        for (var i = 0; i < 3; i++)
        {
            signedIn.Add(await server.ConnectVolunteerAsync());
        }

        // A fourth connection from the same account is refused while all three are signed in…
        await using (var raw = await SquatAsync(server))
        {
            Assert.True(await IsClosedAsync(raw));
        }

        // …but replaces that account's oldest signed-out connection once one signs out.
        await signedIn[0].Auth.LogoutAsync();
        var replacement = await server.ConnectAsync(TestServer.AdminName, TestServer.AdminSecret).WaitAsync(TimeSpan.FromSeconds(15));
        await replacement.Playback.GetStateAsync();
        Assert.True(server.Pipe!.ConnectionCount <= 3 + 1);
    }
}
