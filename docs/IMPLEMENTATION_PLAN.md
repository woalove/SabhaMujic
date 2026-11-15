# SabhaMujic — Implementation Plan

## Context
SabhaMujic is a Windows desktop music player for sabha (assembly) volunteers. Volunteers browse categorized tracks and play them with seamless **Smart Loop** and **equal-power crossfades**. Administrators upload music. An **iPhone web remote** controls playback over the local Wi-Fi. Volunteers must not be able to copy the protected music files. The repo is empty (README only), so this is a greenfield build.

**Decisions already made (from your answers):**
- **File protection:** a Windows service owns a folder that only it and Administrators can read (ACL only). Files stay plain audio, with no encryption at rest.
- **Remote transport:** HTTPS with an app-generated local CA. Each iPhone trusts the CA once, through a QR link.
- **Smart Loop:** automatic. Trim silence, skip the intro/outro, and find the best-matching loop region by self-similarity, then loop it seamlessly with a short crossfade. Admins can override the loop points.
- **Windows CI:** GitHub Actions `windows-latest` builds, tests and packages the MSI.
- **Cloud environment:** limited network (package registries, NuGet, GitHub), .NET 10 SDK installed by the setup script. Verified: WPF compiles here with `EnableWindowsTargeting`, and NAudio.Core, SQLite and xUnit tests run on Linux.

**Status:** Approved 2026-11-15. Changes to approved components need a recorded reason in docs/ARCHITECTURE.md.

---

## 1. Architecture

```
 Volunteer / Admin (standard Windows user)          iPhone (Safari, same Wi-Fi)
 ┌───────────────────────────────┐                 ┌──────────────────────────┐
 │ SabhaMujic.Desktop (WPF, MVVM)│                 │ Web remote (static PWA)  │
 │ library · queue · playlists   │                 │ controls · queue · admin │
 │ mini-player · admin import    │                 │ upload                   │
 └──────────────┬────────────────┘                 └────────────┬─────────────┘
   Named pipe (StreamJsonRpc, per-user ACL)           HTTPS :8443 (local-CA cert)
                │                                    REST + SignalR (real-time)
 ┌──────────────▼────────────────────────────────────────────────▼─────────────┐
 │ SabhaMujic.Service  — Windows Service, virtual account NT SERVICE\SabhaMujic │
 │  Adapters: PipeRpcServer │ RemoteApi (Kestrel + SignalR) │ PairingService      │
 │  ── Application layer (single authorization choke point) ─────────────────── │
 │   AuthService (sessions, roles) · PlaybackController · LibraryService ·       │
 │   PlaylistService · ImportService · CertificateAuthority                      │
 │  ── Engine ───────────────────────────────────────────────────────────────── │
 │   PlaybackEngine (state machine, queue, Smart Loop scheduler)                 │
 │   AudioGraph: Decoder → SilenceTrim → LoopRegion → CrossfadeMixer → Volume    │
 │   IAudioOutput: WasapiOutput (Windows) │ NullRealtimeOutput (tests/Linux)     │
 │  ── Persistence ──────────────────────────────────────────────────────────── │
 │   SQLite (ProgramData\SabhaMujic\db)  ·  ProtectedStore (…\Library, ACL)       │
 └──────────────────────────────────────────────────────────────────────────────┘
```

**Key principles**
- **The service owns playback, files and authorization.** The desktop UI and the remote are thin clients. If either disconnects or crashes, playback continues.
- **One application layer.** The pipe and HTTP adapters both call the same `I*Service` interfaces, and every command passes one `Authorizer`. This prevents the two transports from drifting into different permission rules.
- **Everything except WPF, WASAPI, ACL and pipe-identity code targets plain `net10.0`.** That code runs and is tested on Linux. Windows-only code sits behind small interfaces (`IAudioOutput`, `IAudioDecoderFactory`, `IStoreAcl`, `ICallerIdentity`).

