using Microsoft.Extensions.Logging;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Server.Application;

/// <summary>
/// Appends audit entries for an action that already happened. A failure to write the audit log is logged but does not
/// undo or fail the action.
/// </summary>
public sealed class AuditWriter(IAuditLog log, TimeProvider clock, ILogger<AuditWriter> logger)
{
    public async Task WriteAsync(CallerContext caller, string action, string? target = null, string? details = null)
    {
        try
        {
            await log.AppendAsync(new AuditEntry(caller.Principal, caller.Client, action, target, details, clock.GetUtcNow()))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit entry {Action} for {Actor}.", action, caller.Principal);
        }
    }
}
