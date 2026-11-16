# Testing

## How to run
- All platforms: `dotnet test SabhaMujic.slnx`
- Windows-only tests (`tests/SabhaMujic.Windows.Tests`) run only on Windows. On Linux they are excluded via `IsTestProject`.

## Latest results
| Date | Where | Result |
|---|---|---|
| 2026-11-15 | Linux cloud env | Core.Tests: 74 passed, 0 failed |
| 2026-11-15 | Linux cloud env (after M2 merge) | Core 74 + Data 111 passed, 0 failed |
| 2026-11-15 | Linux cloud env (after M4a merge) | Core 74 + Data 111 + Desktop 131 passed, 0 failed |
| 2026-11-15 | Linux cloud env (after M3a merge) | Core 74 + Data 111 + Desktop 131 + Server 204 = 520 passed, 0 failed |
| 2026-11-15 | Linux cloud env (after M1 merge) | Core 74 + Data 111 + Desktop 131 + Server 204 + Audio 46 = 566 passed, 0 failed |
| 2026-11-15 | Linux cloud env (after M3b merge) | Core 76 + Audio 46 + Desktop 144 + Data 111 + Server 229 = 606 passed, 0 failed |
| 2026-11-15 | GitHub Actions run 7 (M3a) | Linux + Windows success: pipe/server tests pass over real Windows named pipes |
| 2026-11-15 | Linux cloud env (after M5 merge) | .NET: Core 76 + Audio 46 + Desktop 144 + Data 111 + Server 384 = 761 passed, 0 failed. Remote web: `tsc` clean; Playwright e2e 7/7 (Chromium, iPhone 13 emulation) |
| 2026-11-15 | GitHub Actions run 8 (M1) | Windows: 2 MediaFoundation tests failed (seek accuracy bug, wrong crossfade timing in a test); fixed in e5cbb7a |
| 2026-11-16 | **GitHub Actions run 32 (3d07449)** | **All three jobs green:** Linux build + tests; Windows build + tests (incl. Windows-only ACL/pipe/DPAPI/SChannel/service tests); Windows installer (MSI build, silent install, standard-user root takeover, junction refusal, uninstall keeps data, reinstall, upgrade, downgrade refused) |
| 2026-11-16 | GitHub Actions run 30 (e2a055a) | **Windows tests job and installer job green.** Linux: two rare test-side failures, both diagnosed: background-GC false positive in the allocation measurement (#39); SignalR attach race in a hub test (the test now waits for server-side attach like the web app does) |
| 2026-11-16 | GitHub Actions run 29 (a4a6652) | **Windows.Tests 20/20** incl. console-hosted service over the ACL'd pipe and 50 sequential + 10 concurrent connects (pipe hardening #38 verified). Installer job green. Server 434/435 (1 skipped: disk-full needs tmpfs), Core, Data, Desktop green. One Audio timing assertion was too strict for a loaded runner (fixed in the next commit) |
| 2026-11-16 | GitHub Actions run 26 (77cfb49) | Linux and Windows test jobs **fully green** (Windows: Core 80, Audio 61+53, Data 116, Desktop 171, Windows.Tests 19, Server 431) |
| 2026-11-16 | GitHub Actions run 27 (43db648) | **Installer job green incl. the `--prepare-data-root` takeover of a standard-user-owned root and junction refusal.** Linux green. Windows: one intermittent failure, `Console_hosted_service_answers_over_the_acl_pipe` (passed in run 26), under investigation as a possible intermittent pipe-connect defect |
| 2026-11-16 | GitHub Actions run 21 | **Windows installer job passed**: MSI build (WiX 5.0.2), silent install (service as `NT SERVICE\\SabhaMujic`, delayed auto-start, failure actions, pipe, protected folders, firewall Private rules, Event Log source, shortcuts, ARP), uninstall keeps data, reinstall, upgrade, downgrade refused. The test jobs hung in Server.Tests → a dispose/connect race in `PipeSabhaClient`, fixed (ARCHITECTURE #37) |
| 2026-11-16 | Linux, pinned to 2 cores (CI-like) | Server.Tests 431 passed in 5/5 runs after the race fix (previously about 1 in 3 hung) |
| 2026-11-16 | Linux cloud env (after M6d merge) | .NET 852 passed, 0 skipped (Core 80, Desktop 171, Audio 55, Data 116, Server 430), 4 consecutive full-suite runs. Remote web e2e 8/8 |
| 2026-11-16 | Linux cloud env (after M6c merge) | .NET 803 passed, 3 skipped (bug repros BUG-01..03 pending fixes) |
| 2026-11-16 | GitHub Actions run 17 | Windows: all tests passed, incl. the remote suite (Exportable fix), DPAPI and the SChannel handshake. MSI build failed ICE60 (fixed in f1e2549) |
| 2026-11-16 | Linux cloud env (after M6a merge) | .NET 794 passed (Core 78, Audio 52, Desktop 162, Data 116, Server 386). Remote web: typecheck clean, e2e 8/8 |
| 2026-11-15 | GitHub Actions run 11 (M3b + seek fix), **Windows** | Windows.Tests 8/8 passed: exact store DACL; **a temporary standard user is denied the protected folders**, can connect to the pipe and can't create a pipe instance (anti-squatting); elevation detection; console-hosted service completes first-run setup over the ACL'd pipe. Audio (net10.0-windows) 53 passed / 1 skipped (sox): MediaFoundation MP3/AAC/WMA/FLAC/WAV decoding, exact seeks, engine crossfade. Server 229 passed over real named pipes. One failure: the wall-clock analysis perf test (5.006 s vs a 5 s budget, under parallel load), now run in its own CI step |
| 2026-11-15 | GitHub Actions run 3 (Linux + Windows) | Build, tests (incl. Windows smoke test), self-contained publish: success |

## Verification status of completion criteria (Step 8)
Legend: **Verified (CI)** = automated evidence on real Windows (GitHub Actions `windows-latest`); **Verified (Linux)** = automated evidence on Linux only; **Unverified** = needs `docs/WINDOWS_TEST_CHECKLIST.md` on a real PC.

| Criterion | Status | Evidence |
|---|---|---|
| Windows desktop app runs | **Unverified** | Builds, publishes and installs on Windows CI; installer shortcuts verified. No CI runner can show a WPF window, so the first launch is a manual checklist item. View models: 171 tests (Linux + Windows). |
| Volunteers browse and play categorized tracks | Verified (Linux + CI service tests); UI **unverified** | Full-stack integration over real pipes on Linux and Windows CI. The WPF screens need the manual checklist. |
| Smart Loop and crossfade with real audio | Partly verified | DSP and engine on synthetic signals (planted loop found exactly, ±0.5 dB equal-power crossfade, zero render allocations). MediaFoundation MP3/AAC/WMA/FLAC decoding **Verified (CI)**. Audible quality on real devotional recordings and WASAPI output: **unverified**. |
| Playlists and queues | Verified (Linux + CI) | Unit, server, integration and soak tests; restore of queue and position after restart. |
| Volunteers can't access protected files | **Verified (CI)** | A real standard user (`LogonUser`) is denied listing and reading the protected folders. The exact DACLs, the takeover of a pre-created root and junction refusal are checked by the installer smoke test. Authorization matrix: zero side effects over the pipe and the remote. |
| Admin-only uploads | Verified (Linux + CI) | Pipe and HTTPS upload paths; volunteer and anonymous attempts denied with nothing staged (incl. under stress). |
| iPhone remote controls playback over the LAN | Verified (Chromium iPhone emulation + Windows CI TLS); real iPhone **unverified** | 8 Playwright e2e tests, 155+ remote .NET tests; SChannel TLS with DPAPI-loaded certificates **Verified (CI)**. Safari profile install/trust: manual checklist. |
| Playback continues when the remote disconnects | Verified (Linux + CI) | Real engine + `RemoteHost` + SignalR phone: position keeps advancing after every hub connection is cut; same for pipe clients; soak windows with zero clients. |
| Normal resizable window | **Unverified** | Implemented (min 760×480, mini-player); manual checklist. |
| Performance and stability tested | Verified (Linux, accelerated) | 8-simulated-hour soak, 0 invariant violations, no leak trend; 20-client stress; fault injection. A 24 h real-time Windows soak is **not done**. |
| Installer and setup documentation | **Verified (CI)** | MSI build; silent install with the service under `NT SERVICE\SabhaMujic`; data-root takeover; ACLs; firewall; Event Log source; uninstall keeps data; reinstall; upgrade; downgrade refused. Docs: SETUP, ADMIN_GUIDE, TROUBLESHOOTING, BUILDING. Dialogs, SmartScreen and icons: manual checklist. |

## Known limitations
- Playwright WebKit isn't available in the cloud environment. Remote e2e tests use Chromium with iPhone emulation, so real Safari needs a manual check. Run with `cd src/SabhaMujic.Remote.Web && npm install && npm test`.
- No audio device in the cloud environment or on CI. Audible quality needs the manual Windows checklist.
