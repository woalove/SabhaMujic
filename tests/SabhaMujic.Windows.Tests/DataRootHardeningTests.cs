using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Service;
using SabhaMujic.Service.Security;
using Xunit.Abstractions;

namespace SabhaMujic.Windows.Tests;

/// <summary>
/// Data-root hardening on real NTFS: owner takeover, exact protected DACLs, junctions refused and never followed, and
/// <c>logs</c> readable but not writable by standard users. Most cases need an elevated administrator (true on GitHub's
/// windows-latest runners) to create a temporary standard user and to take ownership.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DataRootHardeningTests(ITestOutputHelper output)
{
    private static readonly string[] OwnerSids =
        new[] { ServiceSid.LocalSystem, ServiceSid.Administrators, ServiceSid.For(ServiceSid.ServiceName) }.Select(s => s.Value).Order().ToArray();

    [Fact]
    public void Event_log_settings_carry_the_installer_source_name()
    {
        using var root = WindowsTestSupport.NewProgramDataDir();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = false });
        var settings = new ServiceSettings(root.Path, PipeName: null, RemoteEnabled: false, HttpsPort: 8443, BootstrapPort: null);
        ServiceProgram.Configure(builder, settings, new SabhaDataPaths(root.Path));
        using var host = builder.Build();

        var eventLog = host.Services.GetRequiredService<IOptions<EventLogSettings>>().Value;
        Assert.Equal("SabhaMujic.Service", eventLog.SourceName);
        Assert.Equal("Application", eventLog.LogName);
        Assert.Equal("SabhaMujic.Service", ServiceProgram.EventLogSource);
    }

    [Fact]
    public void A_root_pre_created_by_a_standard_user_is_taken_over_with_exact_dacls()
    {
        if (!SkipUnlessElevated())
        {
            return;
        }

        using var parent = WindowsTestSupport.NewProgramDataDir();
        var paths = new SabhaDataPaths(Path.Combine(parent.Path, "SabhaMujic"));
        using var user = new WindowsTestSupport.TempLocalUser();
        using var token = WindowsTestSupport.LogOn(user.Name, user.Password);
        var userSid = WindowsIdentity.RunImpersonated(token, () =>
        {
            // A standard user may create folders in %ProgramData%: pre-create the root and a sub-folder it owns.
            Directory.CreateDirectory(paths.Root);
            Directory.CreateDirectory(paths.Library);
            File.WriteAllText(Path.Combine(paths.Root, "planted.txt"), "x");
            return WindowsIdentity.GetCurrent().User!;
        });
        Assert.Equal(userSid, OwnerOf(paths.Root)); // the setup is sound

        new WindowsDataRootGuard(logger: NullLogger.Instance).Prepare(paths);
        new WindowsDataRootGuard().Prepare(paths); // idempotent

        // Root: owner Administrators; protected; SYSTEM/Administrators/service full control (inherited by children),
        // Users read & execute on the root folder only. The pre-creator keeps nothing.
        Assert.Equal(ServiceSid.Administrators, OwnerOf(paths.Root));
        var rootRules = RulesOf(paths.Root, out var rootProtected);
        Assert.True(rootProtected);
        AssertOwnersFullControl(rootRules);
        var usersOnRoot = Assert.Single(rootRules, r => r.IdentityReference.Value == WindowsDataRootGuard.Users.Value);
        Assert.Equal(FileSystemRights.ReadAndExecute, usersOnRoot.FileSystemRights & ~FileSystemRights.Synchronize);
        Assert.Equal(InheritanceFlags.None, usersOnRoot.InheritanceFlags);
        Assert.Equal(4, rootRules.Count);
        Assert.DoesNotContain(rootRules, r => r.IdentityReference.Value == userSid.Value);

        foreach (var (kind, pathOf) in DataRootGuard.Folders)
        {
            var folder = pathOf(paths);
            Assert.Equal(ServiceSid.Administrators, OwnerOf(folder));
            var rules = RulesOf(folder, out var isProtected);
            Assert.True(isProtected, folder);
            AssertOwnersFullControl(rules);
            if (kind == DataFolder.Logs)
            {
                var users = Assert.Single(rules, r => r.IdentityReference.Value == WindowsDataRootGuard.Users.Value);
                Assert.Equal(FileSystemRights.ReadAndExecute, users.FileSystemRights & ~FileSystemRights.Synchronize);
                Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, users.InheritanceFlags);
                Assert.Equal(4, rules.Count);
            }
            else
            {
                Assert.Equal(3, rules.Count);
            }
        }
    }

    [Fact]
    public void A_junction_in_place_of_library_stops_startup_without_touching_its_target()
    {
        if (!SkipUnlessElevated())
        {
            return;
        }

        using var parent = WindowsTestSupport.NewProgramDataDir();
        var target = Directory.CreateDirectory(Path.Combine(parent.Path, "victim")).FullName;
        File.WriteAllText(Path.Combine(target, "marker.txt"), "untouched");
        var targetSddl = new DirectoryInfo(target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        var targetOwner = OwnerOf(target);

        var paths = new SabhaDataPaths(Path.Combine(parent.Path, "SabhaMujic"));
        Directory.CreateDirectory(paths.Root);
        MakeJunction(paths.Library, target);
        Assert.True(new DirectoryInfo(paths.Library).Attributes.HasFlag(FileAttributes.ReparsePoint)); // the setup is sound

        var ex = Assert.Throws<DataRootException>(() => new WindowsDataRootGuard().Prepare(paths));
        Assert.Contains("junction or symbolic link", ex.Message);
        Assert.Contains(paths.Library, ex.Message);

        Assert.Equal(targetSddl, new DirectoryInfo(target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        Assert.Equal(targetOwner, OwnerOf(target));
        Assert.Equal(["marker.txt"], Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(target, "marker.txt")));
    }

    [Fact]
    public async Task The_host_refuses_to_start_on_a_junction()
    {
        if (!SkipUnlessElevated())
        {
            return;
        }

        using var parent = WindowsTestSupport.NewProgramDataDir();
        var target = Directory.CreateDirectory(Path.Combine(parent.Path, "victim")).FullName;
        var root = Path.Combine(parent.Path, "SabhaMujic");
        Directory.CreateDirectory(root);
        MakeJunction(Path.Combine(root, "data"), target);

        var options = WindowsHostComposition.CreateOptions(root, NullLoggerFactory.Instance, "sm-junction-" + Guid.NewGuid().ToString("N")[..8]);
        await Assert.ThrowsAsync<DataRootException>(() => SabhaHost.StartAsync(options));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target)); // no database was created through the junction
    }

    [Fact]
    public void Standard_users_can_read_logs_but_not_write_them()
    {
        if (!SkipUnlessElevated())
        {
            return;
        }

        using var parent = WindowsTestSupport.NewProgramDataDir();
        var paths = new SabhaDataPaths(Path.Combine(parent.Path, "SabhaMujic"));
        new WindowsDataRootGuard().Prepare(paths);
        var logFile = Path.Combine(paths.Logs, "service-20261116.log");
        File.WriteAllText(logFile, "service started");
        File.WriteAllText(Path.Combine(paths.Data, "sabhamujic.db"), "secret");

        using var user = new WindowsTestSupport.TempLocalUser();
        using var token = WindowsTestSupport.LogOn(user.Name, user.Password);
        string? read = null;
        Exception? append = null, create = null, delete = null, listData = null;
        WindowsIdentity.RunImpersonated(token, () =>
        {
            read = File.ReadAllText(logFile);
            append = Record.Exception(() => File.AppendAllText(logFile, "forged"));
            create = Record.Exception(() => File.WriteAllText(Path.Combine(paths.Logs, "fake.log"), "forged"));
            delete = Record.Exception(() => File.Delete(logFile));
            listData = Record.Exception(() => Directory.EnumerateFiles(paths.Data).ToList());
        });

        Assert.Equal("service started", read);
        Assert.IsType<UnauthorizedAccessException>(append);
        Assert.IsType<UnauthorizedAccessException>(create);
        Assert.IsType<UnauthorizedAccessException>(delete);
        Assert.IsType<UnauthorizedAccessException>(listData);
        Assert.Equal("service started", File.ReadAllText(logFile));
        Assert.False(File.Exists(Path.Combine(paths.Logs, "fake.log")));
    }

    private bool SkipUnlessElevated()
    {
        if (WindowsTestSupport.IsElevatedAdministrator())
        {
            return true;
        }

        output.WriteLine("SKIPPED: needs an elevated administrator (temporary user, ownership changes).");
        return false;
    }

    private static SecurityIdentifier? OwnerOf(string path) =>
        new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

    private static List<FileSystemAccessRule> RulesOf(string path, out bool isProtected)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        isProtected = security.AreAccessRulesProtected;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, r =>
        {
            Assert.False(r.IsInherited, $"{path}: inherited {r.IdentityReference}");
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
        });
        return rules;
    }

    private static void AssertOwnersFullControl(List<FileSystemAccessRule> rules)
    {
        var owners = rules.Where(r => OwnerSids.Contains(r.IdentityReference.Value)).ToList();
        Assert.Equal(OwnerSids, owners.Select(r => r.IdentityReference.Value).Order());
        Assert.All(owners, r =>
        {
            Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights & FileSystemRights.FullControl);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags);
        });
    }

    private static void MakeJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        Assert.True(process.ExitCode == 0, "mklink /J failed: " + text);
    }
}
