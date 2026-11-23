# SabhaMujic

A Windows music player for sabha (devotional assembly) volunteers.

- **Playback service.** A Windows service plays the music, so playback continues when the app is closed or an iPhone disconnects. It does seamless **Smart Loop** repeats and equal-power **crossfades** between tracks.
- **Protected library.** Imported music is stored in a folder that only the service and Windows administrators can open, so volunteers can play it but can't copy the files.
- **Desktop app** (WPF). Volunteers use it with no sign-in: browse categories, search, play, and manage the queue and playlists. Administrators click **Admin sign-in** to import music, organise categories, adjust loop points, and manage accounts and phones. They are signed out after about 15 minutes without activity.
- **Categories.** A track can be in several categories, assigned from the track or from the category.
- **iPhone remote, for administrators.** Safari on the same Wi-Fi, over HTTPS with a certificate the PC creates itself. After a one-time certificate setup, an administrator signs in with their user name and password; "Remember this phone" keeps it signed in for about 180 days.
- **Accounts.** Only administrators have SabhaMujic accounts (passwords of at least 8 characters), independent of Windows accounts.
- **Look.** Dark by default with a light theme, the bundled Inter font, and the same screens on the PC and the phone.

## Documentation

| For | Read |
|---|---|
| Installing at the sabha, first-run setup, setting up iPhones, updating (including from v1), uninstalling | [docs/SETUP.md](docs/SETUP.md) |
| Running it: signing in, importing music, categories, Smart Loop, administrator accounts, phones, backups | [docs/ADMIN_GUIDE.md](docs/ADMIN_GUIDE.md) |
| When something doesn't work | [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) |
| Building, testing, the MSI and CI | [docs/BUILDING.md](docs/BUILDING.md) |
| Design, decisions, verification status | [docs/DESIGN.md](docs/DESIGN.md), [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md), [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/TESTING.md](docs/TESTING.md), [docs/WINDOWS_TEST_CHECKLIST.md](docs/WINDOWS_TEST_CHECKLIST.md) |

## Status

Under development. The cross-platform parts are covered by automated tests on Linux and Windows CI. CI also builds the MSI and runs a silent install, upgrade and uninstall test. Audible playback from the service, the desktop app on a real PC and the remote on a real iPhone still need the manual checklist; see [docs/TESTING.md](docs/TESTING.md). The v2 changes (open use, password sign-in on the phone, many-to-many categories, the new look) are tested on Linux and in Chromium iPhone emulation only; they haven't been checked on Windows or a real iPhone yet.

## Quick start for developers

```bash
dotnet build SabhaMujic.slnx
dotnet test SabhaMujic.slnx --filter "Category!=Performance"
```

Building the installer (Windows): `pwsh installer/build.ps1`. Details are in [docs/BUILDING.md](docs/BUILDING.md).
