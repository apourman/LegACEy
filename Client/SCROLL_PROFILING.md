# Temporary ticket 07 scrolling capture

This build measures the reported 500-row Performance list stutter. It changes diagnostics only; it does not move Avalonia to another thread or optimize scrolling.

## Live comparison

Close the game, pull the local `client-avalonia-ui` branch and rebuild the DecalPlugin through the usual Windows workflow. Open the Performance list with the P slot. Capture starts automatically while this window is open, with no configuration file or extra commands.

Keep the same scene, resolution, plugins and number of clients. Let the list settle for five seconds, then run these stages in order, about 30 seconds each:

1. Stand still with the list open, without scrolling.
2. Stand still while scrolling up and down.
3. Run with the list open, without scrolling.
4. Run while scrolling up and down.

Note each stage's approximate start time and whether world movement, list scrolling or both stutter. Close the list after the last stage; this flushes the final partial interval and stops capture. Avoid changing other windows or settings during the comparison. No restart is required to flush the file. If restarting, retain each process's file.

Logs are beside the plugin DLL: `Client/LegACEy.Client.DecalPlugin/bin/Debug/net48/scroll-profile-<pid>-<UTC-start>.log` in the normal checkout. Each process has a separate file. The agent inspects these read-only; the registered plugin is rebuilt by the user. Only this comparison's files and stage notes are needed, not unrelated chat logs.

## Interpreting the capture

Every line carries `[DEBUG-scroll07]`, UTC time and process ID. Timing rows include managed thread ID, surface ID, stage, count, total/average/maximum milliseconds, and counts at or above 8 and 16 ms. These thresholds are diagnostic counters, not acceptance budgets. Summaries aggregate approximately one second of activity, retaining short peaks rather than taking a single frame sample every 30 seconds. Disk writes run on a background thread with a bounded queue. Dropped snapshots/metric keys are reported; a writer failure is reported once in `legacey-avalonia.log`.

- `wheel-callback` includes the complete Decal wheel callback, including routing; `wheel-input` measures the panel's synchronous wheel input. They overlap and must not be added together.
- `panel-tick` includes the whole panel tick. Its narrower stages are `dispatcher-before`, `render-timer`, `dispatcher-after`, `capture-lock`, `pixel-diff-copy`, and `resource-observation`. `resume` reports additional work when a hidden panel resumes. Stage counts can differ on idle or exceptional ticks. Idle ticks do not reuse old pixel/capture measurements.
- `texture-upload` and `draw` measure the corresponding Direct3D operations. `hidden-dispatcher` measures queued work for a hidden surface.
- `render-callback` measures the plugin's pre-UI callback, including the existing periodic logger. `periodic-log` identifies that logger's contribution. `diagnostic-flush` measures formatting/queuing the preceding interval's summary.
- `render-callback-gap` measures spacing between consecutive callback entries. It helps correlate stalls but is **not** a full client CPU/GPU frame measurement; it also includes engine work, scheduling, frame limiting and other plugins. Do not attribute a gap to LegACEy without correlating its input/tick/upload timings.

Avalonia's dispatcher and render timer are shared by all panels. A surface label identifies the call that pumped them, not exclusive ownership of all work performed there. Nested measurements overlap; maxima from separate stages may occur in different frames. Profiling adds some overhead, so compare like-for-like captures and verify any eventual fix again with diagnostics removed.

## Repeatable panel-only capture

`ScrollProfileTests.Repeated_themed_scrolling_can_produce_a_stage_capture_at_game_panel_size` drives the actual 500-row list with chrome/theme at 620×460, warms it up, measures 60 idle ticks, and sends 120 wheel events with ticks. `LEGACEY_PORTAL_DAT` supplies real art; optional `LEGACEY_SCROLL_PROFILE_OUTPUT` selects the report path. Run with the test filter `FullyQualifiedName~Repeated_themed_scrolling`.

This loop verifies stage capture and panel-level cost. It does not reproduce running inside Decal, measure multibox gameplay or assert a performance budget. The live four-stage comparison remains required.

All `[DEBUG-scroll07]` code and the diagnostic observers are temporary. Remove them after a cause and fix are verified, retaining useful regression coverage and the recorded results.
