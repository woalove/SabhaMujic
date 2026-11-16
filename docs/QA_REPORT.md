# QA report: M6 reliability, soak, performance and independent review

Date: 2026-11-16. Base: `5ca15cc` (`claude/awesome-cerf-aa8wbt`). Environment: Linux cloud container, 4 cores, 15 GB RAM, .NET 10.0.15. Other agents were building and testing on the same machine during these runs, so wall-clock numbers are upper bounds.

Everything here ran on Linux with the WAV decoder and `NullRealtimeOutput`. Nothing in this report verifies WASAPI, Media Foundation, the Windows service, ACLs or real audio quality. Those stay **unverified** until Windows CI or the manual checklist covers them.

## 1. What was built

| Piece | Where | What it does |
|---|---|---|
| Soak and perf harness | `tests/SabhaMujic.Soak` (console app, net10.0) | `soak --hours <sim h> --speed <x> --seed <n> --report <json>`: accelerated soak of a real `SabhaHost`. `perf --report <json> [--startup-only]`: performance measurements. Exit code 0 means no invariant violations and no unexpected log entries. |
| Soak engine (shared) | `tests/SabhaMujic.Server.Tests/Reliability/Shared/*.cs` | Compiled into the test project and linked into the soak app, so the mini-soak and the 8-hour run use the same code. |
| Reliability tests | `tests/SabhaMujic.Server.Tests/Reliability/*.cs` | Mini-soak, fault injection, disk full, concurrency stress, authorization under load, plus skip-marked bug reproductions. |

### Soak harness design
- **Host:** a real `SabhaHost` on a temp data root. It has the real engine, the WAV decoder, `NullRealtimeOutput` at `--speed`, SQLite, the protected store and the pipe server. The real `RemoteHost` (HTTPS + SignalR, local CA, loopback) runs as an `IHostedComponent`, so it stops and starts with the host.
- **Simulated time** is the null output's rendered frames. It stays exact even when the machine is too loaded to keep up.
- **Library:** 30 generated 48 kHz WAVs, 20 s to 4 min long, of three kinds:
  - loopable (intro, a repeated 8 s pattern, outro)
  - silence-padded tones (2–5 s of digital silence on each side)
  - through-composed (no repeats)

  The tracks are imported over the pipe like an admin would. Then one stored file is overwritten with garbage and one is cut to a third of the length its header claims, both behind the database's back.
- **Clients:**
  - an admin pipe client
  - 4 volunteer `PipeSabhaClient`s, churned by abrupt disposal with no logout
  - 2 paired phones (SignalR, device tokens)
  - occasional raw pipe connections that send half a JSON-RPC frame and vanish
- **Workload:** seeded random actions, on average one every 20 simulated seconds:
  - play item or track, pause and stop (each with a scheduled resume), next and previous, seek
  - loop mode, crossfade, volume and mute
  - enqueue (sometimes the broken tracks), move, remove (sometimes the current item), clear, playlists CRUD and enqueue-playlist
  - admin imports and deletes; a third of the deletes hit the playing track
  - hub commands
  - "zero client" windows: every client, admin included, disconnects for 2–8 simulated minutes
  - one graceful host restart at 50 %, with the restore checked
- **Sampling:** every simulated minute:
  - the managed heap after a forced, compacting, blocking GC
  - working set, OS threads, `/proc/self/fd` count and thread-pool threads
  - engine status, queue and library size, pipe and hub connections, broadcaster subscribers, pending analyses
  - event lag (from server snapshot timestamp to client handler) and estimated render underruns
