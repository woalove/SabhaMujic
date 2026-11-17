# Building SabhaMujic

For developers. The architecture is in [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) and [ARCHITECTURE.md](ARCHITECTURE.md); what is and isn't verified is in [TESTING.md](TESTING.md).

## Prerequisites

| Tool | Needed for |
|---|---|
| .NET SDK 10.0.100 or later in the 10.0 band (`global.json`, `rollForward: latestFeature`) | everything |
| Windows 10 22H2+ / Windows 11, x64 | running the desktop app, the Windows service, the Windows-only tests, and building the MSI |
| PowerShell 7 (`pwsh`) | `installer/build.ps1`, `installer/smoke-test.ps1` |
| Node.js 20+ and npm | the iPhone web remote's type check and browser tests |

WiX needs no separate install. `WixToolset.Sdk` 5.0.2 and its extensions come from NuGet when the installer project is built.

## Build and test

```bash
dotnet build SabhaMujic.slnx
dotnet test SabhaMujic.slnx --filter "Category!=Performance"
dotnet test tests/SabhaMujic.Audio.Tests --filter "Category=Performance"   # wall-clock budgets: run alone, Release recommended
```

- **Linux** builds the whole solution, including the WPF app and the service (compile only: `Directory.Build.props` sets `EnableWindowsTargeting`). The cross-platform tests run there. `tests/SabhaMujic.Windows.Tests` is not a test project on Linux, and other Windows-only tests skip themselves.
- **Windows** also runs `SabhaMujic.Windows.Tests` and the MediaFoundation audio tests. Some Windows tests create a temporary local user to prove that a standard user is denied the protected folders, and they need an elevated prompt.
- Warnings are errors (`TreatWarningsAsErrors`), package versions are central (`Directory.Packages.props`).

## Run it locally

- **Desktop app with fake data** (Windows): `dotnet run --project src/SabhaMujic.Desktop -- --demo`. Uses an in-memory client with accounts admin/1234 and sevak/1111, no audio and no service.
- **Development host** (Linux or Windows): the real service (database, engine, pipe) with silent audio output:

  ```bash
  dotnet run -f net10.0 --project tools/SabhaMujic.DevHost -- --data ./devdata --seed-admin admin:1234
  ```

  Options: `--pipe <name>`, `--speed <factor>`, `--verbose`. `--seed-admin` is for development only; real installs create the first administrator from the desktop app. On Windows, stop the installed service first, or use another `--pipe` name; the desktop app always connects to the pipe `SabhaMujic.v1`.
