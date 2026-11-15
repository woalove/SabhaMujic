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
| 2026-11-15 | GitHub Actions run 3 (Linux + Windows) | Build, tests (incl. Windows smoke test), self-contained publish: success |

## Verification status of completion criteria
| Criterion | Status |
|---|---|
| Windows desktop app runs | **Unverified.** Builds and publishes on Windows CI; not launched yet (manual checklist). |
| Volunteers browse/play categorized tracks | Not started |
| Smart Loop + crossfade with real audio | Engine + DSP tested on synthetic WAV signals (Linux). **Unverified** with real recordings and real audio output |
| Playlists and queues | Queue logic unit-tested; integration not started |
| Volunteers cannot access protected files | App-level: exhaustive authorization tests over the real pipe; clients never receive paths or bytes. Windows ACL: not started (M3b) |
| Admin-only uploads | Implemented end to end over the pipe (fake audio probe); volunteer uploads denied with no side effects |
| iPhone remote controls playback | Not started |
| Playback continues when remote disconnects | Pipe clients: tested with a fake backend (no stop/pause on disconnect). Real engine and remote: pending |
| Normal resizable window | Implemented (min 760×480, mini-player); **unverified** on Windows |
| Performance/stability tested | Not started |
| Installer + setup docs | Not started |

## Known limitations
- Playwright WebKit isn't available in the cloud environment. Remote e2e tests use Chromium with iPhone emulation, so real Safari needs a manual check.
- No audio device in the cloud environment or on CI. Audible quality needs the manual Windows checklist.
