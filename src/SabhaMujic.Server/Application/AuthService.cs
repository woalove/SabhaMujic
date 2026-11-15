using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Server.Application;

/// <summary>A session bound to a connection: what the client gets back, and the caller identity for later calls.</summary>
public sealed record AuthResult(SessionDto Session, string TokenHash, CallerContext Caller);

public sealed record PairingResult(DeviceDto Device, SessionDto Session, string TokenHash, CallerContext Caller);

/// <summary>
/// Logins, sessions and pairing. Callers are already authorized by <c>ApiSession</c>; this service applies the rate limits
/// and credential checks. Only token hashes are stored.
/// </summary>
public sealed class AuthService
{
    private const string GlobalKey = "*";

    private readonly IUserRepository _users;
    private readonly ISessionRepository _sessions;
    private readonly IDeviceRepository _devices;
    private readonly IPairingCodeRepository _pairingCodes;
    private readonly EventBroadcaster _events;
    private readonly AuditWriter _audit;
    private readonly ServerOptions _options;
    private readonly TimeProvider _clock;
    private readonly FailureRateLimiter _loginPerUser;
    private readonly FailureRateLimiter _loginGlobal;
    private readonly FailureRateLimiter _pairingPerCaller;
    private readonly FailureRateLimiter _pairingGlobal;
    private readonly Lazy<string> _dummyHash;
    private readonly SemaphoreSlim _bootstrapGate = new(1, 1);

    public AuthService(IUserRepository users, ISessionRepository sessions, IDeviceRepository devices, IPairingCodeRepository pairingCodes,
        EventBroadcaster events, AuditWriter audit, ServerOptions options, TimeProvider clock)
    {
        _users = users;
        _sessions = sessions;
        _devices = devices;
        _pairingCodes = pairingCodes;
        _events = events;
        _audit = audit;
        _options = options;
        _clock = clock;
        _loginPerUser = new FailureRateLimiter(options.LoginMaxFailuresPerUser, options.LoginFailureWindow, clock);
        _loginGlobal = new FailureRateLimiter(options.LoginMaxFailuresGlobal, options.LoginFailureWindow, clock);
        _pairingPerCaller = new FailureRateLimiter(options.PairingMaxFailuresPerCaller, options.PairingFailureWindow, clock);
        _pairingGlobal = new FailureRateLimiter(options.PairingMaxFailuresGlobal, options.PairingFailureWindow, clock);
        // Verified against when the user does not exist, so unknown and known names take about the same time.
        _dummyHash = new Lazy<string>(() => SecretHasher.Hash(Tokens.NewToken(), options.SecretHashIterations));
    }

    public string HashSecret(string secret)
    {
        ValidateSecret(secret);
        return SecretHasher.Hash(secret, _options.SecretHashIterations);
    }

