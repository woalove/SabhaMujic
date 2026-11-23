using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace SabhaMujic.Server.Remote.Http;

/// <summary>
/// The mobile web app, embedded in this assembly (<c>RemoteWeb/…</c> resources from <c>SabhaMujic.Remote.Web/wwwroot</c>)
/// and served from memory: nothing is built or unpacked at install time. App routes (<c>/</c>, <c>/login</c>) serve
/// <c>index.html</c>. Responses carry a content hash ETag and <c>Cache-Control: no-cache</c> so updates apply at once.
/// </summary>
public sealed class WebAssets
{
    private const string Prefix = "RemoteWeb/";

    private static readonly FrozenDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".webmanifest"] = "application/manifest+json",
        [".json"] = "application/json",
        [".txt"] = "text/plain; charset=utf-8",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> AppRoutes = new[] { "/", "/login" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, Asset> _assets;

    public WebAssets(Assembly? assembly = null)
    {
        assembly ??= typeof(WebAssets).Assembly;
        var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var path = "/" + normalized[Prefix.Length..];
            if (!ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType))
            {
                continue; // only known, safe types are served
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();
            var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes), 0, 12).ToLowerInvariant() + "\"";
            assets[path] = new Asset(bytes, contentType, etag);
        }

        _assets = assets.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IEnumerable<string> Paths => _assets.Keys;

    public bool TryGet(string path, out Asset asset)
    {
        if (AppRoutes.Contains(path))
        {
            path = "/index.html";
        }

        return _assets.TryGetValue(path, out asset!);
    }

    /// <summary>Serves a GET/HEAD for a known asset. Returns false when the path is not an asset.</summary>
    public async Task<bool> TryServeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        if ((!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)) || !TryGet(context.Request.Path.Value ?? "", out var asset))
        {
            return false;
        }

        var response = context.Response;
        response.Headers.ETag = asset.ETag;
        response.Headers.CacheControl = "no-cache";
        if (context.Request.Headers.IfNoneMatch == asset.ETag)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return true;
        }

        response.ContentType = asset.ContentType;
        response.ContentLength = asset.Content.Length;
        if (!HttpMethods.IsHead(method))
        {
            await response.Body.WriteAsync(asset.Content, context.RequestAborted).ConfigureAwait(false);
        }

        return true;
    }

    public sealed record Asset(byte[] Content, string ContentType, string ETag);
}
