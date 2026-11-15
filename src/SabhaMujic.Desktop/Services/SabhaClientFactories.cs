using System.IO;
using System.Reflection;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Demo;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Services;

/// <summary><c>--demo</c>: the in-memory <see cref="DemoSabhaClient"/> (dev/demo tool, no audio, nothing persisted).</summary>
public sealed class DemoClientFactory : ISabhaClientFactory
{
    public ISabhaClient Create() => new DemoSabhaClient();
}

/// <summary>Production: the client that talks to the SabhaMujic Windows service.</summary>
public sealed class ServiceClientFactory : ISabhaClientFactory
{
    // TODO(M3): return the named-pipe StreamJsonRpc client from SabhaMujic.Client (Endpoints.PipeName) here.
    // Until it exists the app stays in the "Connecting to SabhaMujic service…" state.
    public ISabhaClient Create() => new ServiceNotAvailableClient();
}

/// <summary>
/// Stand-in until M3 delivers the pipe client: reports <see cref="ConnectionState.Connecting"/> forever and fails
/// every call with "not connected", so the UI shows its connecting banner and login stays disabled.
/// </summary>
internal sealed class ServiceNotAvailableClient : ISabhaClient
{
    public ConnectionState State => ConnectionState.Connecting;

    public SessionDto? Session => null;

    public IAuthApi Auth { get; } = NotConnectedProxy.Create<IAuthApi>();

    public ILibraryApi Library { get; } = NotConnectedProxy.Create<ILibraryApi>();

    public IPlaybackApi Playback { get; } = NotConnectedProxy.Create<IPlaybackApi>();

    public IPlaylistApi Playlists { get; } = NotConnectedProxy.Create<IPlaylistApi>();

    public IAdminApi Admin { get; } = NotConnectedProxy.Create<IAdminApi>();

    public ClientEventHub Events { get; } = new();

    public event EventHandler<ConnectionState>? ConnectionStateChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<SessionDto?>? SessionChanged
    {
        add { }
        remove { }
    }

    public Task ConnectAsync(CancellationToken ct = default) => Task.Delay(Timeout.Infinite, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Implements any API interface by returning a faulted task (IOException → "connection lost" message).</summary>
public class NotConnectedProxy : DispatchProxy
{
    private static readonly MethodInfo FromException = typeof(Task)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Task.FromException) && m.IsGenericMethodDefinition);

    public static T Create<T>()
        where T : class => Create<T, NotConnectedProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var error = new IOException("Not connected to the SabhaMujic service.");
        var returnType = targetMethod?.ReturnType ?? typeof(void);
        if (returnType == typeof(Task))
        {
            return Task.FromException(error);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return FromException.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, [error]);
        }

        throw error;
    }
}
