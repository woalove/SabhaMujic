using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using SabhaMujic.Server.Pipe;

namespace SabhaMujic.Service.Security;

/// <summary>
/// Pipe instances for the desktop app with an explicit DACL:
/// Authenticated Users may connect (Read/Write) but NOT create instances (no <see cref="PipeAccessRights.CreateNewInstance"/>),
/// so no other process can serve fake instances of the pipe; SYSTEM, Administrators, the service SID and the hosting
/// process's own identity get Full Control. The first instance per pipe name is created with
/// <see cref="PipeOptions.FirstPipeInstance"/>, so startup fails if someone else already owns the name (squatting).
/// </summary>
public sealed class WindowsPipeStreamFactory(string serviceName = ServiceSid.ServiceName) : IPipeStreamFactory
{
    private readonly ConcurrentDictionary<string, byte> _created = new(StringComparer.OrdinalIgnoreCase);

    public NamedPipeServerStream CreateServerStream(string pipeName)
    {
        var first = _created.TryAdd(pipeName, 0);
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        try
        {
            return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options, 0, 0, BuildSecurity());
        }
        catch when (first)
        {
            _created.TryRemove(pipeName, out _);
            throw;
        }
    }

    public PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(ServiceSid.AuthenticatedUsers, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var owners = new List<SecurityIdentifier> { ServiceSid.LocalSystem, ServiceSid.Administrators, ServiceSid.For(serviceName) };
        using (var self = WindowsIdentity.GetCurrent())
        {
            if (self.User is { } user && !owners.Contains(user))
            {
                owners.Add(user);
            }
        }

        foreach (var sid in owners)
        {
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }
}
