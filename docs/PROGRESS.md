# Progress

_Last updated: 2026-11-15 (M0 done; M2, M4a, M3a merged; M1 in progress)_

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

## Next
- M1 Audio engine (Agent 1) and M2 Data/storage (Agent 3), in parallel worktrees, once Windows CI is green.
- M4a WPF shell against a fake client (Agent 2) can start alongside.

## Blockers
None.
