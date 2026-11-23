using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Server.Application;

/// <summary>A session bound to a connection: what the client gets back, and the caller identity for later calls.</summary>
public sealed record AuthResult(SessionDto Session, string TokenHash, CallerContext Caller);

/// <summary>
/// Options of a sign-in on the iPhone remote. <see cref="RememberDevice"/> creates a device (listed for administrators,
/// revocable) with a long-lived sliding session; otherwise the session ends after
/// <see cref="ServerOptions.RemoteSessionLifetime"/>.
/// </summary>
public sealed record RemoteSignIn(bool RememberDevice, string? DeviceName);

/// <summary>
/// Sign-in, sessions, first-run setup and recovery. Callers are already authorized by <c>ApiSession</c>; this service
/// applies the rate limits and credential checks. Only token hashes are stored.
/// <para>Session lifetimes: desktop administrator sessions end after <see cref="ServerOptions.DesktopSessionLifetime"/>
/// (15 minutes) without activity (sliding, recorded at most once per <see cref="ServerOptions.SessionTouchInterval"/>);
/// remote sessions last <see cref="ServerOptions.RemoteSessionLifetime"/> (12 hours, not extended); remembered phones
/// slide over <see cref="ServerOptions.DeviceSessionLifetime"/> (180 days).</para>
/// </summary>
public sealed class AuthService
{
    private const string GlobalKey = "*";

    private readonly IUserRepository _users;
    private readonly ISessionRepository _sessions;
    private readonly IDeviceRepository _devices;
    private readonly EventBroadcaster _events;
    private readonly AuditWriter _audit;
    private readonly ServerOptions _options;
    private readonly TimeProvider _clock;
    private readonly FailureRateLimiter _loginPerUser;
    private readonly FailureRateLimiter _loginGlobal;
    private readonly FailureRateLimiter _recoveryPerCaller;
    private readonly Lazy<string> _dummyHash;
    private readonly SemaphoreSlim _bootstrapGate = new(1, 1);
    private readonly ILogger _logger;
    private long _touchSuspendedUntilTicks;

