using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Service;
using SabhaMujic.Service.Security;
using Xunit.Abstractions;

namespace SabhaMujic.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class ServiceSecurityTests(ITestOutputHelper output)
{
    [Fact]
    public void Service_sid_is_computed_like_windows()
    {
        // NT SERVICE\TrustedInstaller has a documented, fixed SID.
        Assert.Equal("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", ServiceSid.For("TrustedInstaller").Value);
        Assert.Equal(new NTAccount(@"NT SERVICE\TrustedInstaller").Translate(typeof(SecurityIdentifier)), ServiceSid.For("TrustedInstaller"));
        Assert.StartsWith("S-1-5-80-", ServiceSid.For(ServiceSid.ServiceName).Value);
    }

    [Fact]
    public void Store_acl_is_exactly_system_administrators_and_the_service()
    {
        using var dir = WindowsTestSupport.NewProgramDataDir();
        var acl = new WindowsStoreAcl();
        acl.Secure(dir.Path);
        acl.Secure(dir.Path); // idempotent
        File.WriteAllText(Path.Combine(dir.Path, "child.txt"), "x");

        var expected = new[] { ServiceSid.LocalSystem, ServiceSid.Administrators, ServiceSid.For(ServiceSid.ServiceName) }.Select(s => s.Value).Order();

        var security = new DirectoryInfo(dir.Path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "inheritance from the parent must be off");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(expected, rules.Select(r => r.IdentityReference.Value).Order());
        Assert.All(rules, r =>
        {
            Assert.False(r.IsInherited);
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights & FileSystemRights.FullControl);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags);
        });

        // Files inside inherit exactly the same three grantees.
        var fileRules = new FileInfo(Path.Combine(dir.Path, "child.txt")).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(expected, fileRules.Select(r => r.IdentityReference.Value).Distinct().Order());
    }

    [Fact]
    public void Pipe_acl_lets_users_connect_but_not_create_instances()
    {
        var security = new WindowsPipeStreamFactory().BuildSecurity();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var users = Assert.Single(rules, r => r.IdentityReference.Value == ServiceSid.AuthenticatedUsers.Value);
        Assert.Equal(PipeAccessRights.ReadWrite, users.PipeAccessRights & ~PipeAccessRights.Synchronize);
        Assert.Equal((PipeAccessRights)0, users.PipeAccessRights & PipeAccessRights.CreateNewInstance);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value);
        foreach (var owner in new[] { ServiceSid.LocalSystem, ServiceSid.Administrators, ServiceSid.For(ServiceSid.ServiceName) })
        {
            Assert.Equal(PipeAccessRights.FullControl, Assert.Single(rules, r => r.IdentityReference.Value == owner.Value).PipeAccessRights);
        }
    }

    [Fact]
    public void First_instance_flag_prevents_pipe_squatting()
    {
        var name = "sm-squat-" + Guid.NewGuid().ToString("N")[..8];
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Assert.Throws<UnauthorizedAccessException>(() => new WindowsPipeStreamFactory().CreateServerStream(name));
    }

    /// <summary>
    /// Real enforcement with a temporary standard user: it cannot list or read the protected folder, can connect to the
    /// service pipe, cannot create a server instance of it, and is not reported as an elevated administrator.
    /// Needs an elevated administrator (true on GitHub's windows-latest runners).
    /// </summary>
    [Fact]
    public async Task A_standard_user_is_kept_out_of_the_store_but_can_use_the_pipe()
    {
        if (!WindowsTestSupport.IsElevatedAdministrator())
        {
            output.WriteLine("SKIPPED: needs an elevated administrator to create a temporary local user.");
            return;
        }

        using var protectedDir = WindowsTestSupport.NewProgramDataDir();
        using var controlDir = WindowsTestSupport.NewProgramDataDir();
        var secretFile = Path.Combine(protectedDir.Path, "track.mp3");
        File.WriteAllText(secretFile, "protected audio");
        File.WriteAllText(Path.Combine(controlDir.Path, "readable.txt"), "anyone may read this");
        new WindowsStoreAcl().Secure(protectedDir.Path);

        var pipeName = "sm-acl-" + Guid.NewGuid().ToString("N")[..8];
        var factory = new WindowsPipeStreamFactory();
        await using var server = factory.CreateServerStream(pipeName);
        var accept = server.WaitForConnectionAsync();

        using var user = new WindowsTestSupport.TempLocalUser();
        using var token = WindowsTestSupport.LogOn(user.Name, user.Password);
        Exception? listError = null, readError = null, createInstanceError = null;
        string? controlText = null;
        NamedPipeClientStream? client = null;
        try
        {
            WindowsIdentity.RunImpersonated(token, () =>
            {
                controlText = File.ReadAllText(Path.Combine(controlDir.Path, "readable.txt")); // the test setup is sound
                listError = Record.Exception(() => Directory.EnumerateFiles(protectedDir.Path).ToList());
                readError = Record.Exception(() => File.ReadAllText(secretFile));
                client = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadWrite, PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification, HandleInheritability.None);
                client.Connect(5_000);
                createInstanceError = Record.Exception(() =>
                {
                    using var rogue = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                });
            });

            await accept.WaitAsync(TimeSpan.FromSeconds(10));
            var caller = new WindowsCallerIdentity().Describe(server);

            Assert.Equal("anyone may read this", controlText);
            Assert.IsType<UnauthorizedAccessException>(listError);
            Assert.IsType<UnauthorizedAccessException>(readError);
            Assert.IsType<UnauthorizedAccessException>(createInstanceError);
            Assert.True(client!.IsConnected);
            Assert.EndsWith(user.Name, caller.Principal, StringComparison.OrdinalIgnoreCase);
            Assert.False(caller.IsElevatedAdministrator);
        }
        finally
        {
            client?.Dispose();
        }
    }

    [Fact]
    public async Task The_current_process_is_reported_elevated_only_when_it_is()
    {
        var pipeName = "sm-self-" + Guid.NewGuid().ToString("N")[..8];
        await using var server = new WindowsPipeStreamFactory().CreateServerStream(pipeName);
        var accept = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadWrite, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        await client.ConnectAsync(5_000);
        await accept;

        var caller = new WindowsCallerIdentity().Describe(server);
        using var self = WindowsIdentity.GetCurrent();
        Assert.Equal(self.Name, caller.Principal, ignoreCase: true);
        Assert.Equal(WindowsTestSupport.IsElevatedAdministrator(), caller.IsElevatedAdministrator);
    }

    /// <summary>The Windows composition (Media Foundation, WASAPI, ACLs, ACL'd pipe, token identity) starts and answers Hello.</summary>
    [Fact]
    public async Task Console_hosted_service_answers_over_the_acl_pipe()
    {
        if (!WindowsTestSupport.IsElevatedAdministrator())
        {
            // The protected folders only admit SYSTEM, elevated Administrators and the service account.
            output.WriteLine("SKIPPED: needs an elevated administrator (the service's folders are ACL-protected).");
            return;
        }

        using var root = WindowsTestSupport.NewProgramDataDir();
        var pipeName = "sm-svc-" + Guid.NewGuid().ToString("N")[..8];
        var options = WindowsHostComposition.CreateOptions(root.Path, NullLoggerFactory.Instance, pipeName);
        await using var service = new SabhaServiceHost(options, NullLogger<SabhaServiceHost>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await using var client = new PipeSabhaClient(new PipeClientOptions { PipeName = pipeName, ConnectTimeout = TimeSpan.FromSeconds(5) });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(timeout.Token);
            Assert.Equal(ContractVersion.Current, client.ServerInfo!.ContractVersion);
            Assert.True(await client.Auth.IsSetupRequiredAsync());

            // This process is an elevated administrator, so first-run setup is allowed over the real token check.
            var session = await client.Auth.CompleteInitialSetupAsync("ci-admin", "ci-pin-1234");
            Assert.Equal(Role.Admin, session.Role);
            Assert.False(await client.Auth.IsSetupRequiredAsync());

            foreach (var folder in new[] { "data", "library", "uploads" })
            {
                Assert.True(new DirectoryInfo(Path.Combine(root.Path, folder)).GetAccessControl().AreAccessRulesProtected, folder);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