    public void ValidateSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < _options.MinSecretLength || secret.Length > _options.MaxSecretLength)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument,
                $"The password or PIN must be {_options.MinSecretLength}–{_options.MaxSecretLength} characters.");
        }
    }

    public async Task<AuthResult> LoginAsync(CallerContext caller, string userName, string secret, CancellationToken ct)
    {
        var name = (userName ?? "").Trim();
        var userKey = "user:" + name.ToUpperInvariant();
        if (_loginPerUser.IsLimited(userKey) || _loginGlobal.IsLimited(GlobalKey))
        {
            await _audit.WriteAsync(caller, "login.rate_limited", "user:" + Truncate(name)).ConfigureAwait(false);
            throw new SabhaMujicException(ErrorCode.RateLimited, "Too many failed sign-in attempts. Wait a few minutes and try again.");
        }

        var user = name.Length == 0 ? null : await _users.GetByNameAsync(name, ct).ConfigureAwait(false);
        var ok = user is not null
            ? SecretHasher.Verify(secret ?? "", user.SecretHash)
            : SecretHasher.Verify(secret ?? "", _dummyHash.Value) && false;
        if (!ok || user is null)
        {
            _loginPerUser.RecordFailure(userKey);
            _loginGlobal.RecordFailure(GlobalKey);
            await _audit.WriteAsync(caller, "login.failed", "user:" + Truncate(name)).ConfigureAwait(false);
            throw new SabhaMujicException(ErrorCode.Unauthenticated, "The user name or password is incorrect.");
        }

        _loginPerUser.Reset(userKey);
        var result = await CreateUserSessionAsync(caller, user, ct).ConfigureAwait(false);
        await _audit.WriteAsync(result.Caller, "login", "user:" + user.UserName).ConfigureAwait(false);
        return result;
    }

    /// <summary>True while no user accounts exist (fresh install).</summary>
    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct) =>
        (await _users.ListAsync(ct).ConfigureAwait(false)).Count == 0;

    /// <summary>
    /// First-run setup from the desktop app (decision #24): creates the first administrator and signs in as it.
    /// Forbidden unless the caller is on the desktop pipe AND the transport reports an elevated local Administrator;
    /// Conflict once any account exists. Serialized with <see cref="EnsureInitialAdminAsync"/>, so concurrent attempts
    /// create exactly one account.
    /// </summary>
    /// <param name="caller">The connection's anonymous (transport) identity.</param>
    /// <param name="isElevatedAdministrator">Set only by a transport that verified the OS token (never by the remote).</param>
    public async Task<AuthResult> CompleteInitialSetupAsync(CallerContext caller, bool isElevatedAdministrator, string userName, string secret,
        CancellationToken ct)
    {
        if (caller.Client != ClientKind.Desktop || !isElevatedAdministrator)
        {
            await _audit.WriteAsync(caller, "setup.denied").ConfigureAwait(false);
            throw new SabhaMujicException(ErrorCode.Forbidden,
                "First-time setup must be done in the SabhaMujic app started with 'Run as administrator' on this PC.");
        }

        UserRecord user;
        await _bootstrapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await IsSetupRequiredAsync(ct).ConfigureAwait(false))
            {
                throw new SabhaMujicException(ErrorCode.Conflict, "SabhaMujic is already set up. Sign in instead.");
            }

            user = await _users.CreateAsync(userName, Role.Admin, HashSecret(secret), ct).ConfigureAwait(false);
        }
        finally
        {
            _bootstrapGate.Release();
        }

        var result = await CreateUserSessionAsync(caller, user, CancellationToken.None).ConfigureAwait(false);
        await _audit.WriteAsync(result.Caller, "setup.complete", "user:" + user.UserName).ConfigureAwait(false);
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.UsersChanged, user.Id));
        return result;
    }

    private async Task<AuthResult> CreateUserSessionAsync(CallerContext caller, UserRecord user, CancellationToken ct)
    {
        var lifetime = caller.Client == ClientKind.Remote ? _options.RemoteSessionLifetime : _options.DesktopSessionLifetime;
        var token = Tokens.NewToken();
        var tokenHash = Tokens.HashToken(token);
        var record = await _sessions.CreateAsync(
            new NewSession(tokenHash, user.Id, null, user.Role, caller.Client, _clock.GetUtcNow() + lifetime), ct).ConfigureAwait(false);
        var bound = BoundCaller(caller, record, user.UserName);
        return new AuthResult(new SessionDto(token, user.UserName, user.Role, record.ExpiresAt), tokenHash, bound);
    }

    /// <summary>Re-attaches an existing user or device session by its token. Unauthenticated when invalid, expired or revoked.</summary>
    public async Task<AuthResult> ResumeAsync(CallerContext caller, string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 512)
        {
            throw SessionInvalid();
        }

        var tokenHash = Tokens.HashToken(token);
        var record = await ValidateAsync(tokenHash, ct).ConfigureAwait(false) ?? throw SessionInvalid();
        string displayName;
        if (record.UserId is { } userId)
        {
            displayName = (await _users.GetByIdAsync(userId, ct).ConfigureAwait(false))?.UserName ?? throw SessionInvalid();
        }
        else
        {
            var device = await _devices.GetAsync(record.DeviceId!.Value, ct).ConfigureAwait(false);
            if (device is null || device.IsRevoked)
            {
                throw SessionInvalid();
            }

            displayName = device.Name;
        }

        var bound = BoundCaller(caller, record, displayName);
        return new AuthResult(new SessionDto(token, displayName, record.Role, record.ExpiresAt), tokenHash, bound);
    }

    /// <summary>
    /// Returns the active session for a token hash, or null. Applies the sliding expiry at most once per
    /// <see cref="ServerOptions.SessionTouchInterval"/>.
    /// </summary>
    public async Task<SessionRecord?> ValidateAsync(string tokenHash, CancellationToken ct)
    {
        var record = await _sessions.GetActiveByTokenHashAsync(tokenHash, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        if (now - record.LastSeenAt >= _options.SessionTouchInterval)
        {
            var extendTo = now + LifetimeOf(record);
            if (await _sessions.TouchAsync(record.Id, extendTo, ct).ConfigureAwait(false))
            {
                if (record.DeviceId is { } deviceId)
                {
                    await _devices.TouchAsync(deviceId, ct).ConfigureAwait(false);
                }

                record = record with { LastSeenAt = now, ExpiresAt = extendTo > record.ExpiresAt ? extendTo : record.ExpiresAt };
            }
        }

        return record;
    }

    /// <summary>
    /// Signs the caller out. A user session is revoked. A paired device <b>unpairs itself</b>: the device record and all its
    /// sessions are revoked (no admin needed: a device may always revoke itself) and admins get
    /// <see cref="LibraryChangeKind.DevicesChanged"/>; pairing again needs a new code.
    /// </summary>
    public async Task LogoutAsync(CallerContext caller, CancellationToken ct)
    {
        if (caller.SessionId is not { } sessionId || !Guid.TryParse(sessionId, out var id))
        {
            return;
        }

        if (caller.DeviceId is { } deviceId)
        {
            await _devices.RevokeAsync(deviceId, ct).ConfigureAwait(false); // also revokes every session of the device
            await _audit.WriteAsync(caller, "device.logout", "device:" + deviceId).ConfigureAwait(false);
            _events.EndSessions(c => c.DeviceId == deviceId, "This device was signed out.");
            _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.DevicesChanged, deviceId));
            return;
        }

        await _sessions.RevokeAsync(id, ct).ConfigureAwait(false);
        await _audit.WriteAsync(caller, "logout").ConfigureAwait(false);
        _events.EndSessions(c => c.SessionId == sessionId, "Signed out.");
    }

    /// <summary>Consumes a pairing code and creates a device with a long-lived token (rate-limited per caller and globally).</summary>
    public async Task<PairingResult> PairAsync(CallerContext caller, string code, string deviceName, CancellationToken ct)
    {
        var callerKey = "caller:" + caller.Principal;
        if (_pairingPerCaller.IsLimited(callerKey) || _pairingGlobal.IsLimited(GlobalKey))
        {
            await _audit.WriteAsync(caller, "pair.rate_limited").ConfigureAwait(false);
            throw new SabhaMujicException(ErrorCode.RateLimited, "Too many failed pairing attempts. Wait a few minutes and try again.");
        }

        var name = (deviceName ?? "").Trim();
        if (name.Length is 0 or > 100)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "A device name of 1–100 characters is required.");
        }

        var consumed = await _pairingCodes.TryConsumeAsync((code ?? "").Trim(), ct).ConfigureAwait(false);
        if (consumed is null)
        {
            _pairingPerCaller.RecordFailure(callerKey);
            _pairingGlobal.RecordFailure(GlobalKey);
            await _audit.WriteAsync(caller, "pair.failed").ConfigureAwait(false);
            throw new SabhaMujicException(ErrorCode.Unauthenticated, "The pairing code is invalid or has expired.");
        }

        var device = await _devices.CreateAsync(name, consumed.Role, ct).ConfigureAwait(false);
        var token = Tokens.NewToken();
        var tokenHash = Tokens.HashToken(token);
        var record = await _sessions.CreateAsync(new NewSession(tokenHash, null, device.Id, consumed.Role, ClientKind.Remote,
            _clock.GetUtcNow() + _options.DeviceSessionLifetime), ct).ConfigureAwait(false);
        var bound = BoundCaller(caller with { Client = ClientKind.Remote }, record, device.Name);
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.DevicesChanged, device.Id));
        await _audit.WriteAsync(bound, "device.paired", "device:" + device.Id, "role=" + device.Role).ConfigureAwait(false);
        return new PairingResult(device.ToDto(), new SessionDto(token, device.Name, device.Role, record.ExpiresAt), tokenHash, bound);
    }

    /// <summary>
    /// First-run bootstrap: creates the initial administrator only while there are no users at all.
    /// Returns false (and changes nothing) once any user exists.
    /// </summary>
    public async Task<bool> EnsureInitialAdminAsync(string userName, string secret, CancellationToken ct = default)
    {
        await _bootstrapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if ((await _users.ListAsync(ct).ConfigureAwait(false)).Count > 0)
            {
                return false;
            }

            var user = await _users.CreateAsync(userName, Role.Admin, HashSecret(secret), ct).ConfigureAwait(false);
            var system = new CallerContext(ClientKind.Desktop, Role.Admin, null, null, null, "service (first-run setup)");
            _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.UsersChanged, user.Id));
            await _audit.WriteAsync(system, "user.bootstrap_admin", "user:" + user.UserName).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _bootstrapGate.Release();
        }
    }

    private TimeSpan LifetimeOf(SessionRecord record) =>
        record.DeviceId is not null ? _options.DeviceSessionLifetime
        : record.ClientKind == ClientKind.Remote ? _options.RemoteSessionLifetime
        : _options.DesktopSessionLifetime;

    /// <summary>
    /// The caller identity for calls on a bound connection. <paramref name="transportCaller"/> is the connection's anonymous
    /// identity (login, resume and pair always receive it); the principal becomes "name (transport principal)" for audit.
    /// </summary>
    public static CallerContext BoundCaller(CallerContext transportCaller, SessionRecord record, string displayName) =>
        new(transportCaller.Client, record.Role, record.UserId, record.DeviceId, record.Id.ToString("D"),
            $"{displayName} ({transportCaller.Principal})");

    private static SabhaMujicException SessionInvalid() =>
        new(ErrorCode.Unauthenticated, "The session has expired or was revoked. Please sign in again.");

    private static string Truncate(string value) => value.Length <= 64 ? value : value[..64];
}
