using System.IO.Pipes;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Pipe;

namespace SabhaMujic.Server.Tests;

/// <summary>
/// The pipe's accept path and the client's handshake under stress and faults: the listener keeps accepting whatever
/// identification does (hang, throw), and a client never waits forever on a connection that does not answer.
/// </summary>
public sealed class PipeAcceptTests
{
    /// <summary>Blocks (until released) or throws for the connections chosen by <c>behave</c>; describes the rest normally.</summary>
    private sealed class FaultyIdentity(Func<int, string?> behave) : ICallerIdentity, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _calls;

        public PipeCallerInfo Describe(NamedPipeServerStream pipe)
        {
            switch (behave(Interlocked.Increment(ref _calls)))
            {
                case "hang":
                    _release.Wait(TimeSpan.FromSeconds(60));
                    break;
                case "throw":
                    throw new NotSupportedException("identity lookup failed");
            }

            return new PipeCallerInfo("PC\\user", false);
        }

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }

    private static Func<string, PipeServerOptions> Pipe(TimeSpan? identifyTimeout = null) => name => new PipeServerOptions
    {
        PipeName = name,
        IdentifyTimeout = identifyTimeout ?? TimeSpan.FromSeconds(5),
    };

    private static async Task<PipeSabhaClient> ConnectWithinAsync(TestServer server, TimeSpan timeout, string what)
    {
        var client = server.NewClient();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await client.ConnectAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"{what}: no connection after {client.ConnectAttempts} attempts; last error: {client.LastConnectError}");
        }

        return client;
    }

    [Fact]
    public async Task Fifty_sequential_and_ten_concurrent_connections_all_complete_the_handshake()
    {
        await using var server = await TestServer.StartAsync();
        for (var i = 0; i < 50; i++)
        {
            var client = await ConnectWithinAsync(server, TimeSpan.FromSeconds(10), $"sequential #{i + 1}");
            Assert.False(await client.Auth.IsSetupRequiredAsync());
            await client.DisposeAsync();
        }

        var concurrent = await Task.WhenAll(Enumerable.Range(1, 10)
            .Select(i => Task.Run(() => ConnectWithinAsync(server, TimeSpan.FromSeconds(10), $"concurrent #{i}"))));
        foreach (var client in concurrent)
        {
            Assert.NotNull(client.ServerInfo);
            await client.DisposeAsync();
        }

        await AssertEx.Eventually(() => server.Pipe!.ConnectionCount == 0, "every connection closed again");
    }

    [Fact]
    public async Task A_hanging_identity_lookup_neither_blocks_the_listener_nor_the_connection()
    {
        using var identity = new FaultyIdentity(call => call == 1 ? "hang" : null);
        await using var server = await TestServer.StartAsync(identity: identity, pipe: Pipe(identifyTimeout: TimeSpan.FromMilliseconds(500)));

        // The first connection's lookup hangs: it still gets served (as an unidentified standard user) after the timeout…
        var stuck = Task.Run(() => ConnectWithinAsync(server, TimeSpan.FromSeconds(15), "connection with a hanging lookup"));

        // …and meanwhile the listener has moved on: others connect at once.
        await Task.Delay(50);
        for (var i = 0; i < 3; i++)
        {
            var other = await ConnectWithinAsync(server, TimeSpan.FromSeconds(5), $"connection #{i + 2}");
            Assert.False(await other.Auth.IsSetupRequiredAsync());
            await other.DisposeAsync();
        }

        var first = await stuck;
        Assert.NotNull(first.ServerInfo);
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => first.Auth.CompleteInitialSetupAsync("x", "1234")); // never elevated
        await first.DisposeAsync();
    }

    [Fact]
    public async Task A_failing_identity_lookup_ends_nothing_but_the_identity()
    {
        using var identity = new FaultyIdentity(call => call % 2 == 1 ? "throw" : null);
        await using var server = await TestServer.StartAsync(identity: identity, pipe: Pipe());
        for (var i = 0; i < 6; i++)
        {
            var client = await ConnectWithinAsync(server, TimeSpan.FromSeconds(5), $"connection #{i + 1}");
            Assert.False(await client.Auth.IsSetupRequiredAsync());
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_client_drops_a_connection_that_never_answers_the_handshake_and_reports_why()
    {
        var name = "sm-mute-" + Guid.NewGuid().ToString("N")[..8];
        var accepted = new List<NamedPipeServerStream>();
        using var stop = new CancellationTokenSource();

        // A listener that accepts and then never says a word.
        var mute = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync();
                    return;
                }

                lock (accepted)
                {
                    accepted.Add(pipe);
                }
            }
        });

        await using var client = new PipeSabhaClient(new PipeClientOptions
        {
            PipeName = name,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            HandshakeTimeout = TimeSpan.FromMilliseconds(300),
            InitialBackoff = TimeSpan.FromMilliseconds(50),
            MaxBackoff = TimeSpan.FromMilliseconds(100),
        });
        var connect = client.ConnectAsync();
        await AssertEx.Eventually(() => client.ConnectAttempts >= 3,
            $"the client keeps retrying instead of waiting forever (attempts {client.ConnectAttempts}, last error {client.LastConnectError})");
        Assert.False(connect.IsCompleted);
        var error = Assert.IsType<TimeoutException>(client.LastConnectError);
        Assert.Contains("did not complete the handshake", error.Message);

        await stop.CancelAsync();
        await mute.WaitAsync(TimeSpan.FromSeconds(10));
        lock (accepted)
        {
            foreach (var pipe in accepted)
            {
                pipe.Dispose();
            }
        }
    }
}