- **Invariants:** checked continuously by a 20 ms monitor, by periodic convergence checks, and by watchdogs on every call:
  - `StuckLoading`: Loading lasts more than 10 s of wall time.
  - `StuckCrossfading`: Crossfading lasts more than 10 s plus the crossfade time.
  - `PositionWentBackwards`: the position decreases within the same queue item with no command in between, and Smart Loop or a one-item repeat doesn't explain it.
  - `PlaybackStalled`: the status is Playing but the position is frozen.
  - `UnexpectedStop`: playback stops although no client paused or stopped it, the queue didn't legitimately run out (loop Off, nothing playable after the current item), and the queue isn't all broken tracks. This is classified when the stop is first seen, so it also covers the zero-client windows.
  - `ServerState/QueueRevisionOrder`: backend state or queue revisions are not strictly increasing.
  - `State/QueueRevisionOrder`: per client, per server epoch, pushed revisions are not strictly increasing.
  - `ClientDiverged`: every 10 samples the workload pauses, and every connected pipe and hub client's pushed state (status, item, loop, volume, mute, crossfade) and queue revision must match the server within 10 s.
  - `RestoreQueueMismatch` / `RestoreSettingsMismatch` after the restart, and `ClientDidNotReconnect` (pipe clients must reconnect and resume their sessions by themselves).
  - `HungCall`: a call takes more than 30 s (watchdog).
  - `InternalError`: any `ErrorCode.Internal` reply.
  - Unexpected log entries: every warning or error, or any entry carrying an exception, is captured. Only planted-fault warnings count as expected (playback skips and analysis failures of the broken files).
- **Underruns:** estimated over stretches where the same voice plays with no command in between. They are the rendered frames minus the voice's advance, with one block of slack and the voice's last second excluded.

## 2. 8-hour soak results

Command: `dotnet run -c Release --project tests/SabhaMujic.Soak -- soak --hours 8 --speed 40 --seed 1 --report soak.json`

| | Result |
|---|---|
| Simulated / wall time | **8.00 h** in **12.2 min** (setup 14 s: 30 tracks generated, imported and analysed) |
| Invariant violations | **0** |
| Unexpected log entries | **0** (expected: 4 skips of the corrupt file, 1 failed analysis of it, 12 abnormal pipe disconnects caused by the churn) |
| Client convergence checks | 49, 0 failures |
| Workload | 1,397 actions, all successful. This includes 25 admin deletes (11 of the playing track), 26 imports, 45 pipe client churns, 8 phone churns, 12 junk connections, 5 zero-client windows and 9 re-enqueues of the broken files |
| Restart at 4.0 h | stop 1.09 s, start 0.43 s. Queue (12 items, current index 6) and settings restored exactly. Every pipe client reconnected and resumed its session 1.52 s after the stop began. Playback resumed. |
| Playing / loading / crossfading | 88.7 % of the time (the rest is deliberate pauses and stops); 1,001 state transitions |
| Event lag (18,223 pushes) | p50 0.44 ms, p95 0.73 ms, p99 1.78 ms, max 16.7 ms |
| Render underruns (estimated) | 0 frames; mixer events dropped: 0 |

Growth after warm-up (samples from 1.2 h onwards; the slope is a least-squares fit per simulated hour):

| Metric | First | Last | Max | Slope / sim h |
|---|---|---|---|---|
| Managed heap after full GC (MB) | 8.8 | 21.5 | 34.7 | +0.45 |
| Working set (MB) | 199 | 239 | 240 | +4.7 |
| OS threads | 28 | 27 | 29 | −0.11 |
| Open FDs | 301 | 314 | 315 | +1.1 |
| Thread-pool threads | 10 | 9 | 11 | −0.04 |
| Event subscribers | 7 | 7 | 7 | 0 |

Per simulated hour:

| Sim h | Heap min / avg / max (MB) | WS avg (MB) | FDs | Max threads | Library tracks at end |
|---|---|---|---|---|---|
| 0–1 | 7.6 / 22.7 / 47.6 | 249 | 287–306 | 33 | 29 |
| 1–2 | 6.8 / 16.9 / 26.2 | 192 | 291–312 | 29 | 28 |
| 2–3 | 12.3 / 22.6 / 28.8 | 205 | 305–312 | 29 | 29 |
| 3–4 | 6.1 / 11.9 / 20.1 | 189 | 305–308 | 29 | 26 |
| 4–5 (after restart) | 9.1 / 17.1 / 25.6 | 207 | 295–314 | 29 | 28 |
| 5–6 | 6.5 / 15.6 / 24.6 | 208 | 311–314 | 29 | 26 |
| 6–7 | 6.0 / 15.3 / 30.0 | 202 | 295–315 | 29 | 25 |
| 7–8 | 17.1 / 24.1 / 34.7 | 228 | 294–315 | 27 | 31 |

