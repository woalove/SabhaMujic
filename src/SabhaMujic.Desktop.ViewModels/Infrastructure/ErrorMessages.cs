using SabhaMujic.Contracts;

namespace SabhaMujic.Desktop.ViewModels.Infrastructure;

/// <summary>Turns exceptions into short messages a volunteer can act on. Never throws.</summary>
public static class ErrorMessages
{
    public const string ConnectionLost =
        "Lost connection to the SabhaMujic service. It will reconnect automatically; playback continues on the service.";

    public static string For(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            SabhaMujicException sme => For(sme.Code, sme.Message),
            OperationCanceledException => "Cancelled.",
            TimeoutException => "The SabhaMujic service did not respond in time. Please try again.",
            IOException or ObjectDisposedException => ConnectionLost,
            AggregateException { InnerExceptions.Count: 1 } agg => For(agg.InnerExceptions[0]),
            _ => $"Something unexpected went wrong: {exception.Message}",
        };
    }

    public static string For(ErrorCode code, string? serverMessage = null)
    {
        var detail = string.IsNullOrWhiteSpace(serverMessage) ? null : serverMessage.Trim();
        return code switch
        {
            ErrorCode.Unauthenticated => "You are not signed in, or your session has ended. Please sign in again.",
            ErrorCode.Forbidden => "You don't have permission to do that. Please ask an administrator.",
            ErrorCode.NotFound => "That item no longer exists. It may have been removed by someone else.",
            ErrorCode.InvalidArgument => detail is null ? "Some of the values entered are not valid." : $"Not valid: {detail}",
            ErrorCode.Conflict => detail is null ? "That conflicts with an existing item." : $"Conflict: {detail}",
            ErrorCode.UnsupportedFormat =>
                "This file type isn't supported. Use " + string.Join(", ", AdminLimits.AllowedExtensions.Order()) + " files.",
            ErrorCode.TooLarge => $"The file is too large (the limit is {AdminLimits.MaxUploadBytes / (1024 * 1024)} MB).",
            ErrorCode.RateLimited => "Too many attempts. Please wait a minute and try again.",
            ErrorCode.IncompatibleClient =>
                "This version of the app does not match the SabhaMujic service. Please reinstall or update SabhaMujic.",
            ErrorCode.Internal => "The SabhaMujic service hit a problem. Please try again.",
            ErrorCode.Unavailable => "Not connected to the SabhaMujic service. Reconnecting…",
            _ => detail ?? "Something went wrong. Please try again.",
        };
    }

    /// <summary>Message for a failed sign-in, which is friendlier than the generic mapping.</summary>
    public static string ForLogin(Exception exception) => exception switch
    {
        SabhaMujicException { Code: ErrorCode.Unauthenticated } => "Incorrect user name or PIN/password.",
        SabhaMujicException { Code: ErrorCode.RateLimited } => "Too many sign-in attempts. Please wait a minute and try again.",
        _ => For(exception),
    };
}