    public AuthService(IUserRepository users, ISessionRepository sessions, IDeviceRepository devices,
        EventBroadcaster events, AuditWriter audit, ServerOptions options, TimeProvider clock, ILogger<AuthService>? logger = null)
    {
        _logger = logger ?? NullLogger<AuthService>.Instance;
        _users = users;
        _sessions = sessions;
        _devices = devices;
        _events = events;
        _audit = audit;
        _options = options;
        _clock = clock;
        _loginPerUser = new FailureRateLimiter(options.LoginMaxFailuresPerUser, options.LoginFailureWindow, clock);
        _loginGlobal = new FailureRateLimiter(options.LoginMaxFailuresGlobal, options.LoginFailureWindow, clock);
        _recoveryPerCaller = new FailureRateLimiter(options.LoginMaxFailuresPerUser, options.LoginFailureWindow, clock);
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
                $"The password must be {_options.MinSecretLength}–{_options.MaxSecretLength} characters.");
        }
    }

    /// <summary>
    /// Password sign-in (rate-limited per user name and globally). On the remote only administrators may sign in: any
    /// other account gets <see cref="ErrorCode.Forbidden"/> (after its password was checked, so this reveals nothing to
    /// someone without it); <paramref name="remote"/> decides whether the phone is remembered.
    /// </summary>
    public async Task<AuthResult> LoginAsync(CallerContext caller, string userName, string secret, CancellationToken ct, RemoteSignIn? remote = null)
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
        if (caller.Client == ClientKind.Remote)
        {
            if (user.Role != Role.Admin)
            {
                await _audit.WriteAsync(caller, "login.remote_denied", "user:" + user.UserName).ConfigureAwait(false);
                throw RemoteAdminsOnly();
            }

            if (remote is { RememberDevice: true })
            {
                return await RememberDeviceAsync(caller, user, remote.DeviceName, ct).ConfigureAwait(false);
            }
        }

        var result = await CreateUserSessionAsync(caller, user, ct).ConfigureAwait(false);
        await _audit.WriteAsync(result.Caller, "login", "user:" + user.UserName).ConfigureAwait(false);
        return result;
    }

    /// <summary>Creates the remembered phone (admin role, shown in the Devices list) and its long-lived session.</summary>
    private async Task<AuthResult> RememberDeviceAsync(CallerContext caller, UserRecord user, string? deviceName, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? $"{user.UserName}'s phone" : deviceName.Trim();
        if (name.Length > 100)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "The phone's name can have at most 100 characters.");
        }

        var device = await _devices.CreateAsync(name, Role.Admin, ct).ConfigureAwait(false);
        var token = Tokens.NewToken();
        var tokenHash = Tokens.HashToken(token);
        var record = await _sessions.CreateAsync(new NewSession(tokenHash, user.Id, device.Id, Role.Admin, ClientKind.Remote,
            _clock.GetUtcNow() + _options.DeviceSessionLifetime), ct).ConfigureAwait(false);
        var bound = BoundCaller(caller, record, user.UserName);
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.DevicesChanged, device.Id));
        await _audit.WriteAsync(bound, "login.remembered_device", "device:" + device.Id, device.Name).ConfigureAwait(false);
        return new AuthResult(new SessionDto(token, user.UserName, Role.Admin, record.ExpiresAt), tokenHash, bound);
    }

    private static SabhaMujicException RemoteAdminsOnly() =>
        new(ErrorCode.Forbidden, "Only administrators can use the SabhaMujic phone remote.");

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

    /// <summary>
    /// Admin password recovery from the desktop app. Same gate as <see cref="CompleteInitialSetupAsync"/> (desktop pipe AND
    /// an elevated local Administrator, never the remote). Sets a new password for the named administrator and signs out all of
    /// its sessions, or creates a new administrator when no account has that name; then signs in as it.
    /// <para>Rate-limited like sign-in, per Windows account (transport principal): every gated attempt counts, granted or
    /// denied, because recovery checks no secret. Remote callers are refused before the limiter so they cannot use up a
    /// local account's attempts.</para>
    /// </summary>
    public async Task<AuthResult> ResetAdministratorAsync(CallerContext caller, bool isElevatedAdministrator, string userName, string newSecret,
        CancellationToken ct)
    {
        if (caller.Client != ClientKind.Desktop)
        {
            await _audit.WriteAsync(caller, "recovery.denied").ConfigureAwait(false);
            throw RecoveryForbidden();
        }

        var callerKey = "recovery:" + caller.Principal;
        if (_recoveryPerCaller.IsLimited(callerKey))
        {
            await _audit.WriteAsync(caller, "recovery.rate_limited").ConfigureAwait(false);
            throw new SabhaMujicException(ErrorCode.RateLimited, "Too many recovery attempts. Wait a few minutes and try again.");
        }

        _recoveryPerCaller.RecordFailure(callerKey);
        if (!isElevatedAdministrator)
        {
            await _audit.WriteAsync(caller, "recovery.denied").ConfigureAwait(false);
            throw RecoveryForbidden();
        }

        var name = (userName ?? "").Trim();
        if (name.Length == 0)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "Enter the administrator's user name.");
        }

        var hash = HashSecret(newSecret); // validates the password before anything changes
        UserRecord user;
        bool created;
        await _bootstrapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await _users.GetByNameAsync(name, ct).ConfigureAwait(false);
            if (existing is null)
            {
                user = await _users.CreateAsync(name, Role.Admin, hash, ct).ConfigureAwait(false);
                created = true;
            }
            else if (existing.Role != Role.Admin)
            {
                throw new SabhaMujicException(ErrorCode.Conflict,
                    $"'{existing.UserName}' is not an administrator. Enter an administrator's name, or a new name to create an administrator.");
            }
            else
            {
                if (!await _users.UpdateSecretHashAsync(existing.Id, hash, ct).ConfigureAwait(false))
                {
                    throw new SabhaMujicException(ErrorCode.NotFound, "That account was just removed. Try again.");
                }

                user = existing;
                created = false;
            }

            // Every existing session of the account ends (a new account has none): whoever knew the old password is out.
            await _sessions.RevokeAllForUserAsync(user.Id, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _bootstrapGate.Release();
        }

        var userId = user.Id;
        _events.EndSessions(c => c.UserId == userId, "This account's password was reset on the SabhaMujic PC. Please sign in again.");
        var result = await CreateUserSessionAsync(caller, user, CancellationToken.None).ConfigureAwait(false);
        await _audit.WriteAsync(result.Caller, created ? "recovery.admin_created" : "recovery.admin_reset", "user:" + user.UserName)
            .ConfigureAwait(false);
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.UsersChanged, user.Id));
        return result;
    }

    private static SabhaMujicException RecoveryForbidden() => new(ErrorCode.Forbidden,
        "Resetting an administrator password must be done in the SabhaMujic app started with 'Run as administrator' on this PC.");

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

        if (caller.Client == ClientKind.Remote && record.Role != Role.Admin)
        {
            // A phone paired as a volunteer before contract v2: it stays listed (and revocable) but can no longer be used.
            throw RemoteAdminsOnly();
        }

        var bound = BoundCaller(caller, record, displayName);
        return new AuthResult(new SessionDto(token, displayName, record.Role, record.ExpiresAt), tokenHash, bound);
    }

    /// <summary>
    /// Returns the active session for a token hash, or null. Applies the sliding expiry at most once per
    /// <see cref="ServerOptions.SessionTouchInterval"/>.
    /// <para>
    /// The last-seen/expiry write is best-effort: it must never fail (or hold up for long) the command it rides on. When it
    /// fails (database busy or full) the failure is logged at debug level, the command goes ahead with the session as
    /// read, and touches pause for <see cref="ServerOptions.SessionTouchInterval"/> so an outage costs at most one slow
    /// call per interval; the next call after that tries again.
    /// </para>
    /// </summary>
    public async Task<SessionRecord?> ValidateAsync(string tokenHash, CancellationToken ct)
    {
        var record = await _sessions.GetActiveByTokenHashAsync(tokenHash, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        if (now - record.LastSeenAt >= _options.SessionTouchInterval && now.UtcTicks >= Volatile.Read(ref _touchSuspendedUntilTicks))
        {
            // Remote sign-ins without "remember" end 12 h after sign-in, however active: only last-seen is recorded.
            DateTimeOffset? extendTo = IsSliding(record) ? now + LifetimeOf(record) : null;
            try
            {
                if (await _sessions.TouchAsync(record.Id, extendTo, ct).ConfigureAwait(false))
                {
                    if (record.DeviceId is { } deviceId)
                    {
                        await _devices.TouchAsync(deviceId, ct).ConfigureAwait(false);
                    }

                    record = record with { LastSeenAt = now, ExpiresAt = extendTo is { } e && e > record.ExpiresAt ? e : record.ExpiresAt };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Volatile.Write(ref _touchSuspendedUntilTicks, (now + _options.SessionTouchInterval).UtcTicks);
                _logger.LogDebug(ex, "Could not record session activity; the session stays valid and the next call after {Interval} tries again.",
                    _options.SessionTouchInterval);
            }
        }

        return record;
    }

    /// <summary>
    /// Signs the caller out. A user session is revoked. A remembered phone <b>forgets itself</b>: the device record and all its
    /// sessions are revoked (no admin needed: a device may always revoke itself) and admins get
    /// <see cref="LibraryChangeKind.DevicesChanged"/>; using that phone again needs a new sign-in.
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
            _events.CloseSession(sessionId); // even if this connection has no event subscription
            _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.DevicesChanged, deviceId));
            return;
        }

        await _sessions.RevokeAsync(id, ct).ConfigureAwait(false);
        await _audit.WriteAsync(caller, "logout").ConfigureAwait(false);
        _events.EndSessions(c => c.SessionId == sessionId, "Signed out.");
        _events.CloseSession(sessionId);
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

    private static bool IsSliding(SessionRecord record) => record.DeviceId is not null || record.ClientKind == ClientKind.Desktop;

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
