# SabhaMujic administrator guide

For SabhaMujic administrators: adding music, organising it, tuning Smart Loop, and managing accounts, iPhones and backups. Installing and first-run setup are in [SETUP.md](SETUP.md); problems are covered in [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

Everything in this guide is in the desktop app under **Admin** (visible only to administrators), unless it says otherwise. The iPhone remote has an **Admin** tab for importing music and adding categories; it appears only on iPhones paired with the Admin role.

## Who can do what

| | Volunteer | Admin |
|---|---|---|
| Browse and search the library, play tracks | ✓ | ✓ |
| Control playback: play, pause, stop, next, previous, seek, volume, mute, loop mode, crossfade | ✓ | ✓ |
| Edit the queue, create and edit playlists | ✓ | ✓ |
| Import music, manage categories and tracks, set loop points | | ✓ |
| Manage accounts and paired iPhones | | ✓ |

The role comes from the SabhaMujic account (or, for an iPhone, from the role chosen when pairing), not from the Windows account. The service checks every action, whichever app or device asks.

## Importing music

**Admin → Import music**:

1. Click **Add files…** and choose audio files. Supported formats: **MP3, WAV, M4A, AAC, FLAC and WMA**, up to **1 GB per file**. Other file types are refused.
2. For each file, adjust the **Title** (the file name by default) and **Artist** (optional). Choose a category per file, or set **Category for new files** before adding them.
3. Click **Start import**. Each file is uploaded to the service, which first checks that it really decodes as audio (a renamed non-audio file is rejected). It then stores the file in the protected library and starts the Smart Loop analysis. The status column follows these stages: uploading, validating, storing, analyzing, done. **Cancel** stops the uploads still in progress. Signing out (or revoking the iPhone that uploads) also cancels that session's unfinished uploads.

Notes:

- **Duplicates**: a file whose content is byte-for-byte identical to a track already in the library is not added again; it shows **Already in library**. A re-encoded or trimmed copy of the same song is a different file and is imported as a new track.
- The service keeps imported files in `C:\ProgramData\SabhaMujic\library`, under content-based names. Keep your original files somewhere safe as well, ideally where volunteers can't reach them. The app never gives files back out.
- The desktop app uploads the files one after another; the service accepts at most 2 uploads at a time per signed-in admin.
- On an admin iPhone, use the remote's **Admin** tab. Pick files from the Files app, edit the title, artist and category, and upload.

## Categories

**Admin → Categories**: **New category**, **Rename**, **Delete**, and the up/down arrows to change the order in which categories appear in the library sidebar. Category names must be unique. Deleting a category keeps its tracks; they become uncategorized.

## Tracks and Smart Loop

**Admin → Tracks** lists the library with a search box. Select a track to:

- edit its **Title**, **Artist** and **Category** (**Save**);
- **Delete track…**: removes it from the library, from all playlists and from the queue, and deletes the stored file. If it is playing, playback moves on to the next track.

### How Smart Loop works

With the loop mode set to **Smart Loop**, the current track repeats seamlessly instead of ending. After import, the service analyses each track automatically:

- it trims silence at the start and end;
- it looks for a section that can repeat seamlessly (a "loop region"), skipping the intro and outro, and picks a short crossfade for the seam;
- it rates how sure it is as a **confidence** percentage. Below 40 %, it doesn't trust the region and instead loops the whole track (without the silence) with a 2-second crossfade.

The **Smart Loop** panel for the selected track shows the analysis status ("Waiting for analysis", "Analyzing…", "Analysis complete" or "Analysis failed (the whole trimmed track loops)"). It also shows the current loop, for example "Automatic: 0:12 → 3:45, crossfade 0.25 s, confidence 72%".

### Overriding the loop points

If the automatic loop sounds wrong, play the track and note where a good repeat starts and ends, in seconds. Then:

1. Enter **Loop start (s)**, **Loop end (s)** and **Seam crossfade (s)**. Decimals are allowed, and values are applied when you leave a box.
   - Start must be 0 or more and end after start, at most the track length.
   - The crossfade must be between 0 and half the loop length.
2. Click **Save manual loop**. The panel now says "Manual: …".

**Use automatic loop** removes your override and goes back to the analysis. **Re-analyze** runs the analysis again, for example after an update to SabhaMujic. A manual override is never lost to a re-analysis; it stays until you click **Use automatic loop**.

A change to the loop points (or a finished re-analysis) also reaches the track that is playing: it applies from the next loop seam that hasn't started yet. A seam already crossfading finishes as before.

## Playback settings

These are on the now-playing bar for everyone, and the service keeps them across restarts:

- **Loop mode** cycles **Off → Smart Loop → Repeat queue**. Off plays the queue once and stops; Smart Loop repeats the current track's loop region; Repeat queue starts the queue again from the top when it ends.
- **Crossfade** between tracks: 0–12 seconds (6 by default). Silence at the end of a track is skipped, so the fade starts where the music actually ends.
- **Volume** and **mute**.

## Playlists and the queue

Playlists are shared: every user sees and can edit the same playlists. On **Playlists**: **New playlist**, **Rename**, **Delete**, reorder tracks (drag or the arrows), remove tracks, **Add to queue** and **Replace queue**. To add a track to a playlist, right-click it in the library → **Add to playlist**.

The queue is shared by the desktop app and every iPhone; changes appear everywhere at once. **Clear** removes everything except the track playing now.

Playback runs in the service. Closing the app, signing out or locking an iPhone doesn't stop the music. After a service restart, the queue comes back stopped at the track that was current; press **Play** to continue near where it was. The position is saved when the service stops and whenever the queue or a setting changes, so after a crash it continues from the last save.

## Users and PINs

**Admin → Users**:

- **Create user**: a user name (1–64 characters, unique regardless of upper and lower case), a role and a PIN or password (at least 4 characters).
- **Reset PIN…**: sets a new PIN (enter it twice) and signs that user out everywhere. If you change your own PIN, only the session you are using stays signed in.
- **Delete**: removes the account and signs it out. You can't delete your own account, and the last administrator can't be deleted.

After 5 wrong PINs for the same user name within 5 minutes, sign-in for that name is paused for a few minutes (the app says "Too many sign-in attempts. Please wait a minute and try again.").

Keep at least **two administrator accounts**. If no administrator can sign in, a Windows administrator can reset an administrator PIN on the PC with **Forgot admin PIN?** on the sign-in screen (see [TROUBLESHOOTING.md](TROUBLESHOOTING.md#forgotten-administrator-pin)).

## Remote devices (iPhones)

**Admin → Remote devices** lists the paired iPhones with their role, when they were paired and when they were last seen.

- **Pair new device**: see [SETUP.md](SETUP.md#pair-an-iphone). Choose the role first. An iPhone paired as **Admin** has admin rights without a PIN, so pair as Admin only a phone an administrator keeps.
- **Revoke…**: removes a device immediately. A lost or replaced phone should be revoked. The phone goes back to the pairing screen and needs a new pairing code to be used again.
- **Sign out** on the iPhone unpairs that phone itself: it disappears from this list and needs a new pairing code.

Pairing codes are 6 digits, work once and expire after 5 minutes. Too many wrong codes pause pairing for a few minutes.

## Backups

The things worth backing up are in `C:\ProgramData\SabhaMujic`:

| Folder | Contents | Back up? |
|---|---|---|
| `data` | the database: accounts (PINs stored as salted hashes), library entries, categories, playlists, loop points, paired devices, settings, audit log | **Yes** |
| `library` | the imported audio files | **Yes** |
| `certs` | the iPhone remote's certificate authority (`ca.cer`) and its keys (`ca.key`, `leaf.pfx`, encrypted for this PC) | **Yes**, for restoring on the same PC (see below) |
| `uploads` | unfinished uploads | No |
| `logs` | service logs | No |

`data` and `library` belong together: back them up at the same time, with the service stopped, so the database and files match. Back up `certs` with them: restored on the same PC, it saves every iPhone from installing and trusting a new certificate.

1. Open **PowerShell as administrator**. `data`, `library` and `certs` are locked to the service, SYSTEM and administrators, so a normal window gets "Access denied".
2. Stop the service (this stops playback):

   ```powershell
   Stop-Service SabhaMujic
   ```

3. Copy the folders to the backup drive (replace `E:\SabhaMujic-backup` with your own location):

   ```powershell
   robocopy "$env:ProgramData\SabhaMujic\data"    "E:\SabhaMujic-backup\data"    /MIR
   robocopy "$env:ProgramData\SabhaMujic\library" "E:\SabhaMujic-backup\library" /MIR
   robocopy "$env:ProgramData\SabhaMujic\certs"   "E:\SabhaMujic-backup\certs"   /MIR
   ```

   `/MIR` makes the backup an exact mirror, including deletions. Robocopy exit codes below 8 mean success.
4. Start the service again:

   ```powershell
   Start-Service SabhaMujic
   ```

The backup holds the music as plain audio files. Keep it where volunteers can't copy it.

**Restore** (with SabhaMujic installed): stop the service, copy the folders back into `C:\ProgramData\SabhaMujic` with the same `robocopy` commands (source and destination swapped), then start the service. The service locks the folders again when it starts. Copy real folders: if a folder ends up as a junction or link, or owned by another account, the service refuses to start (see [TROUBLESHOOTING.md](TROUBLESHOOTING.md#setup-stops-or-the-service-wont-start-because-of-the-data-folder)).

**Certificates on a new PC**: the keys in `certs` are encrypted with Windows DPAPI for the PC (the Windows installation) they were made on, so `certs` can't be moved to another PC; restore only `data` and `library` there. If `certs` is missing or can't be decrypted, the service creates a new certificate authority, and every iPhone must remove the old profile, install and trust the new one, and pair again.

## Service settings

The service reads its settings from, in order of precedence, the service command line (`--SabhaMujic:Key=value`), environment variables (`SabhaMujic__Key`, with `__` between parts), and `appsettings.json` next to the service exe (`C:\Program Files\SabhaMujic\Service\appsettings.json`). Normally nothing needs to be set.

| Setting | Default | Meaning |
|---|---|---|
| `SabhaMujic:DataRoot` | `%ProgramData%\SabhaMujic` | Data folder |
| `SabhaMujic:PipeName` | `SabhaMujic.v1` | Desktop app connection; the app always uses the default, so don't change it |
| `SabhaMujic:Remote:Enabled` | `true` | Run the iPhone remote |
| `SabhaMujic:Remote:HttpsPort` | `8443` | Remote port (HTTPS) |
| `SabhaMujic:Remote:BootstrapEnabled` | `true` | Serve the plain-HTTP setup page |
| `SabhaMujic:Remote:BootstrapPort` | `8080` | Setup page port |

An invalid value (a port outside 1–65535, the same port twice, a flag other than `true`/`false`) stops the service at startup, before its own log starts. The message naming the setting is in **Event Viewer → Windows Logs → Application** (source **.NET Runtime**), or on screen when you run `SabhaMujic.Service.exe` by hand. The firewall rules created by the installer only cover 8443 and 8080; if you change the ports, allow the new ones yourself.

### Turning the iPhone remote off

1. In **Notepad run as administrator**, create `C:\Program Files\SabhaMujic\Service\appsettings.json` with:

   ```json
   { "SabhaMujic": { "Remote": { "Enabled": false } } }
   ```

2. In **PowerShell (admin)**: `Restart-Service SabhaMujic`. Nothing listens on 8443 or 8080 any more; the desktop app is unaffected.

To turn it back on, delete the file (or set `true`) and restart the service. The installer doesn't install or remove this file. To keep only the setup page off (once all iPhones are set up), set `"BootstrapEnabled": false` instead; new iPhones then can't install the certificate.

## Where things are

| What | Where |
|---|---|
| Program files | `C:\Program Files\SabhaMujic\Service` and `C:\Program Files\SabhaMujic\App` |
| Data root | `C:\ProgramData\SabhaMujic`, owned by Administrators. `data`, `library`, `uploads`, `certs`: only the service, SYSTEM and administrators. `logs`: standard users can read the logs; only the service (and administrators) can write there |
| Service settings (optional) | `C:\Program Files\SabhaMujic\Service\appsettings.json` (see [Service settings](#service-settings)) |
| Service logs | `C:\ProgramData\SabhaMujic\logs\sabhamujic-YYYYMMDD.log` (a new file each day or every 10 MB; the newest 14 files are kept) |
| Windows Event Log | **Event Viewer → Windows Logs → Application**, source **SabhaMujic.Service** (warnings and errors only) |
| Desktop app window settings | `%LOCALAPPDATA%\SabhaMujic\desktop-settings.json` (per Windows user) |
| Service | **Services** (`services.msc`) → **SabhaMujic Playback Service**; in PowerShell: `Get-Service SabhaMujic` |

The service also records an audit trail (sign-ins, imports, account and device changes) in its database. There is no screen for it yet.