## 2. Technology decisions & tradeoffs
| Area | Choice | Why / tradeoff |
|---|---|---|
| Runtime | .NET 10 LTS, self-contained x64, Win10 22H2+/Win11 | LTS until Nov 2028. Self-contained means no runtime install on sabha PCs, at the cost of a ~70 MB larger MSI. |
| Audio | NAudio 2.x: `NAudio.Core` for DSP (`ISampleProvider`); `NAudio.Wasapi` + `MediaFoundationReader` on Windows | Mature library. MediaFoundation decodes MP3/AAC/M4A/WMA/FLAC/WAV on Win10+, so no ffmpeg is needed. Linux tests decode WAV only. |
| Output | WASAPI shared mode, 100 ms latency, event-driven | Robust and simple. Exclusive mode isn't needed. |
| DSP | Custom `ISampleProvider` chain, 32-bit float, 48 kHz internal mix (resample via `WdlResamplingSampleProvider`) | One sample rate keeps crossfades sample-accurate. |
| IPC | Named pipe + **StreamJsonRpc** (Microsoft; JSON-RPC with notifications) | Request/response plus server-pushed events with no hand-written framing. The pipe ACL limits it to interactive users. |
| Remote | ASP.NET Core Kestrel (inside the service) + **SignalR** + static mobile web app (TypeScript + Vite, no framework) | One process. SignalR handles reconnects. Vanilla TS keeps the bundle small for older iPhones. |
| TLS | Built-in local CA (`System.Security.Cryptography.X509Certificates`): ECDSA P-256 CA (10 y), leaf ≤ 397 days with SAN = LAN IPs + `hostname.local`, re-issued when the IP changes | Meets iOS certificate rules. The CA key is DPAPI(machine)-protected and ACL'd to the service. |
| DB | SQLite via `Microsoft.Data.Sqlite` + Dapper, hand-written versioned migrations, WAL mode | Small, transparent, testable. |
| Auth | App accounts with role `Admin`/`Volunteer`; PBKDF2-SHA256 (600k) hashed passwords/PINs; opaque 256-bit session tokens (stored hashed); remote devices paired via 6-digit one-time code + QR (5-minute expiry); per-device revocation | Volunteers can share one Windows login; the app role, not the Windows account, decides capability. The Windows caller identity is still logged for audit. |
| UI | WPF + CommunityToolkit.Mvvm; resizable main window; compact always-on-top mini-player window | Native, mature MVVM tooling. |
| Installer | **WiX v5** (`WixToolset.Sdk`), built on Windows CI; installs the service, folder ACLs, firewall rule (Private profile, TCP 8443 + 8080 bootstrap), Start-menu shortcut | Standard MSI, supports upgrades. Unsigned at first, so SmartScreen warns until a code-signing certificate is added. |
| Tests | xUnit + FluentAssertions-free plain asserts; Playwright (Chromium, iPhone emulation) for the remote | Playwright WebKit isn't installed here, so real-Safari checks are manual. |

**Protection boundary (honest statement):** Volunteers run as standard Windows users and cannot list or read `C:\ProgramData\SabhaMujic\Library`. Its ACL grants access to `NT SERVICE\SabhaMujic` and `Administrators` only, with inheritance disabled. The service never hands out file paths or raw file bytes; it only produces audio output. Windows administrators can still reach the files, which is accepted. There's no DRM: someone could still record the sound with a loopback recorder.

