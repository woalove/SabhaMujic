using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using SabhaMujic.Server.Hosting;

namespace SabhaMujic.Service.Security;

/// <summary>
/// Locks down <c>%ProgramData%\SabhaMujic</c> before the service writes anything (a standard user can create folders in
/// %ProgramData%, so the root may have been pre-created, owned and seeded with links by someone else):
/// <list type="bullet">
/// <item>Junctions/symlinks in place of the root or a folder are refused, never followed (<see cref="DataRootGuard"/>).</item>
/// <item>Owner of the root and every folder becomes <c>BUILTIN\Administrators</c>. Taking ownership from someone else
/// needs SeTakeOwnership/SeRestore (SYSTEM, elevated administrators: the installer's prepare step); the service's
/// virtual account has neither, so at service startup an owner that is already SYSTEM, Administrators or the service
/// itself is accepted, and any other owner stops the service with instructions.</item>
/// <item>Root DACL (protected): SYSTEM, Administrators, the service: full control (inherited); Users: read &amp; execute on
/// the root folder itself only.</item>
/// <item><c>data</c>, <c>library</c>, <c>uploads</c>, <c>certs</c>: protected, full control for SYSTEM, Administrators and
/// the service only. <c>logs</c>: the same plus Users read-only (they can read logs but not create files).</item>
/// </list>
/// </summary>
public sealed class WindowsDataRootGuard : DataRootGuard
{
    public const string PrepareCommand = "SabhaMujic.Service.exe --prepare-data-root";

    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    private readonly SecurityIdentifier _service;
    private readonly ILogger _logger;

    public WindowsDataRootGuard(string serviceName = ServiceSid.ServiceName, ILogger? logger = null)
    {
        _service = ServiceSid.For(serviceName);
        _logger = logger ?? NullLogger.Instance;
    }

    public static SecurityIdentifier Users { get; } = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Full-control grantees of every DACL here: SYSTEM, Administrators, the service.</summary>
    public IReadOnlyList<SecurityIdentifier> Owners => [ServiceSid.LocalSystem, ServiceSid.Administrators, _service];

    public DirectorySecurity BuildRootSecurity()
    {
        var security = ProtectedWithOwners();
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    public DirectorySecurity BuildFolderSecurity(DataFolder kind)
    {
        var security = ProtectedWithOwners();
        if (kind == DataFolder.Logs)
        {
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    protected override void SecureRoot(string root)
    {
        TakeOwnership(root, "data root");
        new DirectoryInfo(root).SetAccessControl(BuildRootSecurity());
    }

    protected override void SecureFolder(string folder, DataFolder kind)
    {
        TakeOwnership(folder, kind.ToString().ToLowerInvariant() + " folder");
        new DirectoryInfo(folder).SetAccessControl(BuildFolderSecurity(kind));
    }

    private DirectorySecurity ProtectedWithOwners()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Owners)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    private void TakeOwnership(string path, string what)
    {
        TokenPrivileges.TryEnable("SeTakeOwnershipPrivilege", "SeRestorePrivilege");
        var directory = new DirectoryInfo(path);
        SecurityIdentifier? owner;
        try
        {
            owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Untrusted(path, what, null, ex);
        }

        if (owner == ServiceSid.Administrators)
        {
            return;
        }

        try
        {
            var change = new DirectorySecurity();
            change.SetOwner(ServiceSid.Administrators);
            directory.SetAccessControl(change);
            _logger.LogInformation("Took ownership of the SabhaMujic {What} '{Path}' (was {Owner}).", what, path, Describe(owner));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            if (owner is not null && (owner == ServiceSid.LocalSystem || owner == _service))
            {
                // The service's virtual account cannot assign Administrators as owner; its own folders stay owned by it.
                return;
            }

            throw Untrusted(path, what, owner, ex);
        }
    }

    private static DataRootException Untrusted(string path, string what, SecurityIdentifier? owner, Exception inner) => new(
        $"The SabhaMujic {what} '{path}' is owned by {Describe(owner)}, not by Administrators, and the service cannot take it over. " +
        $"Someone may have created it before SabhaMujic was installed. An administrator should run '{PrepareCommand}' " +
        "(or repair the installation), then start the service again.", inner);

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null)
        {
            return "an unknown account";
        }

        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }

    /// <summary>Best-effort enabling of privileges the process token already holds (no-op otherwise).</summary>
    private static class TokenPrivileges
    {
        private const int TokenAdjustPrivileges = 0x20;
        private const int TokenQuery = 0x8;
        private const int SePrivilegeEnabled = 0x2;

        public static void TryEnable(params string[] names)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            {
                return;
            }

            using (token)
            {
                foreach (var name in names)
                {
                    if (!LookupPrivilegeValue(null, name, out var luid))
                    {
                        continue;
                    }

                    var privileges = new TokenPrivilege { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                    AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero); // fails quietly if not held
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint Low;
            public int High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivilege
        {
            public int Count;
            public Luid Luid;
            public int Attributes;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, int access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivilege newState, int bufferLength,
            IntPtr previousState, IntPtr returnLength);
    }
}
