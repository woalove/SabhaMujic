using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Server.Api;
using SabhaMujic.Server.Application;

namespace SabhaMujic.Server.Remote.Http;

public sealed record PairRequest(string? Code, string? DeviceName);

public sealed record MeResponse(string Name, Role Role);

public sealed record BeginUploadRequest(string? FileName, long TotalBytes);

public sealed record BeginUploadResponse(Guid UploadId);

public sealed record CompleteUploadRequest(string? Title, string? Artist, Guid? CategoryId);

/// <summary>
/// The remote's HTTP API. Every endpoint opens a per-request <see cref="ApiSession"/> (resumed from the cookie or bearer
/// token) and performs its operation only through it, so <see cref="Core.Security.Authorizer"/> decides access.
/// <list type="bullet">
/// <item><c>POST /api/pair</c> → <see cref="ApiSession.PairAsync"/>; sets the device cookie.</item>
/// <item><c>GET /api/me</c> → <see cref="ApiSession.ResumeSessionAsync"/>; refreshes the cookie.</item>
/// <item><c>POST /api/logout</c> → <see cref="ApiSession.LogoutAsync"/> when a valid token is presented: the device unpairs itself
/// (device record and sessions revoked). Always clears the cookie and answers 204, also for unknown or expired tokens.</item>
/// <item><c>POST /api/uploads</c> → <see cref="ApiSession.BeginImportAsync"/>.</item>
/// <item><c>PUT /api/uploads/{id}?offset=N</c> (raw body ≤ <see cref="AdminLimits.MaxChunkBytes"/>) → <see cref="ApiSession.UploadChunkAsync"/>.</item>
/// <item><c>POST /api/uploads/{id}/complete</c> → <see cref="ApiSession.CompleteImportAsync"/>.</item>
/// <item><c>DELETE /api/uploads/{id}</c> → <see cref="ApiSession.CancelImportAsync"/>.</item>
/// </list>
/// </summary>
public static class RemoteApiEndpoints
{
    public const int MaxJsonBytes = 16 * 1024;

    /// <summary>Every HTTP API endpoint as (method, route template). Used by the exhaustive authorization tests.</summary>
    public static readonly IReadOnlyList<(string Method, string Route)> All =
    [
        ("POST", "/api/pair"),
        ("GET", "/api/me"),
        ("POST", "/api/logout"),
        ("POST", "/api/uploads"),
        ("PUT", "/api/uploads/{id}"),
        ("POST", "/api/uploads/{id}/complete"),
        ("DELETE", "/api/uploads/{id}"),
    ];

