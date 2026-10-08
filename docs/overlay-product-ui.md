# Product CURRENT overlay

The normal executable now opens the WPF CURRENT overlay. On first use, Settings opens to select an available local Ethernet/Wi-Fi adapter and start the meter. A previously selected, still-available adapter starts automatically on subsequent launches. Missing adapters do not fall back to another adapter. Waiting for a fresh world connection remains an identity requirement.

## Controls

- Header: reset CURRENT, Settings, hide, and close/stop/exit. Closing the overlay stops/disposes the live session, unregisters hotkeys, saves preferences and exits.
- The system tray remains available while the overlay is hidden. Left-click toggles Show/Hide. Right-click exposes Show/Hide Overlay, Reset Current, Settings and Exit Aion2Meter. The tray, header, Settings and hotkeys share the same commands; showing/hiding never restarts capture. The tray has one owner and is disposed on orderly exit.
- Default global shortcuts: **Ctrl+Shift+H** toggles visibility; **Ctrl+Shift+R** resets CURRENT. Change them in Settings and press Apply shortcuts. RegisterHotKey uses MOD_NOREPEAT. Failed/colliding registrations are reported in Settings; tray Show/Hide and Settings remain usable without any registered hotkey. Done/closing Settings reveals the overlay.
- Reset runs on the serialized capture/snapshot owner. It clears encounter totals, recent amounts, participants and the shared encounter clock. Self binding, validated roster, cumulative diagnostic counters, source, publication ordering and deduplication remain intact. Buffered events whose completion predates the reset timestamp cannot refill CURRENT. Validated active members remain as zero-damage rows; frozen departed members disappear on reset.
- Drag the header when unlocked. Drag the bottom-right grip to resize. Width and a minimum height persist; additional validated rows grow the window naturally, with scrolling when screen space runs out. Lock disables both dragging and resizing.
- Opacity/scale preview immediately. Settings persist locally at `%LOCALAPPDATA%\Aion2Meter\overlay-settings.json`: adapter identifier, opacity, scale, lock, position, width, minimum height, two shortcuts and the optional SelfClassOverride preference. Older settings acquire safe defaults. Identity and combat data are not saved in preferences.
- Player accents derive deterministically from the validated display name; Self has a separate mint accent. Colors convey UI distinction only, not class/skill/identity evidence. Ranking and contribution come from the existing snapshot.
- Compact participant rows show optional class icon, rank/name/YOU, bold primary total damage labeled TOTAL, secondary DPS with its own label, contribution %, and a 40-logical-pixel contribution fill behind the text. K/M/B/T formatting changes presentation only; the backend retains full precision. Unknown class hides the icon. User-provided development PNGs are packaged, cached and replaceable; no name/skill/damage-based class guess is made.
- **My Class** offers Automatic / Unknown and all eight existing classes. A known authoritative row class has precedence; otherwise an explicit preference supplies only the Self icon. The live snapshot now carries scoped direct-profile class evidence for supported 4536/3336 layouts; unsupported or conflicting evidence stays Unknown. Remote Unknown stays iconless. Changing the preference immediately reprojects the cached immutable snapshot and reuses the same row/icon controls, without restarting capture or resetting CURRENT. Clearing the preference returns to automatic/Unknown. The icon tooltip identifies a manual display preference; it is never written into actor, protocol, capture or replay data.
- Advanced/research opens the preserved MainWindow. Entering Advanced stops the product meter; the normal meter cannot restart while Advanced is open. Advanced's legacy live controls are disabled in this flow to prevent a second live owner. Research capture controls remain available. Closing Advanced returns to Settings; restart the meter explicitly.

## Approximate network RTT

The display is passive outbound-data to inbound-ACK timing on exactly one selected, resolved, active game TCP epoch. It is independent of transport reconstruction, decoding and combat accounting. Three samples are required, with 1/8 EWMA smoothing, integer milliseconds, a ten-second stale fallback and at most 128 outstanding endpoints. Samples older than five seconds and nonpositive measurements are excluded. Cumulative ACKs produce one sample, duplicate ACKs produce none. Retransmission/overlap clears the ambiguous outstanding flight and waits for the barrier ACK; new epochs, ambiguous selection and FIN/RST clear samples. IPv4/IPv6 and sequence wraparound are covered by tests.

This includes delayed ACK and server scheduling, and is **not authoritative game latency**. Insufficient/expired evidence renders **— ms**. Approximation is labeled **≈** and explained in the tooltip.

Signal presentation uses the already-smoothed value: ≤70 ms green/four bars, >70–120 ms yellow/three, >120–180 ms red/two, >180 ms red/one; unavailable/stale gray/zero. No RTT or signal value affects accounting.

