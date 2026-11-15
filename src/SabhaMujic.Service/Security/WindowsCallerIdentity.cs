using System.IO.Pipes;
using System.Security.Principal;
using SabhaMujic.Server.Pipe;

namespace SabhaMujic.Service.Security;

/// <summary>
/// Identifies the desktop app on the other end of the pipe from its Windows token (the client connects with
/// Identification-level impersonation): the user name for the audit log, and whether the token is an <b>elevated</b>
/// local Administrator. <see cref="WindowsPrincipal.IsInRole(WindowsBuiltInRole)"/> checks enabled group membership, and
/// under UAC the Administrators group is deny-only in a non-elevated token, so only "Run as administrator" counts.
/// Any failure means "standard user".
/// </summary>
public sealed class WindowsCallerIdentity : ICallerIdentity
{
    public PipeCallerInfo Describe(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        var principal = "pipe-client";
        var elevated = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                principal = identity.Name;
                elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            elevated = false;
        }

        return new PipeCallerInfo(principal, elevated);
    }
}
