using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Remote.Http;

namespace SabhaMujic.Server.Tests.Integration;

/// <summary>
/// The whole service with the remote enabled (decision #25 composition): real <c>SabhaHost</c>, real engine, the
/// <c>RemoteHost</c> component on loopback, and a SignalR client as a paired phone.
/// </summary>
public sealed class RemoteServiceTests
{
    [Fact]
    public async Task A_paired_phone_controls_the_real_engine_and_playback_survives_losing_every_hub_connection()
    {
        await using var h = await HostHarness.StartAsync(speed: 10, remote: true);
        var remote = h.Host.Remote!;
        var https = remote.Host!;
        Assert.True(File.Exists(Path.Combine(h.Host.Paths.Certs, "ca.cer")));

        // First-run setup on the desktop pipe identity, then import a track.
        var admin = h.App.CreateSession(ClientKind.Desktop, "PC\\owner", isElevatedAdministrator: true);
        await admin.CompleteInitialSetupAsync("admin", "admin-pin");
        var bytes = TestWav.Tone(seconds: 60, hz: 440);
        var upload = await admin.BeginImportAsync("long.wav", bytes.Length);
        for (var offset = 0; offset < bytes.Length; offset += AdminLimits.MaxChunkBytes)
        {
            await admin.UploadChunkAsync(upload, offset, bytes[offset..Math.Min(bytes.Length, offset + AdminLimits.MaxChunkBytes)]);
        }

        var track = (await admin.CompleteImportAsync(upload, new ImportMetadata("Long tone", null, null))).TrackId;

        // Pairing codes point at the bootstrap site and carry the CA fingerprint for comparison on the PC.
        var code = await admin.CreatePairingCodeAsync(Role.Volunteer);
        Assert.Equal(remote.Authority.Sha256Fingerprint, code.CaFingerprint);
        Assert.Equal($"http://127.0.0.1:{https.BootstrapPort}/#code={code.Code}", code.PairingUrl);

        // Pair over HTTPS (trusting exactly this CA) and open the hub as that phone.
        using var http = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) => Trusted(remote.Authority.Certificate, cert, errors),
        })
        { BaseAddress = new Uri($"https://localhost:{https.HttpsPort}/") };
        using var pair = new HttpRequestMessage(HttpMethod.Post, "/api/pair")
        {
            Content = JsonContent.Create(new { code = code.Code, deviceName = "Sevak iPhone" }, options: RemoteJson.Options),
        };
        pair.Headers.Add(RemoteAuth.CsrfHeader, "1");
        using var paired = await http.SendAsync(pair);
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var token = Remote.RemoteTestHost.CookieToken(paired)!;

        var states = new ConcurrentQueue<PlaybackStateDto>();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(http.BaseAddress!, "hub"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.HttpMessageHandlerFactory = inner =>
                {
                    if (inner is HttpClientHandler handler)
                    {
                        handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) => Trusted(remote.Authority.Certificate, cert, errors);
                    }

                    return inner;
                };
                options.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    Trusted(remote.Authority.Certificate, cert is null ? null : X509CertificateLoader.LoadCertificate(cert.GetRawCertData()), errors);
            })
            .AddJsonProtocol(json => RemoteJson.Configure(json.PayloadSerializerOptions))
            .Build();
        hub.On<PlaybackStateDto>(nameof(IClientEvents.PlaybackStateChanged), states.Enqueue);
        await using (hub)
        {
            await hub.StartAsync();
            Assert.Equal(1, https.ConnectionCount);

            // The phone plays a track; the real engine's state arrives on the hub (stamped with this run's epoch).
            await hub.InvokeAsync("PlayTrack", track);
            await AssertEx.Eventually(() => states.Any(s => s.Status == PlaybackStatus.Playing && s.CurrentTrackId == track), "Playing on the hub");
            Assert.All(states, s => Assert.Equal(h.App.ServerEpoch, s.ServerEpoch));
            await AssertEx.Eventually(() => states.Count(s => s.Status == PlaybackStatus.Playing) >= 2, "heartbeats while playing");

            // Every hub connection is cut: the engine keeps playing on its own.
            https.AbortAllConnections();
            await AssertEx.Eventually(() => https.ConnectionCount == 0, "connections dropped");
            var before = h.Host.Engine.GetState();
            await Task.Delay(500);
            var after = h.Host.Engine.GetState();
            Assert.Equal((PlaybackStatus.Playing, track), (after.Status, after.CurrentTrackId));
            Assert.True(after.PositionSeconds > before.PositionSeconds + 1, $"{before.PositionSeconds} -> {after.PositionSeconds}");
        }

        // Graceful stop: the remote goes away first, state is saved, the host reports stopped and the port is closed.
        await h.Host.StopAsync();
        Assert.False(h.Host.IsRunning);
        Assert.Null(remote.Host);
        Assert.True((await h.App.PlaybackState.LoadQueueAsync()).PositionSeconds > 0);
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, https.HttpsPort));

        await h.StartHostAsync(); // the same certificates are reused after a restart
        Assert.Equal(remote.Authority.Sha256Fingerprint, h.Host.Remote!.Authority.Sha256Fingerprint);
    }

    private static bool Trusted(X509Certificate2 ca, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }
}