**Reading of the growth numbers:**
- **Heap:** there is no monotonic heap growth. The live heap swings between 6 and 35 MB, depending on what is in flight when the sample is taken. Each voice holds about 0.4 MB, and imports hold 2–5 MB of WAV bytes in the harness. The hourly minimum doesn't trend.
- **Last hour:** the higher heap in the last hour coincides with the largest library (31 tracks) and a burst of imports.
- **Threads and handles:** OS threads and the thread pool are flat. FDs stay within a band of 287–320; the band moves with the number of live voices and pipe and hub connections, and doesn't climb.
- **Restart:** the restart in the middle didn't leave anything behind. Thread and FD counts after it match those before it.
- **Caveat:** the working set drifts upwards by about 5 MB per hour across runs, with a 240–255 MB maximum. That is within the noise of a GC that hasn't been asked to trim, and two earlier runs showed +2.7 and +7.7 MB per hour with non-monotonic samples. An 8-hour run cannot rule out a slow native leak of a few MB per hour. A 24-hour real-time run on Windows with WASAPI is the right confirmation and is **not done**.

Earlier runs, kept for honesty:
- The first 8-hour run had 0 violations, but it never actually re-queued the broken files. The workload now re-enqueues them.
- The second run reported one `UnexpectedStop`. Investigation showed a harness false positive: the queue legitimately ran out, and an `Enqueue` a moment later made the classifier think items remained. The classifier now decides when the stop is first seen, and violations now carry the last 8 actions for diagnosis.
- The final run above is the third.

## 3. Reliability tests (part of `dotnet test`)

All are in `tests/SabhaMujic.Server.Tests/Reliability`. Wall time: about 23 s for the whole folder, running in parallel with the rest of the suite (each class gets its own host).

| Test | Result |
|---|---|
| `MiniSoakTests`: 30 simulated minutes at 120×, the full workload including the SignalR phone, the restart and zero-client windows | Pass. Asserts zero violations, zero unexpected log entries, at least 2 convergence checks, and exact queue and settings restore. Leak thresholds after warm-up: heap growth < 48 MB, FD growth < 60, thread growth < 30. Typical run: about 22 s wall. |
| Corrupt and truncated files in the middle of the queue | Pass. The corrupt file is skipped and clients get `ErrorMessage` naming its track id (never a path). The truncated file plays as far as it goes, then the next item starts. No stall. |
| A file deleted from disk behind the DB | Pass. The item is skipped with an error, and the next item plays. |
| `DeviceLost` / `DeviceRestored` (`NullRealtimeOutput.Simulate*`) | Pass. The device error is pushed to clients while the status stays Playing and the position advances. A second loss followed by a restore clears it. |
| SQLite locked by `BEGIN EXCLUSIVE` from a second connection for 7 s | Pass. The engine keeps playing from memory and queue edits return immediately. The playlist write waits for the lock and then succeeds, and the debounced queue save lands after the lock is released. |
| Host stop during an active import (mid-upload, and three races against `CompleteImport`) | Pass. Stop finishes in time, no partial track and no orphan file remain, the staging folders are empty after restart, and failures carry defined error codes. |
| Disk full (the data root on a 24 MB tmpfs, filled up; `[TmpfsFact]` runs only where a tmpfs can be mounted, so it is skipped on GitHub runners and Windows) | Pass here. The import fails with a defined code (it is `Internal`; see R-7) and leaves nothing behind once the client cancels, as the desktop app does. The playback save fails with `SQLITE_FULL` and is logged, while playback continues. After space is freed, saves and imports work again. |
| Concurrency stress: 20 pipe clients (2 admins, 18 volunteers), random commands for 10 s | Pass. About 6,200 calls, no watchdog hit (20 s), and only `NotFound` races besides OK, never `Internal`. Every client converges on the server state, all clients see identical queues, the engine answers `SyncAsync` within 5 s, the subscriber count equals the connection count and returns to 0 after disconnect, and heap growth stays under 64 MB. |
| Authorization under stress: 6 volunteers and 2 unauthenticated connections hammer all 19 admin operations, including the admin's live upload ids, while the admin imports 3 tracks | Pass. About 850 attempts: **0 successes**, only `Forbidden` and `Unauthenticated`. A DB snapshot (all tables except audit, sessions and playback state) is identical apart from the admin's three imports, and those all completed. |
| BUG-01, BUG-02, BUG-03 reproductions | `[Fact(Skip = "BUG-NN: …")]`. Each one was run unskipped once and confirmed to fail on the current code. |

