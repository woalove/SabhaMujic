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
| 2026-11-15 | GitHub Actions run 3 (Linux + Windows) | Build, tests (incl. Windows smoke test), self-contained publish: success |

## Verification status of completion criteria
| Criterion | Status |
|---|---|
| Windows desktop app runs | **Unverified.** Builds and publishes on Windows CI; not launched yet (manual checklist). |
| Volunteers browse/play categorized tracks | Full stack tested on Linux (real engine, null output, pipe clients). Desktop UI **unverified** on Windows |
| Smart Loop + crossfade with real audio | Engine + DSP tested on synthetic WAV signals (Linux). **Unverified** with real recordings and real audio output |
| Playlists and queues | Unit, server and integration tests (Linux) |
| Volunteers cannot access protected files | App-level: exhaustive authorization tests over the real pipe; clients never receive paths or bytes. Windows ACL: not started (M3b) |
| Admin-only uploads | Implemented end to end over the pipe (fake audio probe); volunteer uploads denied with no side effects |
| iPhone remote controls playback | Automated: Chromium with iPhone emulation, against a scripted backend. Real iPhone Safari: **unverified** (manual checklist) |
| Playback continues when remote disconnects | Tested: pipe clients against the real engine (integration test), and remote hub connections + `RemoteHost` stop against a fake backend. Windows: pending CI |
| Normal resizable window | Implemented (min 760×480, mini-player); **unverified** on Windows |
| Performance/stability tested | Not started |
| Installer + setup docs | Not started |

## Known limitations
- Playwright WebKit isn't available in the cloud environment. Remote e2e tests use Chromium with iPhone emulation, so real Safari needs a manual check. Run with `cd src/SabhaMujic.Remote.Web && npm install && npm test`.
- No audio device in the cloud environment or on CI. Audible quality needs the manual Windows checklist.
