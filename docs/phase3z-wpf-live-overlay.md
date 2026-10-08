# Phase 3Z — WPF live DPS overlay MVP

The normal WPF application now provides a compact, original Self-only overlay. The backend source,
transport, decoder, identity resolver, DamageEvent, association and encounter accounting are unchanged.
Category 0x36 remains Unsupported; supported 06/26 coverage remains partial. No HPS, party resolver,
Overall accounting, game-window tracking, memory inspection, hooks or input automation were added.

## Integration and threading

```text
Existing AdapterDiscovery / adapter ComboBox
  → remembered local adapter identifier
  → LiveOverlaySession background Task
  → existing NpcapLiveSource
  → existing LivePacketPipeline + LiveCombatFeed + LiveCombatMeter
  → immutable OverlaySnapshot, one latest reference
  → WPF DispatcherTimer, 200 ms (5 Hz)
  → UI-owned OverlayViewModel / reusable row template
```

`LiveOverlaySession` owns one instance of the existing stack per start. `LiveDiagnosticRunner`
serializes packet ingestion and periodic backend snapshots away from the WPF synchronization context.
Only compact strings/numbers/read-only rows cross the boundary. No packets, endpoint objects,
TCP structures, mutable actor dictionaries or session-history scans reach XAML.

UI reads the latest snapshot; capture does not enqueue Dispatcher callbacks or wait for UI. A stalled
UI can skip intermediate presentations without changing publication or accounting. Stable row keys
include epoch + entity identity. Ordinary refreshes update existing row properties only when values
change, preserving actual row controls. Collections are empty or one row in the production projector.
The template supports name, ME flag, rank, DPS, damage, contribution and bar for later eligible rows.

Background source failure publishes Data unavailable and removes stale rows. Stop cancels and awaits
the runner and the source iterator's native cleanup. Repeated Stop/Dispose are safe. Overlay close
stops its timer, yields beyond WPF's current Closing event, awaits stop, then closes. Main-window close
awaits in-progress startup/research work, stops live capture, closes the overlay, flushes preferences,
and disposes the existing capture engine. Live overlay and research file capture are mutually exclusive.
Console `research live-meter` continues using the same meter semantics and passes existing regressions.

## Display contract

| Backend state | Normal display |
| --- | --- |
| Unknown / midstream attach | Waiting for character identity...; Will recover on the next fresh world connection. No row or DPS. |
| Resolved, no Self hit | Authoritative name + ME; Ready, 0 damage, undefined DPS shown as —. |
| In combat | One Self row; backend TotalDamage, DPS, first-to-last completed Self hit duration, 100% contribution. First hit has no invented rate. |
| Idle after 30 s without Self activity | Last encounter; preserve existing totals/rate/duration until a new Self hit starts the next encounter. |
| Fresh reconnect | Old row disappears while identity is Unknown; a resolved new epoch gets a new row and cleared current totals. |
| Untrusted / binding conflict / ambiguous scopes / source failure | Data unavailable; no stale name/DPS row. |
| Stopped | Meter stopped; no row. |

Duration and DPS come from `LiveCombatMeter`; duration is not an independent wall-clock counter.
Aggregate DamageEvent Amount already includes optional components; the overlay adds no components
or gameplay calculations. Other and Unknown can continue decoding internally; they never supply UI
rows or contribute to Self totals. Solo contribution is always 100% for the one eligible row.

Coverage: Partial is a small footer label. Its tooltip uses the backend coverage string, currently
06/26 supported and 0x36 pending. Future supported events automatically reach existing accounting
and presentation. No protocol tags appear in normal waiting/combat status or hint text.

## Window, adapter and preferences

- Original 420-DIP dark translucent panel, approximately 151 DIP tall in combat, 137 DIP while waiting.
  Header: Aion2Meter / CURRENT / lock / Settings / close. Name + ME on the left, prominent DPS on the
  right; damage/contribution below; thin bar; time/status/coverage footer. Built-in WPF only, no UI packages.
- Borderless, Topmost, no overlay taskbar entry, ShowActivated=false. Normal updates and placement
  never call Activate. Explicit Settings opens/activates the normal application. The launcher retains
  its normal taskbar entry; minimizing it leaves the overlay visible.
- Drag from the header while unlocked. Header buttons are excluded from dragging. Locked prevents
  dragging; lock/unlock remains clickable. Click-through is intentionally absent in this MVP.
- Preferences include stable adapter identifier, lock, opacity (0.5–1), scale (0.75–1.5), physical-pixel
  position and monitor device name. Runtime name/entity/binding are never saved. Preferences live at
  `%LOCALAPPDATA%\Aion2Meter\overlay-settings.json`, outside capture metadata.