## 4. Bugs found

| ID | Severity | Summary | Repro |
|---|---|---|---|
| BUG-01 | Medium | A failed playback-state save is never retried | `FaultInjectionTests.A_failed_playback_state_save_is_retried_after_the_lock_is_released` (skip) |
| BUG-02 | Medium | Tapping an unplayable track stops the music that was playing, and the error message is wrong | `FaultInjectionTests.Asking_for_an_unplayable_track_keeps_the_current_track_playing` (skip) |
| BUG-03 | Medium | 32 idle, never-authenticated pipe connections lock the desktop app out | `PipeExhaustionTests.Idle_unauthenticated_connections_cannot_lock_out_the_desktop_app` (skip) |

### BUG-01: a failed playback-state save is never retried
- **Minimal reproduction:**
  1. Queue one track; it is saved.
  2. Take `BEGIN EXCLUSIVE` on the DB from another connection.
  3. Enqueue a second track.
  4. After about 30 s, "Saving the playback state failed; it will be retried on the next change." is logged.
  5. Release the lock and change nothing else.
  6. The DB still holds a one-item queue indefinitely. A crash or power loss now restores the stale queue. The same happens with `SQLITE_FULL` (seen in the disk-full test).
- **Root cause:** `src/SabhaMujic.Server/Playback/PlaybackPersistence.cs:143-159`. `SaveFromTimerAsync` logs and drops the failure. `Schedule()` (`:130-139`) only runs on the next `StateChanged`/`QueueChanged` that differs from the last saved state.
- **Suggested fix:** in the catch, re-arm the timer with backoff (for example 1 s, 2 s, up to 30 s) while `_disposed` is false. Since `SaveAsync` compares against `_savedQueueRevision`/`_savedSettings`, a retry is idempotent. Also consider `PRAGMA busy_timeout` alone, without the 30 s `DefaultTimeout` retry loop (see R-6), so failures surface sooner and are retried by this loop.

### BUG-02: an unplayable requested track stops the current playback
- **Minimal reproduction:**
  1. A good track is playing, with loop mode Off and nothing after it in the queue.
  2. Its stored file is missing or corrupt for track X.
  3. Call `PlayTrackAsync(X)`.
  4. The engine inserts X after the current item, fails to open it, finds nothing after it, and calls `_mixer.Stop()`. The status becomes `Error` and the music stops. `ErrorMessage` reads "None of the queued tracks could be played." although the queue holds a perfectly playable track.
  5. The same happens with `PlayQueueItemAsync` on such an item. With RepeatQueue it instead wraps to an arbitrary earlier item.
- **Root cause:** `src/SabhaMujic.Audio/Engine/PlaybackEngine.cs:508-590` (`StartItem`). The failure tail at `:580-589` stops the mixer and clears `_current`, even when the call was user-initiated and something audible was already playing (`audible` is computed at `:516` but not used for this decision). The message at `:589` claims all queued tracks failed, while only the candidates from `start` onward were tried.
- **Suggested fix:** when `userInitiated && audible` and no candidate opens, leave `_current`, the mixer and the machine state as they were (fire `LoadFailed` back to Playing), and set `_error = "Track … could not be played"`. Only reach "None of the queued tracks…" when every item was actually tried.
- **Why it matters:** a volunteer tapping one broken track in the middle of a sabha should not silence the hall.

