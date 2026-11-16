using System.IO.Pipes;

namespace SabhaMujic.Server.Tests.Reliability;

/// <summary>Connection-slot exhaustion of the desktop pipe by idle, never-authenticated local connections.</summary>
public sealed class PipeExhaustionTests
{
    [Fact(Skip = "BUG-03: 32 idle unauthenticated pipe connections (any local user) lock the desktop app out; there is no idle timeout or per-caller cap. See docs/QA_REPORT.md.")]
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

            await AssertEx.Eventually(() => h.Host.Pipe!.ConnectionCount >= 32, "slots taken");

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
