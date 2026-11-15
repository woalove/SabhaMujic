# Architecture decisions

The full design is in `IMPLEMENTATION_PLAN.md` §1–3. This file records decisions and every later change, newest last.

| # | Date | Decision | Reason |
|---|------|----------|--------|
| 1 | 2026-11-15 | A Windows service (virtual account `NT SERVICE\SabhaMujic`) owns playback, the SQLite DB and the protected library folder. Clients are thin. | Playback must survive client disconnects. Volunteers must not reach the files. |
| 2 | 2026-11-15 | File protection is the folder ACL only (service + Administrators), with no encryption at rest. | User choice. Windows administrators are trusted. |
| 3 | 2026-11-15 | App-level roles (`Admin`, `Volunteer`) decide capabilities. The Windows identity is only logged for audit. | Volunteers often share one Windows login. |
| 4 | 2026-11-15 | Desktop ↔ service: named pipe + StreamJsonRpc. Remote ↔ service: HTTPS (Kestrel) + SignalR. Both call one application layer through one `Authorizer`. | Prevents the two transports from drifting into different permission rules. |
| 5 | 2026-11-15 | Remote TLS uses an app-generated local CA, trusted once per iPhone through a `.mobileconfig` profile served on a plain-HTTP bootstrap page. | User choice. Avoids Safari warnings and flaky WebSockets on untrusted certificates. |
| 6 | 2026-11-15 | Smart Loop automatically detects a loop region (self-similarity on beat-synchronous chroma + energy). Low confidence falls back to the whole trimmed track. Admins can override. | User choice. |
| 7 | 2026-11-15 | .NET 10 LTS, self-contained x64. NAudio 2.x. WiX v5 MSI built on GitHub Actions Windows. | LTS support window. Linux cannot build an MSI. |
| 8 | 2026-11-15 | Solution file is `SabhaMujic.slnx` (the .NET 10 default XML format). | Default for .NET 10 `dotnet new sln`. Supported by VS 2022 17.13+ and the .NET 9.0.200+ CLI. |

## Contract versioning
`SabhaMujic.Contracts.ContractVersion.Current` is checked in the handshake (`IAuthApi.HelloAsync`). Bump it on any breaking change to DTOs or RPC signatures.

## Playback state machine
States: `Idle`, `Loading`, `Playing`, `Paused`, `Crossfading`, `Stopped`, `Error`.
The transition table is defined in code in `SabhaMujic.Core.Playback.PlaybackStateMachine` and covered exhaustively by `PlaybackStateMachineTests`. The code is the spec.

## Later decisions
| # | Date | Decision | Reason |
|---|------|----------|--------|
| 9 | 2026-11-15 | NAudio **3.3.0** (`NAudio.Core` + `NAudio.Wasapi`), not 2.x as the plan said. `NAudio.Wasapi` 22.0.0 on NuGet is ignored. | 3.3.0 is the current stable release. The 22.0.0 package looks mis-published (generic description, depends on Core 2.2.0). |
| 10 | 2026-11-15 | Volunteers may create and edit playlists and the queue. Library, loop-point, account and device management are admin-only. | Volunteers run sessions day to day. Matrix in `Core/Security/Authorizer.cs`. |
| 11 | 2026-11-15 | Central package versions in `Directory.Packages.props`. | Keeps versions consistent across projects that different agents own. |
| 12 | 2026-11-15 | `ImportService` (decode validation + analysis) lives in Server (M3). Data provides `ProtectedStore` + `UploadStaging` only. | Data must not depend on Audio. |
| 13 | 2026-11-15 | Storage conventions: GUID = lowercase TEXT, timestamps = INTEGER Unix ms UTC, durations = REAL seconds. Case-insensitive names and search use C#-folded `*_key` columns (NFKC + upper). LIKE input is escaped. | Deterministic round-trips. Unicode-correct matching, beyond ASCII. Details in `Migrations/0001_initial.sql`. |
| 14 | 2026-11-15 | Too many concurrent uploads → `ErrorCode.RateLimited`. Sessions are revoked by timestamp and purged later. Loop overrides are stored separately from analysis. | Re-analysis must never wipe an admin override. |
| 15 | 2026-11-15 | Contract additions (still v1, pre-release): `ILibraryApi.GetTracksByIdsAsync`; `ServerEpoch` init-property on `PlaybackStateDto`/`QueueDto` (resets client revision tracking after a service restart); `LibraryChangeKind.UsersChanged/DevicesChanged` (admin sessions only); `ImportStages` constants; documented the semantics of `ResumeAsync`, `MoveQueueItemAsync`, `ISabhaClient.Session` and `ConnectAsync`, and the push rules. | Proposals from the M4a review. Non-positional additions, so in-flight engine code is unaffected. `ContractVersion` stays 1 until the first release, which freezes it. |
| 16 | 2026-11-15 | Desktop view models live in the cross-platform `SabhaMujic.Desktop.ViewModels` (net10.0) library. WPF only holds views and adapters. `DemoSabhaClient` is a dev/demo tool, only used with `--demo`. | View models stay unit-testable on Linux. |
| 17 | 2026-11-15 | The wire protocol (JSON-RPC method names `"<Interface>.<Method>"`, header-delimited framing with a size guard, errors as code -32000 carrying `ErrorInfo`) lives in `SabhaMujic.Client/Rpc`. Server references Client, so both sides share it. | One definition of the protocol. No drift between service and desktop. |
| 18 | 2026-11-15 | `ApiSession.Run` is the authorization choke point. It finds the operation via `[CallerMemberName]` + `OperationMap`, re-validates the session, then calls `Authorizer.Demand` before the body runs. It's reused by the M5 remote. Unexpected exceptions become `Internal` with a fixed message. | Structurally hard to bypass. An exhaustive real-pipe test (51 methods × 3 roles) also asserts that denials have zero side effects. |
| 19 | 2026-11-15 | Auth policy: 5 failed logins per user name and 50 globally per 5 min. Unknown users still run one PBKDF2 check. Desktop and remote user sessions slide over 12 h. Device tokens last until revoked. A password reset revokes the target user's sessions. You can't delete yourself or the last admin. Pairing: 5 failures per caller and 20 globally per 5 min. | Brute-force resistance for 6-digit codes and PINs. |
| 20 | 2026-11-15 | `ErrorCode.Unavailable` (client-side: not connected or connection lost) was added, so `Internal` means only genuine server faults. | Proposal from the M3a review. |
| 21 | 2026-11-15 | Remote web app: plain ES modules + JSDoc, type-checked with `tsc --checkJs`, embedded as resources in Server. The SignalR browser client is vendored. This replaces the plan's "TypeScript + Vite". | No build step at install time and no Node on the Windows build path. Same type safety. |
| 22 | 2026-11-15 | The pairing QR encodes `http://<ip>:8080/#code=NNNNNN`. The fragment is never sent over plain HTTP, and the bootstrap page carries it to `https://…/pair#code=`. Device tokens live in HttpOnly + Secure + SameSite=Strict cookies, with a custom-header + Origin CSRF check. The remote rejects non-private source IPs. | Keeps codes and tokens off the wire and away from JS. Trust in the CA is TOFU on the local network; its fingerprint is shown on the desktop and on the bootstrap page for comparison. |
