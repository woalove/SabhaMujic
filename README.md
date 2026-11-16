# SabhaMujic

A Windows music player for sabha (devotional assembly) volunteers.

- **Playback service.** A Windows service plays the music, so playback continues when the app is closed or an iPhone disconnects. It does seamless **Smart Loop** repeats and equal-power **crossfades** between tracks.
- **Protected library.** Imported music is stored in a folder that only the service and Windows administrators can open, so volunteers can play it but can't copy the files.
- **Desktop app** (WPF). Volunteers browse categories, search, play, and manage the queue and playlists. Administrators import music, adjust loop points, and manage accounts and iPhones.
- **iPhone remote.** Safari on the same Wi-Fi, over HTTPS with a certificate the PC creates itself. iPhones pair with a one-time QR code.
- **Roles.** SabhaMujic accounts are either *Volunteer* or *Admin*, independent of Windows accounts.

## Documentation

| For | Read |
|---|---|
| Installing at the sabha, first-run setup, pairing iPhones, updating, uninstalling | [docs/SETUP.md](docs/SETUP.md) |
| Running it: importing music, categories, Smart Loop, users, devices, backups | [docs/ADMIN_GUIDE.md](docs/ADMIN_GUIDE.md) |
| When something doesn't work | [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) |
| Building, testing, the MSI and CI | [docs/BUILDING.md](docs/BUILDING.md) |
| Design, decisions, verification status | [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md), [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/TESTING.md](docs/TESTING.md), [docs/WINDOWS_TEST_CHECKLIST.md](docs/WINDOWS_TEST_CHECKLIST.md) |

## Status

Under development. The cross-platform parts are covered by automated tests on Linux and Windows CI. CI also builds the MSI and runs a silent install, upgrade and uninstall test. Audible playback from the service, the desktop app on a real PC and the remote on a real iPhone still need the manual checklist; see [docs/TESTING.md](docs/TESTING.md).

## Quick start for developers

```bash
dotnet build SabhaMujic.slnx
dotnet test SabhaMujic.slnx --filter "Category!=Performance"
```

Building the installer (Windows): `pwsh installer/build.ps1`. Details are in [docs/BUILDING.md](docs/BUILDING.md).