- Existing adapter discovery/ComboBox is reused. First use selects manually. A remembered identifier
  is reused only if present, local and carrying IP addresses; missing/remote/no-address selections
  leave no arbitrary replacement selected. Interface indices, character IDs/names and historical IPs
  are not embedded in the implementation. Existing configurable service-port default remains 13328.
- Preferences load off the UI thread. Debounced, serialized background writes use an adjacent unique
  temporary file and atomic replacement; errors are logged, previous complete settings are preserved
  on replacement failure, and orderly exit flushes current preferences. Abrupt termination can lose
  a preference changed within the debounce interval; this does not affect combat accounting.
- PerMonitorV2 manifest, WPF layout scaling and native OS monitor work areas. Restore clamps to the
  remembered available monitor or an available fallback, including negative coordinates. DPI, display
  and overlay-size changes re-clamp. Native APIs operate only on our overlay, never on game windows.
  If a monitor is smaller than the scaled panel, the header remains reachable at the work-area origin.
- No expensive effects, continuously moving decorations, per-packet UI/logging, repeated full-session
  computation or visual-tree rebuild per refresh. Existing bounded pipeline/checkpoint limits remain.

Current is the only view. Separate snapshot projection and reusable collection/template leave room
for Overall and additional validated rows later; their accounting/membership are not implemented.
The remaining party/group blocker is a validated current roster/membership resolver with join/leave,
scope and identity handling. Decoding Other or observing an identity is not proof of group eligibility.

## External source / bug audit (2026-10-08)

Public code was inspected only to understand behaviors and failures; no GPL/no-license source,
assets, hooks, game tracking or layout was copied. All Aion2Meter additions are independently written.