## 3. Smart Loop & crossfade design (Audio)
- **Silence detection:** RMS over 20 ms windows; threshold −50 dBFS (configurable), minimum 300 ms. Produces leading/trailing trim points and internal gaps. Trim points snap to the nearest zero-crossing.
- **Loop region analysis** (offline, at import, streaming so memory stays bounded):
  1. Compute per-frame features at a ~46 ms hop: RMS energy, onset strength, and a 12-bin chroma from FFT (NAudio `FastFourierTransform`).
  2. Run tempo/beat estimation by autocorrelating onset strength.
  3. Build a self-similarity matrix over beat-synchronous features. Find a candidate pair (start A, end B), with B − A ≥ max(15 s, 8 beats) and A ≥ the end of the intro, that maximizes feature similarity around A and B and minimizes the energy jump. The score uses cosine similarity of chroma + energy over a ±2-beat window.
  4. Refine A/B to the zero-crossing nearest the best sample-level cross-correlation alignment within ±20 ms.
  5. Output a `LoopRegion {Start, End, CrossfadeMs (50–400), Confidence}`. If confidence falls below a threshold, fall back to the whole trimmed track with a longer crossfade. The result is cached in SQLite per content hash and per analysis version.
- **Equal-power crossfade:** `gainOut = cos(t·π/2)`, `gainIn = sin(t·π/2)`. It's used between queue items (default 6 s, configurable 0–12 s) and at loop seams (short, from the analysis). The fade-out start is scheduled from the trimmed end, so silence never sits inside a crossfade.
- **Gapless scheduler:** the engine pre-opens the next decoder ≥ 15 s ahead. Crossfades are sample-accurate inside a single mixer, with no timer-based switching. Buffers are pooled, so the steady-state playback loop doesn't allocate.

## 4. Project structure
```
SabhaMujic.sln
Directory.Build.props        # net10.0, nullable, warnings-as-errors, EnableWindowsTargeting
src/
  SabhaMujic.Contracts/      # DTOs, RPC interfaces, events, error codes (net10.0) — FROZEN FIRST
  SabhaMujic.Core/           # domain: Track, Category, Playlist, Queue, PlaybackState machine, Authorizer
  SabhaMujic.Audio/          # DSP: silence, loop analysis, crossfade, mixer, engine (net10.0)
  SabhaMujic.Audio.Windows/  # WasapiOutput, MediaFoundation decoders (net10.0-windows)
  SabhaMujic.Data/           # SQLite schema, migrations, repositories
  SabhaMujic.Server/         # application services, pipe RPC server, Kestrel/SignalR, pairing, local CA (net10.0)
  SabhaMujic.Service/        # Windows Service host + Windows impls (ACL, pipe identity) (net10.0-windows)
  SabhaMujic.Desktop/        # WPF app (net10.0-windows)
  SabhaMujic.Remote.Web/     # TypeScript/Vite mobile UI → built into Server wwwroot
tests/
  SabhaMujic.Core.Tests/  SabhaMujic.Audio.Tests/  SabhaMujic.Data.Tests/
  SabhaMujic.Server.Tests/   # in-proc host w/ NullRealtimeOutput: IPC + HTTP + authz integration
  SabhaMujic.Windows.Tests/  # Windows-only: ACL, pipe identity, WASAPI/MF smoke (CI windows)
  remote-e2e/                # Playwright tests against headless Linux host
installer/SabhaMujic.Installer/   # WiX v5
tools/devhost/               # run Server headless on Linux (null audio) for dev + e2e
.github/workflows/ci.yml     # ubuntu (build+tests+e2e) and windows (build+all tests+MSI artifact)
docs/  CLAUDE.md
```

## 5. Task dependency graph
```
M0 Contracts + skeleton + CI ─┬─► M1 Audio DSP & engine ───────────┐
                              ├─► M2 Data + ProtectedStore + Import ┼─► M3 Service host, IPC, AuthZ ─┬─► M4 WPF desktop (full)
                              ├─► M4a WPF shell vs FakeClient ──────┘                                ├─► M5 Remote + local CA + pairing
                              └─► M5a Remote UI prototype vs mock ─────────────────────────────────┘
                                                                     M4 + M5 ─► M6 Hardening, perf, installer, docs
```
Contracts are frozen at the end of M0. Any later change goes through the lead, and the version bumps.