Primary references: [Win32 RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey), [RFC 6298 §3: Karn's algorithm](https://www.rfc-editor.org/rfc/rfc6298.html#section-3). No external gameplay decoder or GPL source was used.

## Preserved scope

Self/validated party CURRENT, the 30-second inactivity policy, join/leave/rejoin/disband/reconnect eligibility, aggregate accounting, descending DPS and the Self YOU badge retain their existing behavior. Partial coverage and unsupported 0x36 remain. Boss/HP/HPS/Overall/skill databases, new roster grammar and click-through are deferred; no placeholder gameplay data is displayed.

## Manual live checklist

Automated STA WPF layout/render checks and backend/replay checks do not substitute for a fresh interactive game test. The following remain to be performed with the new executable:

- [ ] Solo: launch, select adapter/start, enter world and resolve Self. Choose My Class and confirm its icon plus YOU; switching to Automatic / Unknown removes the icon when no authoritative class exists. Attack and verify prominent TOTAL, visible DPS, elapsed and 100% fill.
- [ ] Reset: use the header then Ctrl+Shift+R with damage present; CURRENT becomes empty/ready, capture stays running, and subsequent eligible hits start a clean encounter.
- [ ] Hide/show: use header Hide, restore by tray left-click, then tray right-click Show/Hide. Open tray Settings while hidden. Ctrl+Shift+H still restores the same live session. If a shortcut conflicts, tray recovery remains available.
- [ ] Resize/lock: drag the bottom-right corner narrow/wide and short/tall, preview 75%/150% scale, verify no clipping, then lock and confirm no drag/resize. Check each monitor/DPI configuration in use.
- [ ] Party: invite/accept; only validated members appear. Both attack. Verify descending ranking, YOU remains on Self and contribution uses group total.
- [ ] Leave/disband: remote subsequent hits do not increase CURRENT; frozen contribution follows the existing encounter behavior; next encounter/manual reset removes stale departed rows.
- [ ] Reconnect: require fresh Self binding and validated roster recovery. Unrelated nearby players must not become rows.
- [ ] RTT: see integer approximate ms only after sufficient outbound-data/ACK samples; inspect tooltip; idle/insufficient samples show — ms.
- [ ] States: Waiting, Ready, In combat, Last encounter, Data unavailable/untrusted and Meter stopped remain readable without fabricated metrics.
- [ ] Persistence: alter My Class, opacity, scale, lock, hotkeys, position and size; close/relaunch and verify restoration. Missing remembered adapter asks for selection.
- [ ] Dungeon/teleport: with meter running and Advanced closed, enter a solo dungeon, fight, return normally, then use a normal world teleport/loading and fight again. Same-ID/name/layout complete ACKed 3336 refresh remains strictly required; no restart/server selection during transitions.
- [ ] Lifecycle: repeated stop/close, hide while running, exit while settings is open, and Advanced research transitions leave no capture session or hotkey registration behind.

## Membership and runtime association

A completely consumed supported 0092 roster with unique IDs/UUIDs/tokens, matching the independently bound Self, establishes a bounded authoritative membership list. Membership is distinct from combat eligibility. A far member has a zero TOTAL, unavailable DPS and null runtime EntityId until independent same-epoch 4536 evidence agrees on both exact name and the roster's numeric ID candidate. No name-only lookup, nearby actor, target/class/skill heuristic or retrospective damage assignment is used. The row keeps its epoch-scoped membership key when binding arrives; frozen counted rows retain that key. Unsupported roster forms, conflicts, termination and new epochs withdraw membership. A supported complete join plus matching invite can now establish named zero-damage membership before runtime identity. The independently supported 1B92 status path is documented in [live-party-profile.md](live-party-profile.md). Membership survives checkpoints, reset and the attested same-epoch 3336 refresh.

Party diagnostics retain at most eight transition summaries, two pending replacement entries, five memberships/invites, 16 closed intervals and 4096 independently observed identities. Diagnostics do not grant eligibility.

## Single product process

Startup acquires a Windows-session mutex before constructing product windows, tray, settings or capture. A second cooperating build is rejected. A bounded display of existing legacy process IDs/paths also blocks starting alongside old builds without the mutex. Exit those builds normally before switching executable folders. The guard cannot control an old, unguarded executable launched later; do not run old builds alongside this one. Advanced handlers cannot start the legacy live overlay even if directly invoked. All normal visibility commands retain the same OverlayWindow, ViewModel, session, size, position and encounter.

## Validation in this workspace

The latest Release locked restore/build/test is under ignored `.tools/tmp/live-party-row-final/`: 879 passing tests (825 backend/presentation + 54 WPF), zero build warnings/errors. Root `publish/` and original captures are untouched. Actual WPF renders cover 0/10/50/100% fill, solo/two/four/many rows and 75/100/150% scale. The row uses a dark rounded track and a 52% opacity accent fill; TOTAL is 15px bold, DPS 12px semibold and contribution 10px. Tall rows remain fixed-height at the top and overflow scrolls.

The new party-far-return-01 capture independently confirms a complete 0092 roster before Self binding and delayed Mystogan 4536 at proximity, with a same-epoch teleport refresh between them. Replay of the current pre-change resolver already admitted the later 18 supported hits / 64604 amount; the new model additionally displays authoritative zero-damage membership while runtime association is pending. Thus the original sustained omission after proximity was not reproduced by this capture. Two old product executables were concurrently running during investigation; the new guard prevents that launch condition. Manual real-game smoke with only the new executable is still required before staging, commit or push.

Historical reports/builds under `.tools/tmp/overlay-row-polish/` remain intact. The class audit compared current MIT prior art with 43 physical captures / 45 selected game flows. Yaaz Cleric and CHAD Gladiator corroborate two structural codes, but do not independently validate every class/layout; That historical baseline preceded the broader direct-profile audit documented in [live-party-profile.md](live-party-profile.md). My Class remains Self-only, and unsupported remote layouts remain iconless. 06/26 decoding, unsupported 0x36, Partial coverage, CURRENT/shared clock/reset/RTT and attested refresh behavior remain unchanged.
