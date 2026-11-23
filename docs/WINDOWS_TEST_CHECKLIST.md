# Windows manual test checklist

Run on a real Windows 10 22H2+ / 11 PC. Record date, build commit, tester and results in `docs/TESTING.md`.
Anything not ticked here is **unverified**.

## Desktop shell (M4a; run `SabhaMujic.Desktop.exe --demo` unless noted)
- [ ] With no arguments and the service stopped: starts and shows "Connecting to SabhaMujic service…", and Admin sign-in stays disabled until it connects.
- [ ] `--demo`: the demo banner shows and the player works with no sign-in; **Admin sign-in** with admin / demo-admin shows the Admin item in the nav rail; **Sign out** hides it again.
- [ ] Main window resizes and can't go below 760×480. At minimum size the nav rail, centre, queue panel and now-playing bar are all usable without clipping.
- [ ] Queue panel hides and shows (queue-panel button and ×), and the state survives a restart.
- [ ] Window position, size and maximized state survive a restart (`%LOCALAPPDATA%\SabhaMujic\desktop-settings.json`). An off-screen saved position falls back to centred.
- [ ] Space play/pause, Ctrl+→ next, Ctrl+← previous, Ctrl+F jumps to Library search. Space still types a space in the search box and sign-in fields.
- [ ] Seek: clicking the bar seeks once. Dragging the thumb doesn't jump during the drag and seeks once on release. The time label shows the drag target.
- [ ] Volume, mute, loop-mode cycle, crossfade slider (0–12 s, whole seconds). The Crossfading badge appears near the end of a track.
- [ ] Library: clicking a category tab filters (and switches to Library from another page); search filters as you type; Enter searches now; Esc clears; paging; double-click plays; the Play / Play next / Add buttons work.
- [ ] Right-click a track → Add to playlist → choose a playlist: the playlist's track count goes up.
- [ ] Queue: drag-reorder (in both the side panel and the Queue page), up/down, remove, Clear (with confirm), double-click plays, current row highlighted with a play icon.
- [ ] Playlists: create, rename, delete with confirm, drag-reorder tracks, Add to queue, Replace queue with confirm.
- [ ] Import: the Add files dialog filters to audio types; per-file title/artist/categories editing; progress bars; Cancel mid-upload; re-importing the same file shows "Already in library"; a .txt file is rejected.
- [ ] Admin Tracks: edit and save, loop override validation messages, decimals can be typed in the loop boxes, "use automatic", re-analyze.
- [ ] Admin › Administrators: create (password box clears afterwards; fewer than 8 characters refused), Reset password… dialog (both entries must match), delete with confirm.
- [ ] Admin › Phones: **Set up a phone** shows a scannable QR for the setup page, the setup page and remote addresses, and the certificate fingerprint. **Forget…** asks for confirmation.
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
- [ ] Scanning the QR in Admin › Phones › **Set up a phone** opens `http://<ip>:8080/` in Safari. The page shows the CA SHA-256 fingerprint, and it matches the one on the PC.
- [ ] "Download profile" → Allow → Settings → General → VPN & Device Management → SabhaMujic Remote → Install. The profile shows "SabhaMujic Local CA (<PC name>)".
- [ ] Settings → General → About → Certificate Trust Settings: full trust for the SabhaMujic CA can be enabled.
- [ ] "Continue" opens `https://<ip>:8443/` with no certificate warning and shows the sign-in screen. Signing in with an administrator's name and password shows Now Playing, and the pill says Connected.
- [ ] `https://<pc-hostname>.local:8443/` also loads without a warning.
- [ ] Transport (play/pause, prev, next, stop), seek, volume, mute, loop mode, crossfade and the Crossfading badge all work; the position advances smoothly.
- [ ] A second iPhone sees changes live.
- [ ] Queue (play item, up/down, remove, clear with confirm), Library (category tabs, search, load more, play now / play next / add to queue), Playlists (add, replace with confirm).
- [ ] Signing in with a non-administrator account (only possible on an install upgraded from v1) shows "Only administrators can use the SabhaMujic phone remote."
- [ ] Upload tab: pick several files from Files, edit title/artist, choose several categories, upload with progress, cancel mid-upload, re-upload the same file ("Already in the library"), add a category.
- [ ] Lock the phone or toggle Wi-Fi: the pill shows Reconnecting… and returns to Connected with current state. Restarting the service: same.
- [ ] **Playback continues on the PC while every phone is locked, offline or closed.**
- [ ] Forgetting the phone on the PC returns it to the sign-in screen; reloading stays signed out.
- [ ] Add to Home Screen: the icon shows and the app opens standalone. If iOS keeps separate cookies and asks to sign in, signing in there works.
- [ ] Light and dark mode read well, touch targets are comfortable, and iOS 16.x is checked if available.
- [ ] After the PC's IP changes, the phone connects without a warning within about 10 minutes (the leaf is re-issued).
- [ ] The leaf certificate works with SChannel under the service account.

