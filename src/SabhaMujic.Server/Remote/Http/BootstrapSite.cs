using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using SabhaMujic.Server.Remote.Certificates;

namespace SabhaMujic.Server.Remote.Http;

/// <summary>
/// The plain-HTTP bootstrap site. It serves exactly four things and 404s everything else: the setup page (<c>/</c>), the
/// CA as an Apple profile (<c>/sabhamujic-ca.mobileconfig</c>) and as DER (<c>/sabhamujic-ca.cer</c>), and <c>/health</c>.
/// No API, no cookies, no authentication. The page's only script copies the <c>#code=</c> fragment (never sent to any
/// server) into the HTTPS <c>/pair#code=</c> link; it is allowed by a CSP hash, not by <c>unsafe-inline</c>.
/// </summary>
public sealed class BootstrapSite
{
    public const string ProfilePath = "/sabhamujic-ca.mobileconfig";
    public const string CertificatePath = "/sabhamujic-ca.cer";
    public const string HealthPath = "/health";

    /// <summary>The only paths the bootstrap serves.</summary>
    public static readonly IReadOnlyList<string> Paths = ["/", ProfilePath, CertificatePath, HealthPath];

    private const string Script =
        "(function(){var a=document.getElementById('continue');var p=a.getAttribute('data-port');" +
        "var h=location.hostname;if(h.indexOf(':')>=0&&h.charAt(0)!=='['){h='['+h+']';}" +
        "var m=/^#code=([0-9]{6})$/.exec(location.hash);" +
        "a.href='https://'+h+':'+p+'/pair'+(m?'#code='+m[1]:'');" +
        "if(m){document.getElementById('code').textContent=m[1];document.getElementById('codebox').hidden=false;}})();";

    private const string Style =
        ":root{color-scheme:light dark;--bg:#f6f4ef;--fg:#1d1b18;--muted:#5d5850;--card:#fff;--accent:#b4531f;--line:#e2ddd3}" +
        "@media (prefers-color-scheme:dark){:root{--bg:#141210;--fg:#f1ede6;--muted:#b5ada1;--card:#1f1c19;--accent:#f08a4b;--line:#36312b}}" +
        "*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:17px/1.5 -apple-system,system-ui,sans-serif;" +
        "padding:max(16px,env(safe-area-inset-top)) 16px 32px}main{max-width:560px;margin:0 auto}h1{font-size:1.5rem;margin:.5rem 0 1rem}" +
        "ol{padding-left:1.4rem}li{margin:.6rem 0}.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:16px;margin:16px 0}" +
        ".btn{display:block;text-align:center;text-decoration:none;font-weight:600;border-radius:12px;padding:14px 16px;margin:10px 0;min-height:48px;" +
        "border:2px solid var(--accent);color:var(--accent)}.btn.primary{background:var(--accent);color:#fff}" +
        "code,.fp{font-family:ui-monospace,Menlo,monospace;font-size:.8rem;word-break:break-all;color:var(--muted)}" +
        ".code{font-size:2rem;letter-spacing:.3rem;font-weight:700}small{color:var(--muted)}";

    private readonly LocalCertificateAuthority _ca;
    private readonly Func<int> _httpsPort;
    private readonly Func<string> _defaultHost;
    private readonly byte[] _profile;
    private readonly string _contentSecurityPolicy;

    public BootstrapSite(LocalCertificateAuthority ca, Func<int> httpsPort, Func<string> defaultHost)
    {
        _ca = ca;
        _httpsPort = httpsPort;
        _defaultHost = defaultHost;
        _profile = AppleProfile.Build(ca);
        _contentSecurityPolicy =
            $"default-src 'none'; script-src '{Hash(Script)}'; style-src '{Hash(Style)}'; img-src 'self' data:; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    }

    public string ContentSecurityPolicy => _contentSecurityPolicy;

    public async Task HandleAsync(HttpContext context)
    {
        var response = context.Response;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XFrameOptions = "DENY";
        response.Headers.ContentSecurityPolicy = _contentSecurityPolicy;
        response.Headers.CacheControl = "no-store";

        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        switch (context.Request.Path.Value)
        {
            case "/":
                await WriteAsync(context, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page())).ConfigureAwait(false);
                break;
            case ProfilePath:
                response.Headers.ContentDisposition = "attachment; filename=\"sabhamujic-ca.mobileconfig\"";
                await WriteAsync(context, AppleProfile.ContentType, _profile).ConfigureAwait(false);
                break;
            case CertificatePath:
                response.Headers.ContentDisposition = "attachment; filename=\"sabhamujic-ca.cer\"";
                await WriteAsync(context, "application/x-x509-ca-cert", _ca.CertificateDer).ConfigureAwait(false);
                break;
            case HealthPath:
                await WriteAsync(context, "text/plain; charset=utf-8", "OK"u8.ToArray()).ConfigureAwait(false);
                break;
            default:
                response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    private static async Task WriteAsync(HttpContext context, string contentType, byte[] body)
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength = body.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private string Page()
    {
        var port = _httpsPort().ToString(CultureInfo.InvariantCulture);
        var host = _defaultHost();
        var fallback = $"https://{(host.Contains(':') ? "[" + host + "]" : host)}:{port}/pair";
        var e = (string s) => WebUtility.HtmlEncode(s);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
            <meta name="referrer" content="no-referrer">
            <title>SabhaMujic remote setup</title>
            <style>{{Style}}</style>
            </head>
            <body>
            <main>
            <h1>Set up the SabhaMujic remote</h1>
            <p>Do this once per iPhone. It lets Safari trust this PC's secure connection on the local Wi-Fi.</p>
            <div class="card" id="codebox" hidden>
            <small>Your pairing code</small>
            <div class="code" id="code"></div>
            </div>
            <div class="card">
            <h2>1. Install the certificate profile</h2>
            <a class="btn primary" id="profile" href="{{ProfilePath}}">Download profile</a>
            <ol>
            <li>Tap <b>Allow</b> when Safari asks to download a configuration profile, then <b>Close</b>.</li>
            <li>Open <b>Settings → General → VPN &amp; Device Management</b>, tap <b>SabhaMujic Remote</b>, then <b>Install</b> (enter your passcode) and <b>Install</b> again, then <b>Done</b>.</li>
            </ol>
            </div>
            <div class="card">
            <h2>2. Turn on full trust</h2>
            <ol>
            <li>Open <b>Settings → General → About → Certificate Trust Settings</b>.</li>
            <li>Under “Enable full trust for root certificates”, switch on <b>{{e(_ca.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false))}}</b> and tap <b>Continue</b>.</li>
            </ol>
            </div>
            <div class="card">
            <h2>3. Continue in Safari</h2>
            <p>Come back to Safari and tap Continue. You will be asked for a device name.</p>
            <a class="btn primary" id="continue" data-port="{{port}}" href="{{e(fallback)}}">Continue</a>
            </div>
            <div class="card">
            <small>Check that the fingerprint matches the one shown on the PC (Settings shows it under the profile's certificate details).</small>
            <div class="fp" id="fingerprint">SHA-256 {{e(_ca.Sha256Fingerprint)}}</div>
            <a class="btn" href="{{CertificatePath}}">Download certificate (.cer) instead</a>
            </div>
            </main>
            <script>{{Script}}</script>
            </body>
            </html>
            """;
    }

    private static string Hash(string inline) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(inline)));
}
