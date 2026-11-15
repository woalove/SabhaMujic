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