### BUG-03: idle, never-authenticated pipe connections lock out the desktop app
- **Minimal reproduction:**
  1. Open 32 `NamedPipeClientStream`s to the service pipe and send nothing.
  2. The desktop client (`PipeSabhaClient`) can no longer connect: it is accepted and immediately closed, and then retries forever.
  3. On Windows any "Authenticated User" on the PC can do this, because the pipe DACL allows it by design.
- **Root cause:** `src/SabhaMujic.Server/Pipe/PipeRpcServer.cs:163` enforces a global `MaxConnections` of 32. `PipeConnection.RunAsync` (`:195-251`) has no handshake or idle timeout for connections that never call `Hello`/`Login`, and no per-principal cap.
- **Suggested fix:**
  - Close connections that haven't completed `HelloAsync` within about 10 s and remain unauthenticated after about 2 min.
  - Optionally reserve a few slots for authenticated sessions, or cap connections per OS principal.
  - Log rejected principals for the audit trail.
- **Severity:** Medium, because the attacker must be a local Windows user. Real Windows pipe behaviour is **unverified**; this was reproduced over Unix domain sockets.

## 5. Performance measurements

These are not pass/fail. Command: `soak perf`. Each figure is the median of what was seen across two runs on this loaded 4-core container.

| Measurement | p50 | p95 | p99 | max |
|---|---|---|---|---|
| Pipe `GetState`, 1 client (2,000 calls) | 0.81 ms | 1.18 ms | 1.61 ms | 5.6 ms |
| Pipe `SetVolume`, 1 client (1,000 calls, includes the engine round-trip) | 0.80 ms | 1.09 ms | 1.47 ms | 3.5 ms |
| Pipe `GetState`, 10 clients in parallel (3,000 calls) | 1.07 ms | 2.17 ms | 3.93 ms | 13.8 ms |
| Event fan-out to 10 clients, per delivery (300 volume changes; measured from the start of the `SetVolume` call to each client's handler) | 1.20 ms | 1.80 ms | 2.72 ms | 3.4 ms |
| Event fan-out, slowest of the 10 | 1.26 ms | 1.85 ms | 2.80 ms | 3.4 ms |
| Soak event lag (snapshot to handler, 18k pushes over 8 h) | 0.44 ms | 0.73 ms | 1.78 ms | 16.7 ms |

- **Analysis time:** about **0.14–0.17 s of CPU per minute of audio**. Measured on 48 kHz mono WAV, single-threaded `LoopAnalyzer.Analyze` including decoding. Lengths 1, 3 and 5 min took 0.14 s, 0.47 s and 0.79 s; a 3 min through-composed track took 0.52 s. So a 1,000-track library of 4-minute songs analyses in about 10 min on one background thread.
- **Engine CPU at 1× real time**, 60 s of playback, `NullRealtimeOutput(1.0)`, whole process:

  | Scenario | % of one core | Of which idle baseline |
  |---|---|---|
  | Smart Loop, one 3 min track | 4.0–5.2 % (1.0–1.3 % of the machine) | 3.9 % |
  | Repeat queue (40 s + 3 min) with 12 s crossfades | 4.0–4.2 % | 3.9 % |

  The engine's own cost above idle is about 0–1.3 % of a core. Most of the baseline is the null output's 1 ms polling loop, which WASAPI doesn't have. **WASAPI CPU is unverified.**
- **Memory per voice:** **404 KiB** managed. 375 KiB of that is the 1 s stereo float ring buffer; the rest is the decoder and bookkeeping. At most about 8 voices are live during crossfades and loop seams, so about 3 MB.
- **`SabhaHost.StartAsync` with a 1,000-track library:** seeded through the repositories with small real WAVs in the store and a 50-item persisted queue.
  - First start in a fresh process: 0.06 s with analyses Ready.
  - Warm restarts (same process): under 10 ms.
  - With all 1,000 analyses Pending: 0.01 s. The 1,000 tracks are queued for background analysis and startup isn't blocked.
  - First `GetTracks` page (200 tracks) over the pipe: 14–58 ms. All 5 pages: 13–23 ms.
  - Managed heap after start: 4–10 MB.
  - This excludes the HTTPS remote's Kestrel start, which isn't part of `SabhaHost` yet.

## 6. Independent code review

Severity scale: **High** means it can stop music or lose data in normal use. **Medium** means it needs a fault or a hostile local user. **Low** means robustness or clarity. Items marked *speculative* are plausible from the code but weren't reproduced.

| ID | Severity | Location | Finding |
|---|---|---|---|
| R-1 | Medium | `Server/Playback/PlaybackPersistence.cs:143-159` | Failed saves are dropped. This is BUG-01. |
| R-2 | Medium | `Audio/Engine/PlaybackEngine.cs:508-590` | A user-requested unplayable item stops audible playback. This is BUG-02. |
| R-3 | Medium | `Server/Pipe/PipeRpcServer.cs:163, 195-251` | No idle or handshake timeout and no per-caller cap, so the 32 slots can be squatted. This is BUG-03. There is also no server-side limit on concurrent in-flight requests per connection. |
| R-4 | Medium | `Server/Application/AuthService.cs:195-207`, `Server/Api/ApiSession.cs:261-273` | Every call re-validates the session, and once per `SessionTouchInterval` (1 min) per session it **writes** to the DB (`TouchAsync`). While the DB is locked or the disk is full, the first playback command a client sends each minute (Pause, Next…) blocks for up to about 30 s and then fails with `Internal`, although the engine itself never needs the DB. This follows from the code and the 30 s busy behaviour measured for R-6; the playback-command case itself was not reproduced. **Suggested fix:** make the touch best-effort: catch, log and continue, or fire-and-forget it. |
| R-5 | Medium | `Server/Playback/AnalysisWorker.cs:129-131, 231-242` | `TrySave` only handles `NotFound`. A busy or full DB during the `Ready` save makes the job fail with a logged error. The track stays **Analyzing** in the DB (clients show it as analysing) until the next service start resets it, and nothing retries. **Suggested fix:** retry with backoff, or re-enqueue with a delay. |
| R-6 | Low | `Data/SqliteDatabase.cs:13, 36` | The documented "5 s busy timeout" is not the effective limit. `DefaultTimeout = 30` makes Microsoft.Data.Sqlite keep retrying a busy statement for up to 30 s (measured: a 7 s exclusive lock was waited out, and a failure needed more than 30 s). Consequences: client calls can hang for 30 s, and `SabhaHost.StopAsync`'s state flush (`Hosting/SabhaHost.cs:180`) can take 30 s or more per statement, close to the Windows SCM stop timeout. **Suggested fix:** pick one deliberate limit (for example a 5–10 s command timeout) and document it. |
| R-7 | Low | `Server/Api/ApiSession.cs:268-273` | Transient environmental failures, such as `SQLITE_BUSY`, `SQLITE_FULL` and `IOException` ENOSPC on upload chunks, all become `Internal` with "An internal error occurred". The client can't tell the admin "disk full" or "try again". **Suggested fix:** map these to `Unavailable` or `Conflict` with a safe message (no paths). |
| R-8 | Low | `Server/Application/ImportService.cs:118` | `UpsertAsync(Pending)` runs after `AddAsync`. If it fails, the import reports **Failed**, yet the track and its file stay in the library, unqueued for analysis. A retry then says "duplicate". The engine still analyses on first play and the next startup re-queues it, so there is no data loss. **Suggested fix:** do the add and the pending-analysis insert in one transaction, or treat the upsert as best-effort after the add. |
| R-9 | Low | `Server/Playback/EnginePlaybackBackend.cs:157-183` | Deleting the playing track sets the crossfade to 0 and later restores the old value. A client `SetCrossfade` in between is overwritten (lost update). Clients briefly see `CrossfadeSeconds = 0` pushed, and a crash in that window could persist 0. **Suggested fix:** an engine command that removes items with a hard cut, without touching the user setting. |
| R-10 | Low | `Data/Storage/UploadStaging.cs:263` | `CancelAllForOwner` is never called. A session that logs out or disconnects mid-upload keeps its partial file and its slot (2 per session, 8 global) until the 30 min idle expiry. The desktop app cancels on failure (`ImportViewModel.cs:362`), but a crashed app does not. **Suggested fix:** call it on logout and on session revocation. This is relevant to the logout-semantics work in progress. |
| R-11 | Low (*speculative*) | `Audio/Mixing/CrossfadeMixer.cs:28, 568-579` | The render-to-control event ring holds 1024 events and silently drops on overflow. A dropped `VoiceReleased` leaks a voice (file handle, 0.4 MB, a feeder slot). A dropped `VoiceEnded` leaves the engine "Playing" a voice the mixer no longer has (a stall). It only happens if the control thread is blocked long enough for 1024 events to pile up; `DroppedEvents` stayed 0 in every run. **Suggested fix:** never drop `VoiceReleased`/`VoiceEnded` (a separate unbounded or larger queue for them), or have the engine reconcile `_liveVoices` against the mixer when `DroppedEvents` changes. |
| R-12 | Low (*speculative*) | `Audio/Engine/PlaybackEngine.cs:381, 399-405` | `Dispose` waits 10 s for the control thread, then iterates and disposes `_liveVoices` from the calling thread even if the control thread is still running. That is a race on a non-thread-safe `HashSet` and on voices in use. The control thread can be blocked that long by `WaitForBuffered` (5 s) plus a resolver DB read. **Suggested fix:** skip the voice cleanup, or log it, when `Join` times out. |
| R-13 | Low | `Audio/Engine/PlaybackEngine.cs:953` | The result of `WaitForBuffered` is ignored. On slow storage (a spun-down disk or network share) a voice is handed to the mixer with less than the pre-buffer, and the start of the track underruns into silence. **Suggested fix:** log it, and count it in a diagnostic counter. |
| R-14 | Low | `Audio/Engine/PlaybackEngine.cs:965, 977-1004` | The control thread does synchronous DB reads on resolver cache misses, which is fine in WAL. A track that is still Pending in the `AnalysisWorker` is analysed a second time by the engine on first play (duplicate CPU work). `_analysisFailed` is never cleared, so after an admin "Reanalyze" the engine still won't analyse that track itself until restart (the `AnalysisWorker` does, so the impact is small). |
| R-15 | Info | `Server/Application/EventBroadcaster.cs` | Reviewed and **no issue found**. Per-subscriber queues are bounded (256) with snapshot coalescing, `SessionEnded` is never dropped, and a 10 s delivery timeout drops stalled subscribers. The stress test confirmed subscribers return to 0 and that there is one subscription per connection. A minor point: `Enqueue` scans the pending list linearly for coalescing, which is fine at 256. |
| R-16 | Info | Engine threading, overall | The single-control-thread design held up under 20 concurrent clients and 8 simulated hours of churn: no deadlock, no stuck Loading or Crossfading, no position regressions, strictly increasing revisions and zero underruns at 40×. |

## 7. Not tested, and why

- **Real WASAPI output, device unplug or replug, and audible quality:** there is no audio device in the cloud. Device loss was only simulated through `NullRealtimeOutput`.
- **Windows named pipes, the pipe DACL, the service host and SCM stop timeouts:** these are Linux-only runs over Unix domain sockets. BUG-03 behaviour on Windows is unverified.
- **Media Foundation formats (MP3, M4A, WMA, FLAC):** only the WAV decoder exists on Linux.
- **Real-time 8-hour soak, and soak without other load:** the soak is accelerated (40×). Real-time-only effects aren't covered: timers relative to audio time (heartbeat, debounce, session touch are wall-clock and run 40× less often per simulated hour) and WASAPI buffer behaviour. A 24-hour real-time run on Windows is recommended before release.
- **Disk full on Windows, or without root:** the disk-full test needs a mountable tmpfs, so it is skipped on GitHub runners and Windows.
- **The remote under the restart in the real service wiring:** the soak hosts `RemoteHost` through its own `IHostedComponent`, because integrating the remote into the service is in progress elsewhere. Phones were reconnected after the restart, not left to the web app's own reconnect logic.
- **iPhone Safari:** not applicable here.
- **More than 30 or so concurrent pipe clients:** there is a hard cap of 32 (see BUG-03).
