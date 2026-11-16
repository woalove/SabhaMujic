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
| 2026-11-16 | Linux cloud env (after M6a merge) | .NET 794 passed (Core 78, Audio 52, Desktop 162, Data 116, Server 386). Remote web: typecheck clean, e2e 8/8 |
| 2026-11-15 | GitHub Actions run 11 (M3b + seek fix), **Windows** | Windows.Tests 8/8 passed: exact store DACL; **a temporary standard user is denied the protected folders**, can connect to the pipe and can't create a pipe instance (anti-squatting); elevation detection; console-hosted service completes first-run setup over the ACL'd pipe. Audio (net10.0-windows) 53 passed / 1 skipped (sox): MediaFoundation MP3/AAC/WMA/FLAC/WAV decoding, exact seeks, engine crossfade. Server 229 passed over real named pipes. One failure: the wall-clock analysis perf test (5.006 s vs a 5 s budget, under parallel load), now run in its own CI step |
| 2026-11-15 | GitHub Actions run 3 (Linux + Windows) | Build, tests (incl. Windows smoke test), self-contained publish: success |

## Verification status of completion criteria
| Criterion | Status |
|---|---|
| Windows desktop app runs | **Unverified.** Builds and publishes on Windows CI; not launched yet (manual checklist). |
| Volunteers browse/play categorized tracks | Full stack tested on Linux (real engine, null output, pipe clients). Desktop UI **unverified** on Windows |
| Smart Loop + crossfade with real audio | Engine + DSP tested on synthetic WAV signals (Linux). **Unverified** with real recordings and real audio output |
| Playlists and queues | Unit, server and integration tests (Linux) |
| Volunteers cannot access protected files | **Verified on Windows CI**: a standard user is denied listing and reading the protected folders (real `LogonUser` impersonation), and the exact DACL is checked. App level: exhaustive authorization tests over the pipe and the remote. Still to check manually: the installed service under `NT SERVICE\SabhaMujic` |
| Admin-only uploads | Implemented end to end over the pipe (fake audio probe); volunteer uploads denied with no side effects |
| iPhone remote controls playback | Automated: Chromium with iPhone emulation, against a scripted backend. Real iPhone Safari: **unverified** (manual checklist) |
| Playback continues when remote disconnects | Tested: pipe clients against the real engine (integration test), and remote hub connections + `RemoteHost` stop against a fake backend. Windows: pending CI |
| Normal resizable window | Implemented (min 760×480, mini-player); **unverified** on Windows |
| Performance/stability tested | Not started |
| Installer + setup docs | Not started |

## Known limitations
- Playwright WebKit isn't available in the cloud environment. Remote e2e tests use Chromium with iPhone emulation, so real Safari needs a manual check. Run with `cd src/SabhaMujic.Remote.Web && npm install && npm test`.
- No audio device in the cloud environment or on CI. Audible quality needs the manual Windows checklist.
