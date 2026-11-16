# Windows manual test checklist

Run on a real Windows 10 22H2+ / 11 PC. Record date, build commit, tester and results in `docs/TESTING.md`.
Anything not ticked here is **unverified**.

## Desktop shell (M4a; run `SabhaMujic.Desktop.exe --demo` unless noted)
- [ ] With no arguments: starts and shows "Connecting to SabhaMujic service…", and sign-in stays disabled (before M3 lands).
- [ ] `--demo`: the demo banner shows; admin/1234 and sevak/1111 sign in; the Admin entry appears only for admin.
- [ ] Main window resizes and can't go below 760×480. At minimum size the sidebar, centre, queue panel and now-playing bar are all usable without clipping.
- [ ] Queue panel hides and shows (sidebar button and ×), and the state survives a restart.
- [ ] Window position, size and maximized state survive a restart (`%LOCALAPPDATA%\SabhaMujic\desktop-settings.json`). An off-screen saved position falls back to centred.
- [ ] Space play/pause, Ctrl+→ next, Ctrl+← previous, Ctrl+F jumps to Library search. Space still types a space in the search box and login fields.
- [ ] Seek: clicking the bar seeks once. Dragging the thumb doesn't jump during the drag and seeks once on release. The time label shows the drag target.
- [ ] Volume, mute, loop-mode cycle, crossfade slider (0–12 s, whole seconds). The Crossfading badge appears near the end of a track.
- [ ] Library: category click filters (and switches to Library from another page); search filters as you type; Enter searches now; Esc clears; paging; double-click plays; the Play / Play next / Add buttons work.
- [ ] Right-click a track → Add to playlist → choose a playlist: the playlist's track count goes up.
- [ ] Queue: drag-reorder (in both the side panel and the Queue page), up/down, remove, Clear (with confirm), double-click plays, current row highlighted with a play icon.
- [ ] Playlists: create, rename, delete with confirm, drag-reorder tracks, Add to queue, Replace queue with confirm.
- [ ] Import: the Add files dialog filters to audio types; per-file title/artist/category editing; progress bars; Cancel mid-upload; re-importing the same file shows "Already in library"; a .txt file is rejected.
- [ ] Admin Tracks: edit and save, loop override validation messages, decimals can be typed in the loop boxes, "use automatic", re-analyze.
- [ ] Admin Users: create (PIN box clears afterwards), reset PIN dialog (both entries must match), delete with confirm.
- [ ] Admin Devices: Pair new device shows a scannable QR, a 6-digit code and a countdown from 5:00. On expiry the QR disappears and a red "expired" message shows. Revoke with confirm.
- [ ] Mini player: opens from the bar and hides the main window; transport, seek and volume work; the "On top" toggle works and is remembered; Restore or closing the mini player brings the main window back; its position is remembered.
- [ ] Ctrl+Shift+R in the demo: red "Reconnecting…" banner, then it clears after about 3 s and data reloads.
- [ ] Error banners appear and can be dismissed. No crash dialogs in normal use.
- [ ] All Segoe MDL2 icon glyphs render. The layout stays readable at 125% and 150% display scaling.

## Audio (M1): to be filled in when M1 merges
## Service, ACL, installer, iPhone remote (M3, M5, M6): to be filled in

## Audio (M1): `tools/SabhaMujic.AudioSpike`
Build it with `dotnet run --project tools/SabhaMujic.AudioSpike -c Release -- <file1> <file2> [--crossfade 6] [--smartloop] [--repeat]`.
- [ ] MP3, M4A/AAC, WMA, FLAC and WAV files each decode and play at the correct pitch and speed.
- [ ] Queue crossfade (6 s): smooth, with no dip or bump in loudness and no click.
- [ ] `--smartloop` on a real dhun/kirtan recording: the loop seam is inaudible or musically acceptable over at least 5 repetitions. Note the detected loop start/end and confidence the tool prints.
- [ ] A track with leading/trailing silence: the silence is skipped and the crossfade starts from the music's actual end.
- [ ] Unplug the headphones / disable the default device during playback: no crash, and playback resumes on the new default device.
- [ ] 30 minutes of continuous Smart Loop: no drift, crackle or memory growth (Task Manager).