## M6a additions
- [ ] The phone setup page's fingerprint equals the one on the PC's **Set up a phone** panel.
- [ ] "Sign out" on a remembered phone removes it from Admin › Phones; the phone needs the password again.
- [ ] After installing the MSI, the unelevated first-run screen offers "Restart as administrator". Confirming creates the admin; cancelling UAC shows a message and keeps the app open.
- [ ] Setting a loop override while that track plays takes effect at the next seam.
- [ ] Restart the service mid-track, then press Play: it continues near the old position, with the same queue.
- [ ] `%ProgramData%\SabhaMujic\certs` is ACL'd like `data`, and `ca.key` isn't readable as plain data.
- [ ] With `SabhaMujic:Remote:Enabled=false`, nothing listens on 8443 or 8080.

## Installer (M6): run the CI-built `SabhaMujic-0.1.<run>-x64.msi` on a real PC without SabhaMujic
The silent install, uninstall, upgrade and refused downgrade are covered by `installer/smoke-test.ps1` on CI. These items need a person.
- [ ] Double-clicking the downloaded MSI: SmartScreen shows "Windows protected your PC"; **More info → Run anyway** starts setup. Note the exact wording.
- [ ] Dialogs, in order: Welcome → **About this software** (the RTF renders with bold headings, bullet indentation and a readable font; the text matches `AboutThisSoftware.rtf`; the "I accept" box enables Next; **Print** works) → **Custom Setup** → Ready → progress (one UAC prompt) → Finish. No truncated or overlapping text at 100% and 150% scaling.
- [ ] Custom Setup: "SabhaMujic" can't be set to unavailable; "Desktop shortcut" can. With it deselected, no desktop shortcut is created. **Change** from Settings → Apps adds it later.
- [ ] Finish page: the optional text and the **Launch SabhaMujic** checkbox (ticked) are shown. Finishing launches the app **unelevated** (Task Manager → Details → "Elevated" column says No), and the first-run screen leads to "Restart as administrator".
- [ ] Unticking **Launch SabhaMujic** finishes without starting the app.
- [ ] Start menu "SabhaMujic" and the desktop shortcut (Public Desktop) open the app; check which icon they show and whether right-click → **Run as administrator** works on them.
- [ ] Services: "SabhaMujic Playback Service", **Automatic (Delayed Start)**, log on as `NT SERVICE\SabhaMujic`, Recovery tab: restart after 5 s for all three failures. After a reboot the service starts by itself within about 2 minutes.
- [ ] Windows Defender Firewall → Inbound rules: "SabhaMujic Remote (HTTPS)" and "SabhaMujic Remote setup (HTTP)", Private profile only. No Windows Firewall prompt appears when the service starts listening.
- [ ] Running the newer MSI interactively over an installed older one upgrades in place (data kept). Running an older MSI over a newer one shows the downgrade message.
- [ ] Uninstall from Settings → Apps: no leftover program folder, shortcuts or firewall rules; `C:\ProgramData\SabhaMujic` is kept. The "Remove everything" steps in `docs/SETUP.md` then work as written.

