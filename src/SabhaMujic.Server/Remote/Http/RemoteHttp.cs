using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Api;
using SabhaMujic.Server.Application;

namespace SabhaMujic.Server.Remote.Http;

/// <summary>JSON conventions of the remote (HTTP API and SignalR): camelCase, enums as names.</summary>
public static class RemoteJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>The error body of the HTTP API and the message of hub errors: <c>{"code":"Forbidden","message":"…"}</c>.</summary>
    public static string Error(ErrorCode code, string message) => JsonSerializer.Serialize(new ErrorInfo(code, message), Options);

    public static int StatusFor(ErrorCode code) => code switch
    {
        ErrorCode.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorCode.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCode.NotFound => StatusCodes.Status404NotFound,
        ErrorCode.InvalidArgument or ErrorCode.IncompatibleClient => StatusCodes.Status400BadRequest,
        ErrorCode.Conflict => StatusCodes.Status409Conflict,
        ErrorCode.UnsupportedFormat => StatusCodes.Status415UnsupportedMediaType,
        ErrorCode.TooLarge => StatusCodes.Status413PayloadTooLarge,
        ErrorCode.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static async Task WriteErrorAsync(HttpContext context, ErrorCode code, string message)
    {
        context.Response.StatusCode = StatusFor(code);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(Error(code, message)).ConfigureAwait(false);
    }

    public static async Task WriteAsync<T>(HttpContext context, T value, int status = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, Options, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// Remote authentication and request hygiene. The device token lives only in an HttpOnly, Secure, SameSite=Strict
/// <c>__Host-</c> cookie (never readable by script); <c>Authorization: Bearer</c> is accepted for non-browser clients
/// (tests). State-changing API requests need the <see cref="CsrfHeader"/> header and, when present, a same-origin
/// <c>Origin</c>.
/// </summary>
public static class RemoteAuth
{
    public const string CookieName = "__Host-SabhaMujic";
    public const string CsrfHeader = "X-SabhaMujic";
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(400); // browsers cap cookie lifetime at ~400 days

    public static string? GetToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authorization["Bearer ".Length..].Trim();
            return bearer.Length is > 0 and <= 512 ? bearer : null;
        }

        return context.Request.Cookies.TryGetValue(CookieName, out var cookie) && cookie.Length is > 0 and <= 512 ? cookie : null;
    }

    public static void SetCookie(HttpContext context, string token) =>
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = CookieLifetime,
            IsEssential = true,
        });

    public static void ClearCookie(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });

    /// <summary>
    /// True when the request has no <c>Origin</c> (non-browser client) or its Origin is exactly this server's
    /// <c>https://host:port</c>.
    /// </summary>
    public static bool IsSameOriginOrNone(HttpContext context)
    {
        var origins = context.Request.Headers.Origin;
        if (origins.Count == 0)
        {
            return true;
        }

        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = context.Request.Host;
        var requestPort = host.Port ?? 443;
        return string.Equals(origin.IdnHost.Trim('[', ']'), host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
            && origin.Port == requestPort
            && origin.AbsolutePath == "/";
    }

    /// <summary>State-changing requests must carry <c>X-SabhaMujic: 1</c> (a simple cross-site form cannot add it).</summary>
    public static bool HasCsrfHeader(HttpContext context) => context.Request.Headers[CsrfHeader] == "1";

    /// <summary>The transport principal used for audit and per-caller pairing rate limits.</summary>
    public static string Principal(HttpContext context) => "remote:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    /// <summary>
    /// The authorization choke point for one HTTP request: an <see cref="ApiSession"/> resumed from the request's token
    /// (anonymous without one). An invalid/revoked token throws <see cref="ErrorCode.Unauthenticated"/>.
    /// </summary>
    public static async Task<ApiSession> OpenSessionAsync(ServerApplication app, HttpContext context)
    {
        var session = app.CreateSession(ClientKind.Remote, Principal(context));
        if (GetToken(context) is { } token)
        {
            await session.ResumeSessionAsync(token, context.RequestAborted).ConfigureAwait(false);
        }

        return session;
    }
}
