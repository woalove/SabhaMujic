using System.Reflection;
using SabhaMujic.Client;
using SabhaMujic.Client.Rpc;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;

namespace SabhaMujic.Server.Tests;

public sealed class ServerFixture : IAsyncLifetime
{
    public TestServer Server { get; private set; } = null!;

    public async Task InitializeAsync() => Server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await Server.DisposeAsync();
}

/// <summary>
/// Invokes every method of every contract interface over the real pipe as anonymous, Volunteer and Admin and checks the
/// outcome against <see cref="Authorizer"/>. Desktop rules (contract v2): an anonymous connection is open use (everything
/// a volunteer may do); administration needs an admin sign-in (<c>Unauthenticated</c> anonymously, <c>Forbidden</c> for a
/// volunteer account). Denied calls must leave no trace (DB, store, uploads, backend, analysis).
/// </summary>
public sealed class AuthorizationMatrixTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    public enum As
    {
        Anonymous,
        Volunteer,
        Admin,
    }

    public static TheoryData<string, string, As> Cases()
    {
        var data = new TheoryData<string, string, As>();
        foreach (var api in RpcProtocol.ApiInterfaces)
        {
            foreach (var method in api.GetMethods())
            {
                foreach (var role in Enum.GetValues<As>())
                {
                    data.Add(api.Name, method.Name, role);
                }
            }
        }

        return data;
    }

    [Fact]
    public void Every_contract_method_is_covered()
    {
        var methods = RpcProtocol.ApiInterfaces.Sum(api => api.GetMethods().Length);
        Assert.Equal(methods * 3, Cases().Count);
        Assert.Equal(OperationMap.ByMethod.Count, methods);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Method_is_authorized_exactly_per_the_matrix(string apiName, string methodName, As role)
    {
        var server = fixture.Server;
        var api = RpcProtocol.ApiInterfaces.Single(a => a.Name == apiName);
        var method = api.GetMethod(methodName)!;
        var operation = OperationMap.For(apiName, methodName);
        var level = Authorizer.RequiredLevel(operation);

        await using var client = role switch
        {
            As.Anonymous => await server.ConnectAsync(),
            As.Volunteer => await server.ConnectVolunteerAsync(),
            _ => await server.ConnectAdminAsync(),
        };

        var expectedDenial = role switch
        {
            As.Anonymous when level == AccessLevel.Admin => ErrorCode.Unauthenticated,
            As.Volunteer when level == AccessLevel.Admin => ErrorCode.Forbidden,
            _ => (ErrorCode?)null,
        };
        Assert.Equal(expectedDenial, Authorizer.Check(role switch
        {
            As.Anonymous => CallerContext.Anonymous(ClientKind.Desktop, "probe"),
            As.Volunteer => new CallerContext(ClientKind.Desktop, Role.Volunteer, Guid.NewGuid(), null, "s", "probe"),
            _ => new CallerContext(ClientKind.Desktop, Role.Admin, Guid.NewGuid(), null, "s", "probe"),
        }, operation));

        var before = await server.SnapshotAsync();
        var error = await InvokeAsync(client, api, method);

        if (expectedDenial is { } denial)
        {
            Assert.NotNull(error);
            Assert.Equal(denial, error.Code);
            // Give any (wrongly) started background work a moment, then prove nothing changed.
            await Task.Delay(20);
            Assert.Equal(before, await server.SnapshotAsync());
        }
        else if (methodName is nameof(IAuthApi.CompleteInitialSetupAsync) or nameof(IAuthApi.ResetAdministratorAsync))
        {
            // Anonymous in the matrix, but the service adds its own gate (elevated desktop admin; setup also needs an empty
            // user table); this fixture's pipe identity is never elevated.
            Assert.NotNull(error);
            Assert.Equal(ErrorCode.Forbidden, error.Code);
            await Task.Delay(20);
            var after = await server.SnapshotAsync();
            Assert.Equal(StripAudit(before), StripAudit(after));
        }
        else if (error is not null)
        {
            Assert.NotEqual(ErrorCode.Forbidden, error.Code);
            Assert.NotEqual(ErrorCode.Internal, error.Code);
            if (level != AccessLevel.Anonymous)
            {
                Assert.NotEqual(ErrorCode.Unauthenticated, error.Code);
            }
        }
    }

    /// <summary>The denial itself is audited ("setup.denied" / "recovery.denied"); everything else must be unchanged.</summary>
    private static string StripAudit(string snapshot) =>
        string.Join('\n', snapshot.Split('\n').Where(line => !line.StartsWith("audit_log:", StringComparison.Ordinal)));

    internal static async Task<SabhaMujicException?> InvokeAsync(PipeSabhaClient client, Type api, MethodInfo method)
    {
        object target = api.Name switch
        {
            nameof(IAuthApi) => client.Auth,
            nameof(ILibraryApi) => client.Library,
            nameof(IPlaybackApi) => client.Playback,
            nameof(IPlaylistApi) => client.Playlists,
            _ => client.Admin,
        };
        var args = method.GetParameters().Select(ArgumentFor).ToArray();
        try
        {
            await (Task)method.Invoke(target, args)!;
            return null;
        }
        catch (SabhaMujicException ex)
        {
            return ex;
        }
    }

    internal static object? ArgumentFor(ParameterInfo p)
    {
        var t = p.ParameterType;
        if (t == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        if (t == typeof(string))
        {
            return p.Name!.Contains("secret", StringComparison.OrdinalIgnoreCase) ? "probe-secret"
                : p.Name == "userName" ? "probe-" + Guid.NewGuid().ToString("N")[..8]
                : "probe";
        }

        return t switch
        {
            _ when t == typeof(int) => p.Name == "clientContractVersion" ? ContractVersion.Current : 0,
            _ when t == typeof(long) => 1L,
            _ when t == typeof(Guid) => Guid.NewGuid(),
            _ when t == typeof(bool) => false,
            _ when t == typeof(float) => 0.5f,
            _ when t == typeof(double) => 1.0,
            _ when t == typeof(byte[]) => new byte[] { 1, 2, 3 },
            _ when t == typeof(IReadOnlyList<Guid>) => Array.Empty<Guid>(),
            _ when t == typeof(TrackQuery) => new TrackQuery(),
            _ when t == typeof(ImportMetadata) => new ImportMetadata("t", null, null),
            _ when t == typeof(TrackUpdateDto) => new TrackUpdateDto(Guid.NewGuid(), "t", null, []),
            _ when t == typeof(LoopRegionDto) => null,
            _ when t == typeof(Role) => Role.Admin,
            _ when t == typeof(LoopMode) => LoopMode.Off,
            _ => throw new NotSupportedException($"No test argument for {t} ({p.Member.Name}.{p.Name}). Add one."),
        };
    }
}