| Primary reference | Observation and local response |
| --- | --- |
| [A2Tools PR 29](https://github.com/taengu/A2Tools-DPS-Meter/pull/29) | UI stalls and competing snapshot requests; separate computation/IO from UI and coalesce latest work. We use one serial backend worker and a single latest snapshot, with no per-packet Dispatcher work. |
| [A2Tools PR 38](https://github.com/taengu/A2Tools-DPS-Meter/pull/38) | Settings-save work must not block UI; stale hover summaries need explicit refresh/invalidation. Our writes run in background and reconnect/fault removes old presentation scope. No hover computation or large settings product. |
| [A2Tools v2.0.53](https://github.com/taengu/A2Tools-DPS-Meter/releases/tag/v2.0.53) | Notes report unnamed Self rows merging/disappearing, incorrect time range and animation CPU cost. We require resolved identity, retain epoch keys and backend encounter metrics, and avoid continuous animation. Its mid-session identity heuristics were not adopted. |
| [TK issue 48 and comments](https://github.com/TK-open-public/Aion2-Dps-Meter/issues/48) | Saved position below the taskbar hid the meter; resetting X/Y restored it. Our saved positions clamp to current monitor work areas and recover when a monitor disappears. |
| [a2meter issue 6](https://github.com/a2meter/Aion2Meter/issues/6), [current OverlayForm](https://github.com/a2meter/Aion2Meter/blob/master/src/A2Meter/Forms/OverlayForm.cs) | Disposed overlay/native-handle crash during mouse handling; current source guards disposed/handle states and uses non-activation. Our timer/subscription teardown, close guards and awaited cancellation cover lifecycle; no native mouse-message hook. |
| [Kuroukihime PR 51](https://github.com/Kuroukihime/AIon2-Dps-Meter/pull/51), [BuffOverlayWindow](https://github.com/Kuroukihime/AIon2-Dps-Meter/blob/master/AionDpsMeter.UI/Views/BuffOverlayWindow.xaml.cs), [NativeWindowHelper](https://github.com/Kuroukihime/AIon2-Dps-Meter/blob/master/AionDpsMeter.UI/Services/Windowing/NativeWindowHelper.cs), [WindowHelper](https://github.com/Kuroukihime/AIon2-Dps-Meter/blob/master/AionDpsMeter.UI/Services/Windowing/WindowHelper.cs) | PR describes refresh/retention cost and stale edit-mode input/listener failures. Current source uses a WebView wrapper and native click-through helpers. We keep lightweight WPF and persistent row controls, an accessible lock button, and no click-through, game anchoring or global hooks. The PR is closed, not represented as a merged upstream implementation. |

## Verification

Baseline: 660 tests. Added 28 UI-neutral cases and 8 Windows STA WPF cases: **696 passed,
0 failed, 0 skipped**. Full restore with locked package resolution and full build: **0 warnings, 0 errors**.
No new runtime UI dependency; the existing LZ4 dependency now reaches the application through Replay.

UI-neutral tests cover actual pipeline-to-meter projection, Unknown/Ready/combat/idle/reset,
300 internally decoded Other actors and Unknown history, exact damage/rate/duration mapping,
100% contribution, immutable generated rows, reconnect identity replacement, transport fault,
ambiguous/conflict states, 0x36 Unsupported, stable row/property notifications, future row ordering,
identifier selection/fallback, preference round trip/no persisted identity/concurrent writes,
corrupt/oversized/nonfinite preference recovery, negative-coordinate/removed-monitor/off-screen geometry,
background synchronization-context isolation, a 500-hit burst with no per-packet presentation,
200 ms periodic snapshots, source failure withdrawal and concurrent stop/disposal.

WPF tests instantiate and lay out the real XAML on STA threads for Waiting/Ready/combat/unavailable,
detect binding errors, assert one-way bar mapping/readable name styling, reuse actual row controls,
remove controls on waiting, apply scale/opacity, unlock and wait for stop exactly once on repeated close.
Closing the launcher before preference loading also preserves existing user settings.
Render QA caught and fixed the default TwoWay RangeBase binding to a read-only ViewModel property,
and the initial dark inherited row text. Neither issue is hidden by a mocked XAML test.

Existing backend, bounded-session, recovery, console output and Unsupported-category regressions pass.
`research live-meter --help` also exits successfully. Protocol/capture/accounting source files are untouched.

Actual WPF content was rendered headlessly at 100% and 150% scale for synthetic combat, waiting
and unavailable views with no binding diagnostics. Local PNGs and the preview harness are ignored under
`.tools/tmp/phase3z/`. Preview values/name are explicitly synthetic, not a live capture or ground truth.
Live game/Npcap overlay use, physical mixed-DPI monitor dragging and fullscreen composition need the
manual check below; automated template/geometry tests are not evidence that those native scenarios ran.
Existing untracked `publish/` is preserved and is not this new build.

## Manual smoke — normal mobs, no dummy

From `C:\Projects\Aion2Meter`, after restore/build:

```powershell
.\src\Aion2Meter.App\bin\Debug\net10.0-windows\Aion2Meter.App.exe
```

1. Select the active Ethernet/Wi-Fi adapter. Use administrator only if the installed Npcap access policy
   requires it. On later launches the matching identifier should already be selected; if unavailable,
   select another adapter. The older `publish/` copy has not been refreshed.
2. Click **Start Live Overlay**. Minimize the launcher if desired. Research CLI and process selection
   are unnecessary for the live-overlay workflow; Start Capture is an independent research tool.
3. If identity is waiting because the client was already in-world, enter/reconnect the world normally.
   After authoritative initialization, expect the current character's real name / ME and Ready / 0 damage.
   No guessed name, fake rate or restart-game instruction should appear.
4. Kill a normal mob. Verify Self damage, DPS (after distinct completed-hit times), current encounter
   duration and 100% contribution. Nearby actors must not add rows or alter Self damage.
5. Kill another mob before the 30-second Self inactivity timeout; values should extend the same encounter.
   Wait at least 30 seconds without Self activity: Last encounter preserves the finished display.
   A subsequent Self hit starts a new current encounter, leaving only the Self row.
6. Drag the header. Lock prevents drag; unlock remains accessible. Use Settings to adjust opacity/scale.
   Move across monitors if available; reopen the application to check saved position/adapter/lock/style.
   Display-topology/DPI changes should leave the header accessible.
7. If the connection/capture becomes untrusted, expect Data unavailable and no stale DPS. Restart live
   capture when appropriate; Unknown midstream will wait for the next fresh connection.
8. **Stop Live Meter** stops capture. Overlay **×** stops capture and closes the overlay. Close the
   normal application to stop/close everything and flush preferences; verify no capture remains.

## Changed files

- `src/Aion2Meter.Presentation/`: project, `OverlaySnapshot`, `OverlayViewModel`, `LiveOverlaySession`,
  `OverlaySettings`, `OverlayPlacement`, package lock file.
- `src/Aion2Meter.App/`: `OverlayWindow.xaml/.cs`, `NativeOverlayPlacement.cs`, `MainWindowLive.cs`,
  `MainWindow.xaml/.cs`, `app.manifest`, project reference and package lock file.
- `tests/Aion2Meter.Tests/OverlayTests.cs`, project reference and package lock file.
- `tests/Aion2Meter.App.Tests/`: project, `OverlayWindowTests.cs`, package lock file.
- `Aion2Meter.sln`, `README.md`, this report.

Only these implementation/tests/docs/project files are staged. Capture files, original metadata,
ignored research output, binaries and the pre-existing `publish/` directory are excluded.
The target branch is `phase-3-protocol-research`; main is not merged or updated.
