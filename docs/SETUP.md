# Setting up SabhaMujic

This guide is for the person who installs SabhaMujic on the sabha PC. Day-to-day administration (music, loop points, playlists, backups) is in [ADMIN_GUIDE.md](ADMIN_GUIDE.md). If something goes wrong, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

> **Verification status.** CI builds the installer and runs a silent install, uninstall and upgrade test on a Windows Server image. That test does not cover audible playback from the service, the setup dialogs, the first-run flow on a real PC or pairing a real iPhone. Until those are checked on real hardware (see [TESTING.md](TESTING.md) and [WINDOWS_TEST_CHECKLIST.md](WINDOWS_TEST_CHECKLIST.md)), treat the steps below as the intended behaviour.

## What you need

- A 64-bit (x64) PC with Windows 10 22H2 or Windows 11, and an administrator account on it for installing.
- About 350 MB of disk space for the program, plus space for the music (each imported file is stored once, up to 1 GB per file).
- Speakers or a sound system on the PC's default playback device.
- For the iPhone remote: an iPhone with Safari on the **same Wi-Fi network** as the PC.

Nothing else needs to be installed. The .NET runtime is included in the installer.

## Install

1. Copy `SabhaMujic-<version>-x64.msi` to the PC and double-click it.
2. The installer isn't code-signed yet, so Windows SmartScreen may show "Windows protected your PC". Click **More info**, check that the file name is the SabhaMujic installer you expect, then click **Run anyway**.
3. Click **Next** on the welcome page.
4. The next page, **About this software**, explains what is installed and lists the third-party components. Tick the box to accept it and click **Next**.
5. On **Custom Setup**, keep or remove **Desktop shortcut**. The SabhaMujic entry itself can't be left out. Click **Next**, then **Install**, and confirm the Windows administrator prompt.
6. On the last page, leave **Launch SabhaMujic** ticked and click **Finish**.

The installer adds:

| What | Where |
|---|---|
| Playback service "SabhaMujic Playback Service" (service name `SabhaMujic`, account `NT SERVICE\SabhaMujic`) | `C:\Program Files\SabhaMujic\Service` |
| Desktop app | `C:\Program Files\SabhaMujic\App`, a Start menu shortcut "SabhaMujic" and, if chosen, a desktop shortcut |
| Firewall rules "SabhaMujic Remote (HTTPS)" (TCP 8443) and "SabhaMujic Remote setup (HTTP)" (TCP 8080), inbound, **Private networks only**, limited to the service program | Windows Defender Firewall |
| Event Log source `SabhaMujic.Service` | Windows Logs → Application |

The service starts straight away and is set to **Automatic (Delayed Start)**. After a reboot, Windows starts it a minute or two after the other automatic services. If it fails, Windows restarts it after 5 seconds.

Before starting the service, the installer prepares `C:\ProgramData\SabhaMujic` and its folders `data`, `library`, `uploads`, `certs` and `logs`. Windows administrators own them, even if someone created the folder earlier. Only the service, SYSTEM and Windows administrators can open `data`, `library`, `uploads` and `certs`, and other users can only read `logs`. Volunteers using a standard Windows account can't browse or copy the music files. The service checks all this again every time it starts. If setup stops with an error at this step, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md#setup-stops-or-the-service-wont-start-because-of-the-data-folder).

## First run: create the administrator

There are no default accounts or passwords. The first SabhaMujic administrator is created on this PC by a Windows administrator.

1. Open SabhaMujic (the installer can start it for you). It shows **Welcome to SabhaMujic**. If it says "Waiting for the SabhaMujic service…", wait a moment.
2. Enter an **Administrator name** and a **PIN or password** (at least 4 characters), twice, and click **Create administrator**.
3. Windows only allows this step from the app running **as administrator**. The installer starts the app normally, so the first attempt usually answers "Creating the first administrator needs Windows administrator rights" and shows a **Restart as administrator** button. Click it and confirm the Windows prompt. SabhaMujic reopens with administrator rights; fill in the form again and click **Create administrator**.
   - If you cancel the Windows prompt, the app stays open and says so; click **Restart as administrator** again.
   - Fallback: close the app, then in the Start menu right-click **SabhaMujic** → **More** → **Run as administrator**.
4. You're signed in as the administrator. Close the app and open it normally from now on. Running it as administrator is only needed for this one step.

It's still a good idea to create a **second administrator account** (next section). If an administrator PIN is forgotten, a Windows administrator can reset it on this PC; see [TROUBLESHOOTING.md](TROUBLESHOOTING.md#forgotten-administrator-pin).

## Create volunteer accounts

SabhaMujic accounts are separate from Windows accounts. Volunteers can share one Windows login and each sign in to SabhaMujic with their own name and PIN.

1. Sign in as an administrator and open **Admin → Users**.
2. Under **New user**, enter a **User name**, choose the **Role** and enter a **PIN or password** (at least 4 characters). Click **Create user**.
   - **Volunteer**: browse and play music, control playback, edit the queue and playlists.
   - **Admin**: everything a volunteer can do, plus importing music, categories, tracks and loop points, accounts and iPhone pairing.
3. Give each volunteer their user name and PIN.

**Reset PIN…** changes a user's PIN and signs that user out everywhere. If you change your own PIN, only the session you are using stays signed in. You can't delete your own account or the last administrator.

## Pair an iPhone

Each iPhone is set up once: it installs the PC's own certificate, then pairs with a one-time code. A paired iPhone stays signed in until an administrator removes it, and it has the role chosen when pairing.

**On the PC** (signed in as an administrator):

