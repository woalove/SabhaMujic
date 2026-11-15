# Progress

_Last updated: 2026-11-15 (M0–M5 merged except installer/hardening (M6); M4 desktop polish pending)_

## Current milestone: M0 Foundations
| Task | Status | Evidence |
|---|---|---|
| Cloud environment (limited network, .NET 10 setup script) | Done | Setup script ran here; NuGet restore works under the limited network. |
| Docs: plan, CLAUDE.md, architecture, progress, testing | Done | `docs/` |
| Solution skeleton (`SabhaMujic.slnx`, 8 src + 5 test projects, central packages) | Done | `dotnet build` green on Linux (WPF compile-only) |
| Contracts v1 (DTOs, `IAuthApi`/`ILibraryApi`/`IPlaybackApi`/`IPlaylistApi`/`IAdminApi`, `IClientEvents`) | Done | Compiles; serialization tests |
| Core: Authorizer + OperationMap, SecretHasher/Tokens, PlaybackStateMachine, PlaybackQueue | Done | 74 tests pass on Linux |
| CI (Linux + Windows) | Done: run 1 built, tested and published on Windows; only artifact upload failed (account artifact quota full), now non-fatal | `.github/workflows/ci.yml` |
| `SabhaMujic.Client`: `ISabhaClient` abstraction for desktop view models | Done | Compiles |

## M2 Data & storage: merged
- SQLite with WAL, versioned migrations, and repositories for users, sessions, devices, pairing codes, categories, tracks, analysis, playlists, playback state, settings and audit.
- `ProtectedStore`: content-addressed storage, path-traversal-safe keys, atomic writes.
- `UploadStaging`: chunked admin uploads with enforced limits.
- Evidence: 111 Data tests pass on Linux.
- Follow-ups for M3: rate-limit login and pairing; periodic purge/expiry job; `ResetInterruptedAsync` at startup; Windows `IStoreAcl` on both the library and uploads folders.

## M4a WPF shell: merged
- `SabhaMujic.Desktop.ViewModels` (Shell, Login, Library, NowPlaying, Queue, Playlists, Admin: import, categories, tracks/loop override, users, devices/pairing QR), a WPF app (main window, mini-player, theme, shortcuts), and a `--demo` in-memory client.
- Evidence: 131 Desktop tests pass on Linux. XAML compiles. **The WPF runtime is unverified**; see `docs/WINDOWS_TEST_CHECKLIST.md`.
- Remaining for M4: use the real pipe client (M3), and switch the queue/playlist title lookups to `GetTracksByIdsAsync`.

## M3a Server application layer: merged
- Auth service: login with rate limiting, sessions, pairing, first-run admin. Library, playlist, admin and import (orchestration) services.
- `ApiSession`: the authorization choke point, shared by the pipe and (later) the remote.
- `EventBroadcaster` (bounded, coalescing, per-role filtering), maintenance jobs, `PipeRpcServer`, `PipeSabhaClient` (reconnect + session resume). The desktop now uses the real pipe client.
- Evidence: 204 Server tests pass on Linux over a real pipe (Unix domain socket), including the exhaustive authorization matrix with zero side effects on denial, and playback continuing when all clients disconnect (fake backend).
- Unverified: the Windows pipe ACL and impersonation (M3b Service).

## M1 Audio engine: merged
- Decoding (WAV everywhere, MediaFoundation on Windows), silence detection, Smart Loop analysis (analyzes 5 min of audio in 0.5 s in Release), the equal-power `CrossfadeMixer` with buffered voices, the `PlaybackEngine` (queue, state machine, Smart Loop, repeat, skipping unreadable files), `NullRealtimeOutput`, `WasapiOutput`, and the `tools/SabhaMujic.AudioSpike` manual test tool.
- Evidence: 46 Audio tests pass on Linux. Planted loop found exactly (36.000 s). Crossfade power within ±0.5 dB. Zero render-thread allocations over 10 s with crossfades and seams.
- **Unverified:** MediaFoundation decoding, WASAPI playback and device loss (8 Windows-only tests run on CI, which has no audio device), audible quality, and loop detection on real devotional recordings.

## M3b Engine wiring + Windows service: merged
- First-run setup: elevated Windows admin on the desktop pipe only. `EnginePlaybackBackend` (non-blocking dispatch, ~5 s heartbeat, safe deletion of the playing track), `StoreTrackResolver`, `PlaybackPersistence`, `DecoderAudioProbe`, `AnalysisWorker`, `SabhaHost`, `tools/SabhaMujic.DevHost`.
- Windows service: `WindowsStoreAcl`, `WindowsPipeStreamFactory`, `WindowsCallerIdentity`, rolling file log.
- Evidence (Linux): a full-stack integration test with the real engine runs import → analysis → play → crossfade → Smart Loop → all clients disconnected (playback continues) → reconnect → restart restore → delete the playing track → corrupt import. 606 tests green.
- Windows tests (temporary standard user denied the files, can connect to the pipe, can't squat it): pending the CI result.

## M5 iPhone remote: merged
- Local CA + rotating leaf certificate, bootstrap page + `.mobileconfig`, fragment-only pairing code, cookie auth with CSRF checks, the local-network peer filter, a SignalR hub, chunked admin uploads, and the mobile web UI (Now / Queue / Library / Playlists / Admin) with live sync and reconnect.
- Evidence (Linux): 155 remote .NET tests, including the exhaustive hub/HTTP authorization matrix with zero side effects, revoke closing live connections, and playback continuing when all remote connections drop. 7 Playwright e2e tests (Chromium, iPhone 13 emulation) pass. `tsc --checkJs` is clean.
- **Unverified:** real iPhone Safari, CA profile install/trust, SChannel with the leaf under the service account. `RemoteHost` isn't yet started by the Windows service (integration step below).

## Backlog for M6
- Engine `RefreshTrackAsync` (override applies to the live voice) and `RestoreQueueAsync` (keep ids and position). Data migration 0002 for full analysis fidelity.
- `GetManyAsync` batch track lookup in Data. Desktop queue/playlist views should use `GetTracksByIdsAsync`.
- Installer: create ProgramData + ACLs, Event Log source, launch the app elevated on finish, firewall rules.
- Integration: start `RemoteHost` from the service as an `IHostedComponent` with a DPAPI `ISecretProtector` and an ACL'd `certs` folder; show the CA fingerprint on the desktop pairing screen; `/api/logout` unpairs (revokes) the device instead of leaving an orphaned token; DevHost `--remote` for manual e2e against the real engine.

## Next
- M1 Audio engine (Agent 1) and M2 Data/storage (Agent 3), in parallel worktrees, once Windows CI is green.
- M4a WPF shell against a fake client (Agent 2) can start alongside.

## Blockers
None.