## 6. Milestones, tasks & acceptance criteria
**M0 Foundations (lead)**: solution, `Directory.Build.props`, `Contracts` (RPC interfaces `IPlaybackApi`, `ILibraryApi`, `IPlaylistApi`, `IAuthApi`, `IAdminApi`; events `PlaybackStateChanged`, `QueueChanged`, `LibraryChanged`), the state machine spec, CI workflow, `CLAUDE.md` + docs.
*Accept:* `dotnet build` is green on Linux and on Windows CI, and the contract docs are written.

**M1 Audio (Agent 1)**: silence detector, loop analyzer, equal-power crossfade provider, mixer, `PlaybackEngine` (Idle/Loading/Playing/Paused/Crossfading/Stopped; play/pause/stop/next/prev/seek/loop toggle/volume), `NullRealtimeOutput`, `WasapiOutput`, MF decoder factory, and a spike exe that plays audio from a Windows service.
*Accept:*
- Unit tests pass on synthetic signals.
- Crossfade power stays within ±0.5 dB for uncorrelated signals.
- No sample discontinuity > threshold at seams.
- Loop analysis finds the planted loop within ±20 ms on generated fixtures (intro + 4× repeated pattern + outro).
- Steady-state playback makes zero allocations (benchmark).

**M2 Data & storage (Agent 3)**: schema (users, sessions, devices, categories, tracks, track_analysis, playlists, playlist_items, queue, settings, audit_log), migrations, repositories, `ProtectedStore` (content-addressed `<sha256>.<ext>`, atomic write via temp + rename), and `ImportService` (validate the format by actual decoding, hash, store, analyze in the background).
*Accept:* repository tests run on SQLite in-memory/temp, and import is idempotent on duplicates.

**M3 Service & security (Agent 3, lead reviews)**: application services, `Authorizer` (a role matrix that is the single source of truth), pipe server (ACL: Authenticated Users connect / SYSTEM + service full; caller SID captured), Windows service host, store ACL setup, rate-limited login, and audit log.
*Accept:*
- Authorization tests cover the role matrix exhaustively for every RPC and HTTP endpoint.
- The Windows CI test proves a standard user token cannot open the library folder.

**M4 WPF desktop (Agent 2)**: login, library with category sidebar + search, track list, now-playing bar (seek, volume, loop toggle, crossfade setting), queue view (drag reorder), playlists CRUD, mini-player window, admin screens (import with progress, categories, users, paired devices, loop-point override). Reconnects to the service automatically.
*Accept:* view-model unit tests run on Linux, the app compiles here, and the manual Windows checklist passes.

**M5 Remote (Agent 4)**:
- Local CA + leaf issuance.
- HTTP :8080 bootstrap page that serves only the CA profile (`.mobileconfig`) and instructions.
- HTTPS :8443 API + SignalR hub.
- Pairing: desktop shows QR (URL + one-time code) → device token.
- Mobile UI: now playing, controls, queue, browse/search, playlists.
- Admin upload via chunked upload with size/type limits, through `ImportService`.

*Accept:*
- Playwright e2e (iPhone 13 viewport, Chromium) covers pairing, control and live sync between two clients.
- A volunteer device gets 403 on uploads.
- Killing all clients mid-track doesn't stop playback (integration test).

**M6 Hardening & delivery (Agents 5 + 6)**:
- Soak test: 8 hours of simulated play/crossfade/loop on the null output, accelerated. Watch memory and handle growth.
- Fault tests: corrupt file, device unplug (Windows), DB locked, service restart.
- WiX MSI with service install, ACLs and firewall.
- `docs/SETUP.md`, `docs/ADMIN_GUIDE.md`, `docs/TROUBLESHOOTING.md`, `docs/WINDOWS_TEST_CHECKLIST.md`.

*Accept:* the CI produces an MSI, and every Definition of Done item has evidence.

