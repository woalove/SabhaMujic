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
