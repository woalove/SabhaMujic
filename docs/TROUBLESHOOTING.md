# SabhaMujic troubleshooting

Most problems come down to one question: is the **SabhaMujic Playback Service** running? The service plays the music; the desktop app and the iPhones only control it. Start with [Is the service running?](#is-the-service-running) and [Reading logs](#reading-logs-and-event-viewer).

Commands below that say **PowerShell (admin)** need *Start → type PowerShell → right-click → Run as administrator*.

## Is the service running?

- Open **Services** (`services.msc`) and find **SabhaMujic Playback Service**. Its status should be **Running** and its startup type **Automatic (Delayed Start)**.
- Or, in PowerShell: `Get-Service SabhaMujic`.

To start or restart it: in Services, right-click → **Start** / **Restart**, or in **PowerShell (admin)**: `Restart-Service SabhaMujic`. Restarting stops playback; the queue comes back stopped at the current track, and **Play** continues near where it was.

## The desktop app says "Connecting to SabhaMujic service…"

The app can't reach the service. It keeps retrying by itself, so as soon as the service is available it connects without a restart.

1. **Just after Windows started?** The service uses delayed start, so Windows starts it a minute or two after boot. Wait.
2. **Service stopped?** Check [Is the service running?](#is-the-service-running) and start it.
3. **Service won't start or stops again at once?** See [The service doesn't start or keeps stopping](#the-service-doesnt-start-or-keeps-stopping).
4. **"Connection to the SabhaMujic service was lost. Reconnecting…"** The service was restarted or stopped. Playback, if the service is still running, continues; the banner clears when the connection is back.
5. **"This version of the app does not match the SabhaMujic service."** The app and the service come from different versions. Run the installer of the version you want again; it always installs both.

## Setup stops, or the service won't start, because of the data folder

To keep the music library private, SabhaMujic refuses to use `C:\ProgramData\SabhaMujic` (or its `data`, `library`, `uploads`, `certs` or `logs` folders) if one of them is a **junction or symbolic link**, a **file**, or a folder **owned by an account** other than SYSTEM, Administrators or the service. Such a folder may have been planted by someone before SabhaMujic was installed, to redirect or read the protected files. Symptoms:

- **During installation**: setup ends with an error just before the service starts, and the installation is rolled back. The verbose log (`msiexec /i … /l*v install.log`) shows the `PrepareDataRoot` step and the reason.
- **At service start**: the service doesn't start. **Event Viewer → Windows Logs → Application** has an **Error** entry (logged as critical) from source **SabhaMujic.Service** containing "SabhaMujic cannot use its data folder and will not start:", followed by the folder and the problem.

To fix it, in **PowerShell (admin)**:

1. If the message says **junction or symbolic link**, remove the link only (not the folder it points to), then put the real folder back, for example from a backup:

   ```powershell
   cmd /c rmdir "C:\ProgramData\SabhaMujic\library"    # removes a junction/link itself, never its target's contents
   ```

   Look at where the link pointed: data may have been redirected there. If the message says it is **a file**, move the file away.
2. If the message says the folder is **owned by** another account, let SabhaMujic take it over:

   ```powershell
   & "C:\Program Files\SabhaMujic\Service\SabhaMujic.Service.exe" --prepare-data-root
   ```

   It sets Administrators as owner and resets the permissions, then exits (exit code 0 on success, 2 if it still can't use the folder; the reason is printed). Running **Repair** of SabhaMujic in **Settings → Apps** does the same.
3. Start the service: `Start-Service SabhaMujic`.

## The service doesn't start or keeps stopping

If the service fails, Windows restarts it after 5 seconds. A service that keeps stopping has a problem that a restart doesn't fix:

1. Read the newest log in `C:\ProgramData\SabhaMujic\logs` and the Application log in Event Viewer (see [Reading logs](#reading-logs-and-event-viewer)). The last lines before it stopped usually name the cause.
2. Common causes:
   - **Another program holds the pipe name.** For safety the service refuses to start if something else already created its connection point (`\\.\pipe\SabhaMujic.v1`). Restart the PC; if it happens again, find out which program uses that name.
   - **The data folder is refused** (a junction, link or file, or the wrong owner): see [the section above](#setup-stops-or-the-service-wont-start-because-of-the-data-folder).
   - **The data folder isn't usable**: a full disk, or `C:\ProgramData\SabhaMujic` damaged or restored without its contents. Free some space or restore from a backup ([ADMIN_GUIDE.md](ADMIN_GUIDE.md#backups)).
   - **Invalid settings**: a value in `C:\Program Files\SabhaMujic\Service\appsettings.json` (or a `SabhaMujic__…` environment variable) that the service can't use, such as a port outside 1–65535, the same port for HTTPS and the setup page, or a flag that isn't `true`/`false`. The service stops at startup before its own log starts; Event Viewer's Application log (source **.NET Runtime**) has the message naming the setting. See [Service settings](ADMIN_GUIDE.md#service-settings).
   - **A port is already in use**: another program listens on TCP 8443 or 8080, and the remote can't start (the service log says so). Stop that program, or move the remote to other ports ([Service settings](ADMIN_GUIDE.md#service-settings)) and allow them in the firewall.
   - **Damaged installation**: in **Settings → Apps → Installed apps → SabhaMujic**, uninstall and install again. Your data is kept.
3. Services shows an error such as "Error 1053" or "The service did not respond": the System log in Event Viewer (source **Service Control Manager**) has the details.

## No sound

Playing audio from a Windows service hasn't been verified on real PCs yet (see [TESTING.md](TESTING.md)). If you hear nothing:

1. Check the obvious first: the app's **mute** button and **volume**; the Windows volume; the speakers or mixer are on and connected.
2. Is anything actually playing? The now-playing bar should show **Playing** and a moving position. If the queue is empty or the status is **Stopped**, start a track.
3. The service plays to the **default playback device**. Open **Settings → System → Sound** and make sure the right output is selected. When the default device changes or a device is unplugged, the service moves to the current default and keeps retrying while there is none.
4. Restart the service (`Restart-Service SabhaMujic` in **PowerShell (admin)**). The queue comes back stopped at the current track; press **Play** to continue near where it was.
5. Look in the service log for lines mentioning the audio device or "Playback problem for track …". A track that can't be read is skipped and logged.

## First-time setup says to "Run as administrator"

Creating the first SabhaMujic administrator needs the app to run with Windows administrator rights. The service checks this for safety. Click **Restart as administrator** on the setup screen and confirm the Windows prompt; the app reopens elevated, and you fill in the form again. Your Windows account must be a local administrator on this PC, or you must enter an administrator's credentials in the prompt. Afterwards, open the app normally. See [SETUP.md](SETUP.md#first-run-create-the-administrator).

- **"The Windows prompt was cancelled…"**: the app is still running without administrator rights. Click **Restart as administrator** again.
- **"SabhaMujic could not restart itself"**, or no button appears: close the app, then in the Start menu right-click **SabhaMujic** → **More** → **Run as administrator**.

If the form says "SabhaMujic is already set up", an administrator already exists: sign in instead.

## Forgotten administrator PIN

- **Another administrator account exists**: sign in with it, open **Admin → Users**, select the account and use **Reset PIN…**.
- **No administrator can sign in**: reset the PIN on the SabhaMujic PC. This needs someone with Windows administrator rights on that PC. It never works from an iPhone.
  1. On the sign-in screen, click **Forgot admin PIN?** The screen **Reset an administrator PIN** opens.
  2. If the app isn't running as administrator, it says "Resetting an administrator PIN needs Windows administrator rights. Choose 'Restart as administrator' and confirm the Windows prompt." Click **Restart as administrator** and confirm. The app reopens on the same screen.
  3. Enter the **Administrator name**, then the **New PIN or password (at least 4 characters)** and **Repeat new PIN or password**, and click **Reset PIN and sign in**. If no account has that name, a new administrator with that name is created instead. A volunteer's name is refused ("That account is not an administrator…").
  4. You're signed in as that administrator. Anywhere else the account was signed in, it is signed out with "This account's PIN was reset on the SabhaMujic PC. Please sign in again."

  **Back to sign in** returns to the sign-in screen. After 5 attempts within 5 minutes from the same Windows account, further attempts are refused for a while (the app says "Too many attempts. Please wait a minute and try again."). If the Windows prompt was cancelled, click **Restart as administrator** again. If the app can't restart itself, close it and start it with right-click **SabhaMujic** → **More** → **Run as administrator**, then click **Forgot admin PIN?** again.

## "The SabhaMujic service could not save data (storage busy or full)"

The full message is "The SabhaMujic service could not save data (storage busy or full). Try again shortly." The service couldn't write to its database: the disk is full, or the database was briefly locked (for example by a backup or antivirus program reading `C:\ProgramData\SabhaMujic\data`). Nothing was changed, and playback carries on. The service also retries saving the playback state and analysis results by itself.

1. Try the action again after a few seconds.
2. If it keeps happening, check free space on drive C: and free some up.
3. Exclude `C:\ProgramData\SabhaMujic\data` from antivirus scanning or file-level backup while the service runs; back it up with the service stopped ([ADMIN_GUIDE.md](ADMIN_GUIDE.md#backups)).
4. The service log names the operation that failed.

## The sign-in screen briefly reconnects

If the desktop app sits on the sign-in (or setup) screen without anyone signing in, the service closes its connection after 60 seconds, to keep connections free for real use. The app reconnects by itself straight away. This isn't an error, and nothing needs to be done.

## "Too many sign-in attempts"

The app says "Too many sign-in attempts. Please wait a minute and try again." After 5 wrong PINs for one user name within 5 minutes, sign-in for that name pauses for a few minutes. Wait and try again, or ask an administrator to reset the PIN.

## The iPhone can't reach the PC

The setup page (`http://<PC address>:8080/`) or the remote (`https://<PC address>:8443/`) doesn't load:

1. **Same network?** The iPhone must be on the same Wi-Fi as the PC, not on mobile data. Guest Wi-Fi networks often block devices from talking to each other, so use the main network. The service only accepts devices on the local network.
2. **Is the PC's network Private?** The firewall rules only apply on **Private** networks. **Settings → Network & internet → Wi-Fi** (or **Ethernet**) → the network → **Network profile type → Private network**.
3. **Are the firewall rules there?** In **PowerShell (admin)**:

   ```powershell
   Get-NetFirewallRule -DisplayName "SabhaMujic Remote*" | Format-Table DisplayName, Enabled, Profile, Action
   ```

   You should see "SabhaMujic Remote (HTTPS)" and "SabhaMujic Remote setup (HTTP)", enabled, profile **Private**, action **Allow**. If they're missing, run the installer again (Repair). Another firewall or security product may need the same ports opened: TCP 8080 and 8443, inbound, for `C:\Program Files\SabhaMujic\Service\SabhaMujic.Service.exe`.
4. **Is the service running, with the remote on?** On the PC, open `http://localhost:8080/health` in a browser; it should say `OK`. If it doesn't load, see [Is the service running?](#is-the-service-running) and the service log, and check that the remote hasn't been turned off in the [service settings](ADMIN_GUIDE.md#service-settings).
5. **Right address?** Find the PC's address with `ipconfig` (the IPv4 address of the Wi-Fi or Ethernet adapter, usually `192.168.x.x` or `10.x.x.x`). On the iPhone, try `http://<that address>:8080/health`.

## Certificate warning on the iPhone

Safari says "This connection is not private", or the remote keeps disconnecting:

1. **Profile installed and trusted?** Check **Settings → General → VPN & Device Management** for **SabhaMujic Remote**. Then check **Settings → General → About → Certificate Trust Settings**: **SabhaMujic Local CA (<PC name>)** must be switched on. Both steps are needed; see [SETUP.md](SETUP.md#pair-an-iphone).
2. **Right PC?** The certificate is specific to one PC. A profile from another PC doesn't help.
3. **PC reinstalled, moved to a new PC or certificates reset?** If the service lost its `certs` folder (or the folder came from another PC or Windows installation, where its keys can't be decrypted), it created a new certificate authority. On the iPhone, remove the old **SabhaMujic Remote** profile, then install the new one from `http://<PC address>:8080/`, trust it and pair again. Restoring `certs` from a backup of this same PC avoids that ([ADMIN_GUIDE.md](ADMIN_GUIDE.md#backups)).
4. **Never tap through a certificate warning** to "visit anyway". The warning means the iPhone can't confirm it is talking to your PC.

## The PC's IP address changed

The service notices a new address within about 10 minutes (or as soon as Windows reports a network change) and renews its certificate for the new address by itself.

The iPhone, however, opens the remote at the address it was set up with. If that address no longer works:

1. Find the new address (`ipconfig` on the PC) and open `http://<new address>:8080/` on the iPhone, then **Continue**.
2. The iPhone's sign-in is tied to the address it paired with, so you'll probably need to pair it again with a new code from **Admin → Remote devices**. The certificate profile doesn't need to be installed again. Revoke the old entry for that phone.

To avoid this, give the PC a fixed address: add a DHCP reservation for it on the Wi-Fi router.

## Reading logs and Event Viewer

**Service log** (the most detailed): `C:\ProgramData\SabhaMujic\logs\sabhamujic-YYYYMMDD.log`. There's a new file each day or every 10 MB, and the newest 14 are kept. Any user can read it, for example in Notepad. Each line has the time, the level (`INFO`, `WARN`, `ERRO` for errors, `CRIT`) and the part of the service that wrote it. To follow it live in PowerShell:

```powershell
Get-Content "$env:ProgramData\SabhaMujic\logs\sabhamujic-$(Get-Date -Format yyyyMMdd).log" -Tail 50 -Wait
```

**Event Viewer** (`eventvwr.msc`): **Windows Logs → Application**, then **Filter Current Log…** → Event sources **SabhaMujic.Service**. The service writes only warnings and errors here. If the service can't start at all, look in **Windows Logs → System** for events from **Service Control Manager** that mention SabhaMujic.

When asking for help, include the service log of the day of the problem and the time it happened. Logs can contain user names, device names and track titles, so share them only with people you trust.
