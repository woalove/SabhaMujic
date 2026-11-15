using System.Reflection;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;

namespace SabhaMujic.Core.Tests;

public class AuthorizerTests
{
    private static readonly CallerContext Anon = CallerContext.Anonymous(ClientKind.Remote, "test");
    private static readonly CallerContext Volunteer = new(ClientKind.Remote, Role.Volunteer, Guid.NewGuid(), null, "s1", "vol");
    private static readonly CallerContext Admin = new(ClientKind.Desktop, Role.Admin, Guid.NewGuid(), null, "s2", "adm");

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
    public void Role_matrix_is_enforced_for_every_operation(Operation op)
    {
        var level = Authorizer.RequiredLevel(op);

        Assert.Equal(level == AccessLevel.Anonymous, Authorizer.IsAllowed(Anon, op));
        Assert.Equal(level != AccessLevel.Admin, Authorizer.IsAllowed(Volunteer, op));
        Assert.True(Authorizer.IsAllowed(Admin, op));
    }

    [Fact]
    public void Volunteers_are_forbidden_from_every_admin_operation()
    {
        var adminOps = new[]
        {
            Operation.ImportTracks, Operation.ManageCategories, Operation.ManageTracks,
            Operation.ManageLoopPoints, Operation.ManageUsers, Operation.ManageDevices,
        };

        foreach (var op in adminOps)
        {
            var ex = Assert.Throws<SabhaMujicException>(() => Authorizer.Demand(Volunteer, op));
            Assert.Equal(ErrorCode.Forbidden, ex.Code);
        }
    }

    [Fact]
    public void Anonymous_callers_get_unauthenticated_not_forbidden()
    {
        var ex = Assert.Throws<SabhaMujicException>(() => Authorizer.Demand(Anon, Operation.ImportTracks));
        Assert.Equal(ErrorCode.Unauthenticated, ex.Code);
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
    }
}