## Service, ACLs, first-run setup (M3b)
- [ ] Install and start the service. `%ProgramData%\SabhaMujic\{data,library,uploads}` show only SYSTEM, Administrators and `NT SERVICE\SabhaMujic`, and a standard user gets "Access denied".
- [ ] Fresh install, app opened normally: the setup form says to use "Run as administrator". Run as administrator: the admin account is created and you're signed in.
- [ ] Audio from the service (session 0) is audible on the default device. Unplugging and replugging the device recovers. **(Risk #1)**
- [ ] Stopping the service fades out with no click. Starting again restores the queue (stopped at the same item), volume, loop mode and crossfade.
- [ ] Deleting the playing track (admin) skips to the next and removes the file from `library`.
- [ ] `logs\sabhamujic-*.log` grows and rolls, and entries appear in Event Viewer.
- [ ] With the service stopped, another process holding the pipe name makes the service fail to start (anti-squatting).
- [ ] With the service running, playback continues while the desktop app is closed or killed.

## iPhone remote (M5): needs a PC with the service running and an iPhone on the same Wi-Fi
- [ ] The PC firewall allows TCP 8443 and 8080 on the Private profile. `http://<pc-ip>:8080/health` returns OK from the phone.
- [ ] Scanning the desktop pairing QR opens `http://<ip>:8080/#code=NNNNNN` in Safari. The page shows the code and the CA SHA-256 fingerprint, and the fingerprint matches the one on the PC.
- [ ] "Download profile" → Allow → Settings → General → VPN & Device Management → SabhaMujic Remote → Install. The profile shows "SabhaMujic Local CA (<PC name>)".
- [ ] Settings → General → About → Certificate Trust Settings: full trust for the SabhaMujic CA can be enabled.
- [ ] "Continue" opens `https://<ip>:8443/pair#code=NNNNNN` with no certificate warning and the code prefilled. Pairing with a device name shows the Now tab, and the pill says Connected.
- [ ] `https://<pc-hostname>.local:8443/` also loads without a warning.
- [ ] Transport (play/pause, prev, next, stop), seek, volume, mute, loop mode, crossfade and the Crossfading badge all work; the position advances smoothly.
- [ ] A second iPhone sees changes live.
- [ ] Queue (play item, up/down, remove, clear with confirm), Library (chips, search, load more, play now / play next / add to queue), Playlists (add, replace with confirm).
- [ ] A volunteer device shows no Admin tab.
- [ ] Admin tab: pick several files from Files, edit title/artist/category, upload with progress, cancel mid-upload, re-upload the same file ("Already in the library"), add a category.
- [ ] Lock the phone or toggle Wi-Fi: the pill shows Reconnecting… and returns to Connected with current state. Restarting the service: same.
- [ ] **Playback continues on the PC while every phone is locked, offline or closed.**
- [ ] Revoking the device on the PC returns the phone to the pair screen; reloading stays signed out.
- [ ] Add to Home Screen: the icon shows and the app opens standalone. If iOS keeps separate cookies and asks to pair, pairing with a new code works.
- [ ] Light and dark mode read well, touch targets are comfortable, and iOS 16.x is checked if available.
- [ ] After the PC's IP changes, the phone connects without a warning within about 10 minutes (the leaf is re-issued).
- [ ] The leaf certificate works with SChannel under the service account.

## M6a additions
- [ ] The phone setup page's fingerprint equals the one on the PC pairing panel.
- [ ] "Sign out" on the phone removes it from the PC device list; the phone needs a new code.
- [ ] After installing the MSI, the unelevated first-run screen offers "Restart as administrator". Confirming creates the admin; cancelling UAC shows a message and keeps the app open.
- [ ] Setting a loop override while that track plays takes effect at the next seam.
- [ ] Restart the service mid-track, then press Play: it continues near the old position, with the same queue.
- [ ] `%ProgramData%\SabhaMujic\certs` is ACL'd like `data`, and `ca.key` isn't readable as plain data.
- [ ] With `SabhaMujic:Remote:Enabled=false`, nothing listens on 8443 or 8080.