## 7. Subagent assignments & orchestration
| Agent | Owns (exclusive write) | When |
|---|---|---|
| Lead (me) | `Contracts`, `Core`, `Directory.Build.props`, CI, docs, all merges | M0 + continuous |
| 1 Audio | `Audio`, `Audio.Windows`, `Audio.Tests` | M1 (parallel with M2, M4a) |
| 3 Backend/Security | `Data`, `Server` (services, pipe, authz), `Service`, their tests | M2 → M3 |
| 2 WPF UI | `Desktop` | M4a with a fake client during M1/M2, then M4 |
| 4 Remote | `Remote.Web`, `Server/Remote/*`, `remote-e2e` | M5a prototype, then M5 after M3 |
| 5 QA | `Server.Tests` integration, soak, `Windows.Tests` | M3 onward, review passes |
| 6 Build/Docs | `installer/`, user-facing docs | M6 (installer skeleton may start in M3) |

- **At most 2–3 agents run concurrently, each in its own git worktree, with disjoint file ownership.** The lead merges into `claude/awesome-cerf-aa8wbt`.
- **Every brief includes** exact deliverables, acceptance tests, and files the agent must not touch. Each agent reports changed files, test results, assumptions, blockers and open issues.
- **The lead reviews every diff, reruns the tests, and checks the Windows CI run (via GitHub Actions logs) before marking a task done.** Security-sensitive code (authz, pipe ACL, CA, pairing, upload validation) gets a dedicated lead review plus `/security-review`.

## 8. Testing strategy
- **Unit (Linux):** DSP on synthetic + sox-generated WAV fixtures; state machine transitions, including illegal ones; queue/playlist logic; the authorizer role matrix; PBKDF2/token handling; certificate issuance (SAN, validity, EKU).
- **Integration (Linux):** in-proc server with `NullRealtimeOutput` (the clock can run faster than real time) exercised through the real StreamJsonRpc-over-pipe (Unix-domain socket on Linux) and real HTTPS. This covers:
  - authorization boundaries per role
  - remote disconnect continuity
  - concurrent commands from desktop and remote
  - upload limits
- **E2E remote:** Playwright Chromium with iPhone emulation against `tools/devhost`.
- **Windows CI:** everything above, plus ACL enforcement with a created standard-user token, the pipe caller identity, MediaFoundation decode of MP3/M4A/FLAC fixtures, WPF build, MSI build, and a silent MSI install/uninstall smoke test (service starts, `/health` responds).
- **Manual on real hardware (`docs/WINDOWS_TEST_CHECKLIST.md`):** audible crossfade/loop quality, WASAPI device changes, iPhone Safari CA install + control, firewall prompt, resize/mini-player behaviour. Anything not verified this way is reported as **unverified**.
- **Results** are recorded in `docs/TESTING.md`.

## 9. Key risks
1. **A Windows service in session 0 might not get reliable audio output.** M1 spike on CI + your PC. Fallback: run the engine in a per-machine "SabhaMujic Player" host launched at logon under the service account, with the same IPC.
2. **iOS trust of the local CA requires manual profile install + "Full Trust" toggle.** Mitigated with a step-by-step bootstrap page with screenshots.
3. **LAN IP changes break the certificate SAN.** Mitigated by re-issuing on IP change and including `hostname.local` (mDNS) in the SAN.
4. **Loop detection quality on real devotional music** (rubato, live recordings). Mitigated by confidence fallback + admin override.
5. **Unsigned MSI triggers SmartScreen.** Documented; add code signing later (a secret on GitHub Actions, not in this environment).

## 10. Definition of Done
A task is done when:
- the code builds with no warnings, and its tests are written and passing on Linux and Windows CI (where applicable)
- the lead has reviewed it
- security-relevant changes have had the security review
- docs (`PROGRESS.md`, `TESTING.md`, and `ARCHITECTURE.md` if decisions changed) are updated
- it's committed and pushed

The project is done when every Step 8 completion criterion has either automated evidence or a completed manual checklist entry. Windows behaviour that hasn't been checked on real Windows is labelled unverified, never "done".

