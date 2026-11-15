# Testing

## How to run
- All platforms: `dotnet test SabhaMujic.slnx`
- Windows-only tests (`tests/SabhaMujic.Windows.Tests`) run only on Windows. On Linux they are excluded via `IsTestProject`.

## Latest results
| Date | Where | Result |
|---|---|---|
| 2026-11-15 | Linux cloud env | Core.Tests: 74 passed, 0 failed |
| 2026-11-15 | Linux cloud env (after M2 merge) | Core 74 + Data 111 passed, 0 failed |

## Verification status of completion criteria
| Criterion | Status |
|---|---|
| Windows desktop app runs | **Unverified.** Compiles on Linux only. |
| Volunteers browse/play categorized tracks | Not started |
| Smart Loop + crossfade with real audio | Not started |
| Playlists and queues | Queue logic unit-tested; integration not started |
| Volunteers cannot access protected files | Authorization matrix unit-tested; ACL not started |
| Admin-only uploads | Matrix unit-tested; upload not started |
| iPhone remote controls playback | Not started |
| Playback continues when remote disconnects | Not started |
| Normal resizable window | Skeleton window is resizable; **unverified** on Windows |
| Performance/stability tested | Not started |
| Installer + setup docs | Not started |

## Known limitations
- Playwright WebKit isn't available in the cloud environment. Remote e2e tests use Chromium with iPhone emulation, so real Safari needs a manual check.
- No audio device in the cloud environment or on CI. Audible quality needs the manual Windows checklist.
