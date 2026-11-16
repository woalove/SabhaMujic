using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// The real service entry point (<see cref="ServiceProgram"/>: data-root guard, logging, Windows composition with Media
    /// Foundation, WASAPI, ACLs, the ACL'd pipe and token identity) run in-process like the installer runs it: first the
    /// elevated prepare step, then the service. It answers Hello and allows first-run setup to this elevated process. If the
    /// service exits instead (exit code 2 = data root refused, 1 = start failed), the test fails at once with that code
    /// and the service's own log in the output, instead of timing out on the client.
    /// </summary>
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
        string[] args = [$"--SabhaMujic:DataRoot={root.Path}", $"--SabhaMujic:PipeName={pipeName}", "--SabhaMujic:Remote:Enabled=false"];
        void Log(ILoggingBuilder logging) => logging.AddProvider(new TestOutputLoggerProvider(output));

        Assert.Equal(0, await ServiceProgram.RunAsync([.. args, ServiceProgram.PrepareSwitch], logging: Log));

        using var stop = new CancellationTokenSource();
        var service = Task.Run(() => ServiceProgram.RunAsync(args, stop.Token, Log));
        try
        {
            await using var client = new PipeSabhaClient(new PipeClientOptions { PipeName = pipeName, ConnectTimeout = TimeSpan.FromSeconds(5) });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var connect = client.ConnectAsync(timeout.Token);
            if (await Task.WhenAny(connect, service) == service)
            {
                Assert.Fail($"The service exited with code {await service} before answering (its log is in the test output).");
            }

            await connect;
            Assert.Equal(ContractVersion.Current, client.ServerInfo!.ContractVersion);
            Assert.True(await client.Auth.IsSetupRequiredAsync());

            // This process is an elevated administrator, so first-run setup is allowed over the real token check.
            var session = await client.Auth.CompleteInitialSetupAsync("ci-admin", "ci-pin-1234");
            Assert.Equal(Role.Admin, session.Role);
            Assert.False(await client.Auth.IsSetupRequiredAsync());

            foreach (var folder in new[] { "data", "library", "uploads", "certs", "logs" })
            {
                Assert.True(new DirectoryInfo(Path.Combine(root.Path, folder)).GetAccessControl().AreAccessRulesProtected, folder);
            }

            Assert.Equal(ServiceSid.Administrators,
                new DirectoryInfo(root.Path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)));
        }
        finally
        {
            await stop.CancelAsync();
            var code = await service.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(0, code);
        }
    }

    /// <summary>A junction in place of a data folder: the real entry point exits with code 2 at once (no host, no hang).</summary>
    [Fact]
    public async Task The_service_exits_with_code_2_when_its_data_root_is_refused()
    {
        using var root = WindowsTestSupport.NewProgramDataDir();
        var target = Directory.CreateDirectory(Path.Combine(root.Path, "elsewhere")).FullName;
        var dataRoot = Path.Combine(root.Path, "SabhaMujic");
        Directory.CreateDirectory(dataRoot);
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", Path.Combine(dataRoot, "data"), target })
        {
            start.ArgumentList.Add(arg);
        }

        using (var mklink = System.Diagnostics.Process.Start(start)!)
        {
            await mklink.WaitForExitAsync();
            Assert.Equal(0, mklink.ExitCode);
        }

        var code = await ServiceProgram.RunAsync(
                [$"--SabhaMujic:DataRoot={dataRoot}", "--SabhaMujic:PipeName=sm-refused-" + Guid.NewGuid().ToString("N")[..8], "--SabhaMujic:Remote:Enabled=false"],
                logging: logging => logging.AddProvider(new TestOutputLoggerProvider(output)))
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ServiceProgram.ExitDataRootRefused, code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }
}
