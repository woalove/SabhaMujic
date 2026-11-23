using System.Reflection;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;

namespace SabhaMujic.Core.Tests;

/// <summary>The single access matrix (contract v2): open desktop use, an admin-only remote.</summary>
public class AuthorizerTests
{
    public static IEnumerable<ClientKind> Transports => Enum.GetValues<ClientKind>();

    private static CallerContext Anon(ClientKind client) => CallerContext.Anonymous(client, "test");

    private static CallerContext Volunteer(ClientKind client) => new(client, Role.Volunteer, Guid.NewGuid(), null, "s1", "vol");

    private static CallerContext Admin(ClientKind client) => new(client, Role.Admin, Guid.NewGuid(), null, "s2", "adm");

    public static TheoryData<Operation> AllOperations()
    {
        var data = new TheoryData<Operation>();
        foreach (var op in Enum.GetValues<Operation>())
        {
            data.Add(op);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllOperations))]
    public void Every_operation_has_a_matrix_entry(Operation op) => Authorizer.RequiredLevel(op);

    [Theory]
    [MemberData(nameof(AllOperations))]
    public void Desktop_is_open_use_and_administration_needs_an_administrator(Operation op)
    {
        var level = Authorizer.RequiredLevel(op);
        var expectedForNonAdmins = level == AccessLevel.Admin ? false : true;

        Assert.Equal(expectedForNonAdmins, Authorizer.IsAllowed(Anon(ClientKind.Desktop), op));
        Assert.Equal(expectedForNonAdmins, Authorizer.IsAllowed(Volunteer(ClientKind.Desktop), op));
        Assert.True(Authorizer.IsAllowed(Admin(ClientKind.Desktop), op));
        if (level == AccessLevel.Admin)
        {
            Assert.Equal(ErrorCode.Unauthenticated, Authorizer.Check(Anon(ClientKind.Desktop), op));
            Assert.Equal(ErrorCode.Forbidden, Authorizer.Check(Volunteer(ClientKind.Desktop), op));
        }
    }

    [Theory]
    [MemberData(nameof(AllOperations))]
    public void Remote_is_for_administrators_only(Operation op)
    {
        var signIn = op is Operation.Login or Operation.ResumeSession;

        Assert.Equal(signIn ? null : ErrorCode.Unauthenticated, Authorizer.Check(Anon(ClientKind.Remote), op));
        Assert.Equal(signIn ? null : ErrorCode.Forbidden, Authorizer.Check(Volunteer(ClientKind.Remote), op));
        Assert.Null(Authorizer.Check(Admin(ClientKind.Remote), op));
    }

    [Fact]
    public void Only_sign_in_is_open_to_anonymous_remote_callers()
    {
        Assert.Equal(new HashSet<Operation> { Operation.Login, Operation.ResumeSession }, Authorizer.RemoteAnonymousOperations.ToHashSet());
        foreach (var op in new[] { Operation.Hello, Operation.InitialSetup, Operation.LocalRecovery, Operation.ReadPlayback, Operation.Logout })
        {
            var ex = Assert.Throws<SabhaMujicException>(() => Authorizer.Demand(Anon(ClientKind.Remote), op));
            Assert.Equal(ErrorCode.Unauthenticated, ex.Code);
        }
    }

    [Fact]
    public void Administration_is_admin_level_on_both_transports()
    {
        var adminOps = new[]
        {
            Operation.ImportTracks, Operation.ManageCategories, Operation.ManageCategoryMembership, Operation.ManageTracks,
            Operation.ManageLoopPoints, Operation.ManageUsers, Operation.ManageDevices,
        };

        foreach (var op in adminOps)
        {
            Assert.Equal(AccessLevel.Admin, Authorizer.RequiredLevel(op));
            foreach (var client in Enum.GetValues<ClientKind>())
            {
                var ex = Assert.Throws<SabhaMujicException>(() => Authorizer.Demand(Volunteer(client), op));
                Assert.Equal(ErrorCode.Forbidden, ex.Code);
                Assert.Equal(ErrorCode.Unauthenticated, Assert.Throws<SabhaMujicException>(() => Authorizer.Demand(Anon(client), op)).Code);
            }
        }
    }

    [Fact]
    public void Unknown_transports_fail_closed()
    {
        var odd = new CallerContext((ClientKind)42, Role.Admin, Guid.NewGuid(), null, "s", "x");
        Assert.Equal(ErrorCode.Forbidden, Authorizer.Check(odd, Operation.ReadPlayback));
    }

    private static readonly Type[] Apis =
        [typeof(IAuthApi), typeof(ILibraryApi), typeof(IPlaybackApi), typeof(IPlaylistApi), typeof(IAdminApi)];

    public static TheoryData<Type> ContractInterfaces() => new(Apis);

    [Theory]
    [MemberData(nameof(ContractInterfaces))]
    public void Every_contract_method_is_mapped_to_an_operation(Type api)
    {
        foreach (var method in api.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var op = OperationMap.For(api.Name, method.Name);
            Authorizer.RequiredLevel(op);
        }
    }

    [Fact]
    public void Operation_map_has_no_stale_entries()
    {
        var known = Apis
            .SelectMany(t => t.GetMethods().Select(m => $"{t.Name}.{m.Name}"))
            .ToHashSet();

        Assert.All(OperationMap.ByMethod.Keys, key => Assert.Contains(key, known));
    }

    [Fact]
    public void Admin_api_is_admin_only()
    {
        foreach (var method in typeof(IAdminApi).GetMethods())
        {
            Assert.Equal(AccessLevel.Admin, Authorizer.RequiredLevel(OperationMap.For(nameof(IAdminApi), method.Name)));
        }

        Assert.Equal(Operation.ManageCategoryMembership, OperationMap.For(nameof(IAdminApi), nameof(IAdminApi.SetCategoryTracksAsync)));
        Assert.Equal(Operation.ManageDevices, OperationMap.For(nameof(IAdminApi), nameof(IAdminApi.GetPhoneSetupInfoAsync)));
    }

    [Fact]
    public void Contract_version_is_2() => Assert.Equal(2, ContractVersion.Current);
}
