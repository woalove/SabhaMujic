using System.Security.AccessControl;
using System.Security.Principal;
using SabhaMujic.Data.Storage;

namespace SabhaMujic.Service.Security;

/// <summary>
/// Locks a folder down to the service: a protected DACL (inheritance from the parent off, inherited ACEs removed) that
/// grants Full Control, inherited by sub-folders and files, to exactly SYSTEM, BUILTIN\Administrators and the service
/// SID (<c>NT SERVICE\SabhaMujic</c>). No Users, Authenticated Users or Everyone entries. Idempotent; children with
/// inheritance enabled pick up the new ACEs.
/// Used for the data (database), library and uploads folders.
/// </summary>
public sealed class WindowsStoreAcl(string serviceName = ServiceSid.ServiceName) : IStoreAcl
{
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>The identities granted Full Control, in order.</summary>
    public IReadOnlyList<SecurityIdentifier> Grantees { get; } = [ServiceSid.LocalSystem, ServiceSid.Administrators, ServiceSid.For(serviceName)];

    public void Secure(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var directory = new DirectoryInfo(rootDirectory);
        if (!directory.Exists)
        {
            directory.Create();
        }

        // Never apply an ACL through a junction/symlink: that would change the link's target instead.
        SabhaMujic.Server.Hosting.DataRootGuard.EnsureNotReparsePoint(directory.FullName, "folder");
        directory.SetAccessControl(BuildSecurity());
    }

    public DirectorySecurity BuildSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Grantees)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }
}
