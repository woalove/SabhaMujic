# SabhaMujic — project instructions

Windows music player for sabha volunteers. A Windows service owns playback, protected files and authorization; a WPF desktop app and an iPhone web remote are thin clients.

## Read first when resuming
1. `docs/PROGRESS.md` (current milestone, active tasks, blockers)
2. `docs/IMPLEMENTATION_PLAN.md` (approved plan; do not redesign approved components without recording a reason in `docs/ARCHITECTURE.md`)
3. `docs/ARCHITECTURE.md` (decisions), `docs/TESTING.md` (what is and isn't verified)

## Build & test (Linux cloud env or Windows)
- `dotnet build SabhaMujic.slnx` — WPF/Windows projects compile on Linux because `Directory.Build.props` sets `EnableWindowsTargeting`.
- `dotnet test SabhaMujic.slnx` — Windows-only tests skip themselves off Windows.
- Windows CI: `.github/workflows/ci.yml` (`windows-latest`) is the only automated Windows verification.

## Conventions
- Target `net10.0` for everything that can run cross-platform. Only `Audio.Windows`, `Service`, `Desktop`, `Windows.Tests` target `net10.0-windows`.
- Windows-specific behaviour goes behind an interface in a cross-platform project (`IAudioOutput`, `IAudioDecoderFactory`, `IStoreAcl`, `ICallerIdentity`).
- `SabhaMujic.Contracts` is the frozen boundary between service and clients. Change it only deliberately: bump `ContractVersion.Current` and note it in `docs/ARCHITECTURE.md`.
- Every client-facing operation is authorized through `SabhaMujic.Core.Security.Authorizer` (single role matrix). Never add an endpoint/RPC method that bypasses it.
- Clients never receive file paths or raw audio bytes for stored tracks.
- Nullable enabled, warnings are errors. Plain xUnit `Assert`, no assertion libraries.
- Audio DSP: 32-bit float, 48 kHz stereo internal mix; no allocations in the steady-state render path.

## Honesty rules
- Never claim Windows-specific behaviour (WPF runtime, WASAPI, ACLs, service, installer, iPhone Safari) is verified unless Windows CI or a completed manual checklist entry proves it. Mark it **unverified** in `docs/TESTING.md` otherwise.
- No placeholders/simulated behaviour for core features once their milestone has started.

## Subagent work
- One owner per project directory at a time (see plan §7); parallel agents use separate git worktrees.
- Briefs state deliverables, acceptance tests and off-limits files; reports list changed files, test results, assumptions, blockers.
- The lead reviews diffs and reruns tests before merging.

## Git
Develop on `claude/awesome-cerf-aa8wbt`. Commit small, tested increments.
