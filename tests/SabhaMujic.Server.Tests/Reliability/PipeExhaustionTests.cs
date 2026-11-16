using System.IO.Pipes;

namespace SabhaMujic.Server.Tests.Reliability;

/// <summary>Connection-slot exhaustion of the desktop pipe by idle, never-authenticated local connections.</summary>
public sealed class PipeExhaustionTests
{
    [Fact]
    public async Task Idle_unauthenticated_connections_cannot_lock_out_the_desktop_app()
    {
        await using var h = await ReliabilityHost.StartAsync(speed: 1);
        var squatters = new List<NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < h.Host.Pipe!.ConnectionCount + 40; i++)
            {
                var pipe = new NamedPipeClientStream(".", h.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2_000);
                squatters.Add(pipe); // connects, never says a word
            }

            // Every slot anonymous connections may hold is taken (the rest stay reserved for signed-in clients).
            await AssertEx.Eventually(() => h.Host.Pipe!.AnonymousConnectionCount >= h.Host.Pipe.MaxAnonymousConnections, "anonymous slots taken");
            Assert.True(h.Host.Pipe!.AnonymousConnectionCount <= h.Host.Pipe.MaxAnonymousConnections, "anonymous connections stay capped");

            // The volunteer's desktop app must still get in (e.g. idle anonymous connections are dropped after a while).
            var connect = h.ConnectAsync(ReliabilityHost.AdminUser, ReliabilityHost.AdminSecret);
            Assert.Same(connect, await Task.WhenAny(connect, Task.Delay(30_000)));
            await connect;
        }
        finally
        {
            foreach (var pipe in squatters)
            {
                await pipe.DisposeAsync();
            }
        }
    }
}