- **The Windows service as a console app** (diagnostics): `SabhaMujic.Service.exe [--SabhaMujic:DataRoot=<dir>] [--SabhaMujic:PipeName=<name>] [--SabhaMujic:Remote:Enabled=false]`. All settings are in `src/SabhaMujic.Service/ServiceSettings.cs` and [ADMIN_GUIDE.md](ADMIN_GUIDE.md#service-settings). Stop the installed service first if you keep the default pipe name or ports. The default data root is `%ProgramData%\SabhaMujic`; use your own `DataRoot` to avoid touching real data.
- **Audio spike** (Windows, audible checks): `dotnet run --project tools/SabhaMujic.AudioSpike -c Release -- <file1> <file2> [--crossfade 6] [--smartloop] [--repeat]`.

## iPhone web remote

The remote is plain ES modules in `src/SabhaMujic.Remote.Web/wwwroot`. They are embedded into `SabhaMujic.Server` at build time, so there is no bundler and no Node at install time.

```bash
cd src/SabhaMujic.Remote.Web
npm install
npm run typecheck   # tsc --checkJs over the JSDoc types
npm test            # builds tests/SabhaMujic.RemoteE2EHost, then Playwright (Chromium, iPhone 13 emulation)
```

Playwright needs a Chromium build. If none is installed (or `PLAYWRIGHT_BROWSERS_PATH` doesn't point at one), run `npx playwright install chromium` first. These tests don't replace a real iPhone: Safari, profile installation and certificate trust are manual checks in [WINDOWS_TEST_CHECKLIST.md](WINDOWS_TEST_CHECKLIST.md). The vendored SignalR client is described in `src/SabhaMujic.Remote.Web/VENDORED.md`.

## Build the MSI (Windows)

```powershell
pwsh installer/build.ps1                    # version 0.1.0
pwsh installer/build.ps1 -Version 0.2.0
pwsh installer/build.ps1 -Version 0.2.0 -SkipPublish   # reuse artifacts/service and artifacts/desktop
```

The script:

1. Publishes `src/SabhaMujic.Service` and `src/SabhaMujic.Desktop` (self-contained win-x64) into `artifacts/service` and `artifacts/desktop`, emptying those folders first, because everything in them goes into the MSI.
2. Builds `installer/SabhaMujic.Installer/SabhaMujic.Installer.wixproj` with `-p:SabhaMujicVersion=<Version>`, `-p:ServicePublishDir=…` and `-p:DesktopPublishDir=…`.
3. Copies the result to `artifacts/SabhaMujic-<Version>-x64.msi`.

The installer project:

- `Package.wxs` is the whole package: per-machine x64, a stable `UpgradeCode` (**never change it**), and a major upgrade that refuses downgrades. It installs the service (`NT SERVICE\SabhaMujic`, delayed auto-start, restart on failure), the firewall rules (TCP 8443/8080, Private profile, service exe only), the Event Log source `SabhaMujic.Service`, the shortcuts and the `WixUI_FeatureTree` dialogs. The two exes are authored explicitly; everything else in the publish folders is harvested with `<Files>`.
- `AboutThisSoftware.rtf` is the notice page in the setup wizard (what is installed, third-party components). Update it when a shipped dependency or its license changes.
- The installer doesn't author data folders or permissions. Its `PrepareDataRoot` custom action (deferred, as SYSTEM, before `StartServices`, on install, repair and upgrade but not uninstall) runs `SabhaMujic.Service.exe --prepare-data-root` through WiX's `WixQuietExec64`. That takes ownership of `%ProgramData%\SabhaMujic` and its folders for Administrators and applies the exact permissions (ARCHITECTURE.md #34). A non-zero exit code (2 = a junction, link or file in the way, or a folder that can't be taken over) fails and rolls back the installation; the tool's output is in the verbose MSI log. WixQuietExec stops waiting after 60 seconds. Uninstall keeps `%ProgramData%\SabhaMujic`.
- The shortcuts and Programs and Features use `src/SabhaMujic.Desktop/Assets/SabhaMujic.ico`.
- The project isn't in `SabhaMujic.slnx`, so Linux builds never load it. WiX can't build MSIs on Linux; CI builds it on `windows-latest`.
- **WiX 5.0.2 only.** WiX 6 and later ship under the Open Source Maintenance Fee EULA ([ARCHITECTURE.md](ARCHITECTURE.md) #29). The SDK version is in the project's `Sdk` attribute, and the extension versions are in `Directory.Packages.props`.

**Versions**: `major.minor.build` (major and minor at most 255, build at most 65535). Every MSI given to users must have a higher version than the last one. MSI treats two different builds with the same version as different products, and both would end up installed. CI builds `0.1.<run number>`.

**Signing**: the MSI isn't signed yet, so SmartScreen warns (see [SETUP.md](SETUP.md#install)). Signing needs a code-signing certificate stored as a CI secret.

### Installer smoke test

`installer/smoke-test.ps1` checks a pair of MSIs on a **disposable** machine. It needs an elevated prompt, installs and uninstalls SabhaMujic, and leaves `%ProgramData%\SabhaMujic` behind:

```powershell
pwsh installer/smoke-test.ps1 -Msi artifacts/SabhaMujic-0.1.0-x64.msi -Version 0.1.0 `
    -UpgradeMsi artifacts/SabhaMujic-0.1.1-x64.msi -UpgradeVersion 0.1.1
```

The test runs these steps in order:

0. A temporary standard user "pre-creates" `%ProgramData%\SabhaMujic` and `library`, owns them and gives itself full control (the #34 attack). The machine must not have that folder yet.
1. Silent install. The test checks the service account, start mode and failure actions, that the service reaches Running and stays running, and that the pipe `\\.\pipe\SabhaMujic.v1` exists. It checks that the remote setup site answers `/health`. It checks that the root and every folder are owned by Administrators, with the exact #34 permissions: SYSTEM, Administrators and the service everywhere, plus Users read & execute on the root and read-only on `logs`, and nothing else. It also checks the Event Log source, the firewall rules, the shortcuts and the Programs and Features entry. It then prints the service log.
1b. With the service stopped, `library` is replaced by a junction: the service must refuse to start and log a critical entry from `SabhaMujic.Service`. The test then restores the folder and the service.
2. Uninstall. Everything is removed except `%ProgramData%\SabhaMujic`.
3. Reinstall over the kept data.
4. Upgrade to the higher version.
5. A downgrade attempt, which must be refused.
6. Final uninstall.

Verbose `msiexec` logs go to `artifacts/install-logs`. The script prints the failing part of a log when `msiexec` fails.

## CI

`.github/workflows/ci.yml` has two jobs:

- **Linux**: restore, build (Release), tests without the performance category, then the performance tests alone.
- **Windows**: the same, plus:
  - a self-contained publish of the desktop app and the service, uploaded as `windows-binaries`;
  - two MSIs from that publish output: `0.1.0`, used only as the "old version", and `0.1.<run number>`;
  - the installer smoke test;
  - the `0.1.<run number>` MSI and the install logs, uploaded as `windows-msi`.

Artifact uploads are best-effort (`continue-on-error`), because the account's artifact storage quota is sometimes full. Windows CI is the only automated Windows verification. Record its results in [TESTING.md](TESTING.md).

## CI artifacts and installer downloads
GitHub Actions artifact storage is a shared account quota. CI uploads test results and install logs only when a job fails (kept 3 days).

To get an installer: start a manual run on the branch (**Actions → CI → Run workflow**). After the install smoke test passes, CI publishes that exact MSI as a GitHub **pre-release** named `installer-0.1.<run number>` (Releases page). Release files don't use the Actions storage quota. Only the newest test build is kept; older `installer-*` pre-releases and their tags are deleted automatically.