1. Make sure the PC is on the sabha Wi-Fi or LAN and that the network is set to **Private** (see [Network and firewall](#network-and-firewall)).
2. Open **Admin → Remote devices**. Under **Pair a new iPhone**, choose the **Role for the new device**. Choose **Admin** only for a phone an administrator keeps, because it gets admin rights without a PIN. Then click **Pair new device**.
3. The app shows a QR code, a 6-digit code and a countdown from 5:00. The code works once and expires after 5 minutes; if it runs out, click **Pair new device** again. Below the code it shows the PC's certificate fingerprint in four lines, under "Check this matches the fingerprint on the phone's setup page".

**On the iPhone:**

1. Connect to the same Wi-Fi as the PC.
2. Open the Camera and point it at the QR code, then tap the link. Safari opens the **Set up the SabhaMujic remote** page on `http://<PC address>:8080/`, showing your pairing code.
3. **Install the certificate profile.** Tap **Download profile**, then **Allow** and **Close**. Open **Settings → General → VPN & Device Management**, tap **SabhaMujic Remote**, then **Install**, enter the iPhone passcode, tap **Install** again and **Done**.
4. **Check the fingerprint.** The setup page shows `SHA-256 …` at the bottom. Compare it with the fingerprint on the PC's pairing panel (step 3 on the PC), a line at a time. The two must be identical. If they differ, don't continue: another device on the network may be pretending to be the PC.
5. **Turn on full trust.** Open **Settings → General → About → Certificate Trust Settings**. Under "Enable full trust for root certificates", switch on **SabhaMujic Local CA (<PC name>)** and tap **Continue**.
6. Go back to Safari and tap **Continue**. It opens `https://<PC address>:8443/pair` with the code filled in, and there must be no certificate warning. Enter a device name (for example "Kirtan iPhone") and pair.
7. The remote opens on the **Now** tab and shows **Connected**. Tap Share → **Add to Home Screen** for quick access. If the home-screen app asks to pair again (iOS can keep its storage separate from Safari), pair it with a new code.

To pair another iPhone, repeat these steps; the profile and trust steps are needed once per iPhone. To remove an iPhone, use **Revoke…** in **Admin → Remote devices**. **Sign out** on the iPhone unpairs it too: it disappears from the PC's list and needs a new pairing code to be used again.

## Network and firewall

- The remote only works on the local network. The service refuses connections from public internet addresses. Phones on mobile data or on a guest Wi-Fi that isolates devices from each other can't reach it.
- The firewall rules only apply on networks Windows treats as **Private**. In **Settings → Network & internet → Wi-Fi** (or **Ethernet**) → your network, set **Network profile type** to **Private network**. On a **Public** network the iPhone can't connect.
- Ports: TCP **8080** serves only the setup page and the certificate (plain HTTP, nothing secret). TCP **8443** serves the remote itself over HTTPS.
- To turn the remote off completely, see [Turning the iPhone remote off](ADMIN_GUIDE.md#turning-the-iphone-remote-off).
- Give the PC a fixed address if you can. On the Wi-Fi router, add a DHCP reservation for the PC. The PC's certificate follows address changes by itself, but the iPhone was set up for the old address and may need to be paired again (see [TROUBLESHOOTING.md](TROUBLESHOOTING.md#the-pcs-ip-address-changed)).

## Update

1. Run the newer `SabhaMujic-<version>-x64.msi` the same way as the first install.
2. The installer stops and removes the old version, then installs the new one and starts the service. Playback stops for a moment during the update.
3. Accounts, music, playlists, loop points and paired iPhones are kept. They live in `C:\ProgramData\SabhaMujic`, which the installer doesn't touch.

An older version can't be installed over a newer one; the installer says so and stops. To go back to an older version, uninstall SabhaMujic first (your data is kept), then install the older version.

## Uninstall and remove all data

**Uninstall**: **Settings → Apps → Installed apps → SabhaMujic → Uninstall** (or Control Panel → Programs and Features). This removes the service, the app, the shortcuts, the firewall rules and the Event Log source. It **keeps** `C:\ProgramData\SabhaMujic`, so the music library and accounts are still there if you install again.

**Remove everything** (this deletes the music library, accounts, playlists, settings and logs, and can't be undone; make a backup first if in doubt, see [ADMIN_GUIDE.md](ADMIN_GUIDE.md#backups)):

1. Uninstall SabhaMujic as above.
2. Open **PowerShell as administrator** and run:

   ```powershell
   Remove-Item -LiteralPath "$env:ProgramData\SabhaMujic" -Recurse -Force
   ```

3. If `C:\Program Files\SabhaMujic` is still there, it holds a settings file you added yourself (`Service\appsettings.json`, see [ADMIN_GUIDE.md](ADMIN_GUIDE.md#service-settings)); delete the folder.
4. Optionally, for each Windows user who used the app, delete `%LOCALAPPDATA%\SabhaMujic`. It only holds window position and size.
5. On each paired iPhone, remove the certificate: **Settings → General → VPN & Device Management → SabhaMujic Remote → Remove Profile**. Also delete the home-screen icon.

## Unattended install (IT)

The MSI supports the standard Windows Installer options, run from an administrator prompt:

```powershell
msiexec /i SabhaMujic-<version>-x64.msi /qn /l*v install.log              # silent install, all features
msiexec /i SabhaMujic-<version>-x64.msi /qn ADDLOCAL=Main                 # without the desktop shortcut
msiexec /x SabhaMujic-<version>-x64.msi /qn                               # silent uninstall (keeps the data)
```

A silent install doesn't start the app. The first administrator must still be created interactively, as described in [First run](#first-run-create-the-administrator).
