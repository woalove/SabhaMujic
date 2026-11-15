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
| 2026-11-15 | GitHub Actions run 3 (Linux + Windows) | Build, tests (incl. Windows smoke test), self-contained publish: success |

## Verification status of completion criteria
| Criterion | Status |
|---|---|
| Windows desktop app runs | **Unverified.** Builds and publishes on Windows CI; not launched yet (manual checklist). |
| Volunteers browse/play categorized tracks | Not started |
| Smart Loop + crossfade with real audio | Not started |
| Playlists and queues | Queue logic unit-tested; integration not started |
| Volunteers cannot access protected files | Authorization matrix unit-tested; ACL not started |
| Admin-only uploads | Matrix unit-tested; upload not started |
| iPhone remote controls playback | Not started |
| Playback continues when remote disconnects | Not started |
| Normal resizable window | Implemented (min 760×480, mini-player); **unverified** on Windows |
| Performance/stability tested | Not started |
| Installer + setup docs | Not started |

## Known limitations
- Playwright WebKit isn't available in the cloud environment. Remote e2e tests use Chromium with iPhone emulation, so real Safari needs a manual check.
- No audio device in the cloud environment or on CI. Audible quality needs the manual Windows checklist.