## M6d additions
- [ ] After install, `%ProgramData%\SabhaMujic` and its subfolders are owned by Administrators with the exact DACLs in ARCHITECTURE #34.
- [ ] Pre-create the root as a standard user, then install: the installer's `--prepare-data-root` step takes it over (or the service fails with a clear Event Log entry until it's run).
- [ ] A junction in place of `library` stops the service with a clear error.
- [ ] A standard user can read `logs` but can't write there.
- [ ] Event Log entries appear under source `SabhaMujic.Service` in the Application log.
- [ ] "Forgot admin password?" unelevated shows the elevation message; "Restart as administrator" reopens the recovery screen; the reset signs in and the old password stops working.
- [ ] The icon shows in the exe, taskbar, title bar and mini player.
- [ ] Leave the app open with no administrator signed in for more than 2 minutes: no "Reconnecting" banner.
- [ ] Interrupt storage mid-session: commands keep working, and the queue is saved once storage recovers.

## v2 additions: none of these have been done yet
Run with the installed v2 MSI and the service running, unless noted. Phone items need a real iPhone with Safari.
- [ ] **Open use:** with an administrator already created, the app starts on the Library with no sign-in. Playing, the queue and playlists work. The header chip says "Admin sign-in" and the nav rail has no Admin item.
- [ ] **Admin sign-in:** the chip asks for user name and password; a wrong password shows "Incorrect user name or password."; a correct one shows the name in the chip and Admin in the nav rail. **Sign out** from the chip menu works.
- [ ] **15-minute sign-out:** using the app keeps the admin signed in. Left untouched for about 15 minutes, the admin is signed out with the "administrator session has ended" note, the Admin item disappears (an open Admin page goes back to Library), and the music keeps playing.
- [ ] **Theme:** dark on first start; the sun/moon toggle switches every screen, dialog and the mini player; the choice survives a restart of the app. Same on the phone after a reload.
- [ ] **Custom title bar** at 100 %, 125 % and 150 % display scaling: drag to move, double-click to maximise/restore, drag to a screen edge and Win+arrow keys snap, Windows 11 snap layouts appear over the maximise button, minimise/maximise/close work, edges resize, and a maximised window doesn't cover the taskbar or cut off content.
- [ ] **Inter font:** text renders in Inter, not Segoe UI. Regular, Medium, SemiBold and Bold look clearly different (titles, tabs, buttons). Times and counts line up (tabular figures).
- [ ] **Icon glyphs:** nav rail, header, title-bar buttons, transport and list buttons all show icons, with no empty boxes.
- [ ] **Two-way categories:** on a track, **Categories…** with two ticks shows two tags. **Add tracks…** from Admin › Categories and from right-clicking a Library tab adds and removes tracks and keeps their other categories. Deleting a category keeps its tracks. The phone shows the same.
- [ ] **Multi-select import:** **Add files…** accepts several files at once; each file can get several categories; **Categories for new files** and **Apply to all** work; every file ends up with the right categories.
- [ ] **Phone sign-in without "Remember this phone"** (real iPhone Safari): works; the phone isn't listed under Admin › Phones; after closing Safari completely and reopening, it asks to sign in again.
- [ ] **Phone sign-in with "Remember this phone"** (real iPhone Safari): the phone is listed under Admin › Phones as "iPhone"; it stays signed in after closing Safari and restarting the phone; the Home Screen app works too. Private Browsing forgets the sign-in when the tab closes.
- [ ] **Phone look:** Inter renders (including bold and semi-bold); the seek, volume and crossfade sliders are easy to drag with a thumb; the header and bottom nav (Now Playing, Queue, Library, Playlists, Upload) sit clear of the notch/Dynamic Island and the home indicator, in Safari and in the Home Screen app, portrait and landscape.
- [ ] **Forget a phone** in Admin › Phones: it leaves the list at once; the phone shows "This device was removed by an administrator." and the sign-in screen; reloading stays signed out; signing in again with Remember adds it back.
- [ ] **Upgrade from a v1 install** (tracks in categories, playlists, loop overrides, a volunteer account, an admin phone and a volunteer phone): after installing v2, every track keeps its category and playlists and loop points are kept; the old admin PIN signs in; the volunteer account is listed as "Volunteer"; the admin phone asks to sign in and then works; the volunteer phone gets "Only administrators can use the SabhaMujic phone remote."; both old phones are listed under Admin › Phones and can be forgotten.
