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

/// <summary>Production: the named-pipe client that talks to the SabhaMujic Windows service.</summary>
public sealed class ServiceClientFactory : ISabhaClientFactory
{
    public ISabhaClient Create() => new PipeSabhaClient(new PipeClientOptions { PipeName = Endpoints.PipeName });
}