    internal static void Map(IEndpointRouteBuilder endpoints, ServerApplication app)
    {
        endpoints.MapPost("/api/pair", context => Handle(context, app, authenticate: false, async session =>
        {
            var request = await ReadJsonAsync<PairRequest>(context).ConfigureAwait(false);
            var paired = await session.PairAsync(request.Code ?? "", request.DeviceName ?? "", context.RequestAborted).ConfigureAwait(false);
            RemoteAuth.SetCookie(context, paired.Session.Token);
            await RemoteJson.WriteAsync(context, new MeResponse(paired.Session.UserName, paired.Session.Role)).ConfigureAwait(false);
        }));

        endpoints.MapGet("/api/me", context => Handle(context, app, authenticate: false, async session =>
        {
            var token = RemoteAuth.GetToken(context)
                ?? throw new SabhaMujicException(ErrorCode.Unauthenticated, "Sign in is required.");
            var resumed = await session.ResumeSessionAsync(token, context.RequestAborted).ConfigureAwait(false);
            if (context.Request.Cookies.ContainsKey(RemoteAuth.CookieName))
            {
                RemoteAuth.SetCookie(context, token); // slide the browser-side expiry
            }

            await RemoteJson.WriteAsync(context, new MeResponse(resumed.UserName, resumed.Role)).ConfigureAwait(false);
        }));

        endpoints.MapPost("/api/logout", async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (RemoteAuth.GetToken(context) is not null)
            {
                try
                {
                    var session = await RemoteAuth.OpenSessionAsync(app, context).ConfigureAwait(false);
                    await session.LogoutAsync(context.RequestAborted).ConfigureAwait(false);
                }
                catch (SabhaMujicException ex) when (ex.Code is ErrorCode.Unauthenticated)
                {
                    // Already revoked or expired: signing out is still a success for the phone.
                }
            }

            RemoteAuth.ClearCookie(context);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        endpoints.MapPost("/api/uploads", context => Handle(context, app, authenticate: true, async session =>
        {
            var request = await ReadJsonAsync<BeginUploadRequest>(context).ConfigureAwait(false);
            var id = await session.BeginImportAsync(request.FileName ?? "", request.TotalBytes, context.RequestAborted).ConfigureAwait(false);
            await RemoteJson.WriteAsync(context, new BeginUploadResponse(id), StatusCodes.Status201Created).ConfigureAwait(false);
        }));

        endpoints.MapPut("/api/uploads/{id:guid}", context => Handle(context, app, authenticate: true, async session =>
        {
            var id = RouteId(context);
            if (!long.TryParse(context.Request.Query["offset"], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "A non-negative 'offset' query parameter is required.");
            }

            // Early refusal so unauthorized callers cannot make the server buffer a chunk; ApiSession re-checks (re-validated).
            Authorizer.Demand(session.Caller, Operation.ImportTracks);
            var data = await ReadChunkAsync(context).ConfigureAwait(false);
            await session.UploadChunkAsync(id, offset, data, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }));

        endpoints.MapPost("/api/uploads/{id:guid}/complete", context => Handle(context, app, authenticate: true, async session =>
        {
            var id = RouteId(context);
            var request = await ReadJsonAsync<CompleteUploadRequest>(context).ConfigureAwait(false);
            var result = await session.CompleteImportAsync(id, new ImportMetadata(request.Title ?? "", request.Artist, request.CategoryId),
                context.RequestAborted).ConfigureAwait(false);
            await RemoteJson.WriteAsync(context, result).ConfigureAwait(false);
        }));

        endpoints.MapDelete("/api/uploads/{id:guid}", context => Handle(context, app, authenticate: true, async session =>
        {
            await session.CancelImportAsync(RouteId(context), context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }));
    }

    /// <summary>
    /// Runs one API call: opens the request's <see cref="ApiSession"/> (resumed from its token when
    /// <paramref name="authenticate"/>), runs <paramref name="body"/> and maps <see cref="SabhaMujicException"/> to
    /// <c>{code, message}</c> with the matching status. An invalid token also clears the cookie.
    /// </summary>
    private static async Task Handle(HttpContext context, ServerApplication app, bool authenticate, Func<ApiSession, Task> body)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            ApiSession session;
            try
            {
                session = authenticate
                    ? await RemoteAuth.OpenSessionAsync(app, context).ConfigureAwait(false)
                    : app.CreateSession(ClientKind.Remote, RemoteAuth.Principal(context));
            }
            catch (SabhaMujicException ex) when (ex.Code == ErrorCode.Unauthenticated)
            {
                RemoteAuth.ClearCookie(context);
                throw;
            }

            await body(session).ConfigureAwait(false);
        }
        catch (SabhaMujicException ex) when (!context.Response.HasStarted)
        {
            if (ex.Code == ErrorCode.Unauthenticated && context.Request.Path == "/api/me" && RemoteAuth.GetToken(context) is not null)
            {
                RemoteAuth.ClearCookie(context);
            }

            await RemoteJson.WriteErrorAsync(context, ex.Code, ex.Message).ConfigureAwait(false);
        }
    }

    private static Guid RouteId(HttpContext context) =>
        Guid.TryParse(context.Request.RouteValues["id"] as string, out var id)
            ? id
            : throw new SabhaMujicException(ErrorCode.InvalidArgument, "Invalid upload id.");

    private static async Task<T> ReadJsonAsync<T>(HttpContext context) where T : class
    {
        var mediaType = context.Request.ContentType ?? "";
        if (!mediaType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new SabhaMujicException(ErrorCode.UnsupportedFormat, "The request body must be JSON.");
        }

        if (context.Request.ContentLength is > MaxJsonBytes)
        {
            throw new SabhaMujicException(ErrorCode.TooLarge, "The request body is too large.");
        }

        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxJsonBytes)
            {
                throw new SabhaMujicException(ErrorCode.TooLarge, "The request body is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), RemoteJson.Options)
                ?? throw new SabhaMujicException(ErrorCode.InvalidArgument, "A request body is required.");
        }
        catch (JsonException)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "The request body is not valid JSON.");
        }
    }

    /// <summary>Reads a raw chunk of at most <see cref="AdminLimits.MaxChunkBytes"/> (also enforced by Kestrel's body limit).</summary>
    private static async Task<byte[]> ReadChunkAsync(HttpContext context)
    {
        var declared = context.Request.ContentLength;
        if (declared is > AdminLimits.MaxChunkBytes)
        {
            throw new SabhaMujicException(ErrorCode.TooLarge, $"Chunks are limited to {AdminLimits.MaxChunkBytes} bytes.");
        }

        var buffer = new byte[declared ?? AdminLimits.MaxChunkBytes];
        var total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                // Declared length reached (or the cap without a declared length): anything more is too large.
                var probe = new byte[1];
                if (await context.Request.Body.ReadAsync(probe, context.RequestAborted).ConfigureAwait(false) > 0)
                {
                    throw new SabhaMujicException(ErrorCode.TooLarge, $"Chunks are limited to {AdminLimits.MaxChunkBytes} bytes.");
                }

                break;
            }

            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(total), context.RequestAborted).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == buffer.Length ? buffer : buffer[..total];
    }
}
