using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Remote.Http;

namespace SabhaMujic.Server.Remote.Hub;

/// <summary>
/// Maps <see cref="SabhaMujicException"/> to a <see cref="HubException"/> whose message is <c>{"code","message"}</c>
/// (the only exception type SignalR passes to clients). Anything unexpected becomes a fixed Internal message.
/// </summary>
public sealed class HubErrorFilter(ILogger<HubErrorFilter> logger) : IHubFilter
{
    public const string InternalMessage = "An internal error occurred in the SabhaMujic service.";

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext).ConfigureAwait(false);
        }
        catch (SabhaMujicException ex)
        {
            throw new HubException(RemoteJson.Error(ex.Code, ex.Message));
        }
        catch (Exception ex) when (ex is not HubException)
        {
            if (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unexpected failure in hub method {Method}.", invocationContext.HubMethodName);
            }

            throw new HubException(RemoteJson.Error(ErrorCode.Internal, InternalMessage));
        }
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (SabhaMujicException ex)
        {
            throw new HubException(RemoteJson.Error(ex.Code, ex.Message));
        }
    }
}
