# Phase 3X: incremental live combat feed and console meter

`research live-meter` adds bounded, exactly-once publication and personal encounter accounting to the
shared Phase 3W pipeline. It does not add another parser, DamageEvent class, ownership heuristic,
party filter, HPS, overlay or 0x36 grammar. Existing `live-smoke` remains a finite diagnostic command.

## Publication and stabilization

The same TCP reader, reassembler, CandidateBlockExtractor/ApplicationFraming, LZ4 decoder, protocol
decoder, DamageEvent projector, initialization resolver and association analyzer are reused. The
meter pipeline first reconstructs the observed transport. For each direction, it selects the prefix
that starts at the fresh ISN+1, is contiguous, is cumulatively acknowledged by the peer, and ends on
a complete frame boundary found by the existing extractor. The shared decoder processes this
prefix. There is no resynchronization, speculative partial-record emission, new opcode scan or timer
delay. Publication follows the next 500 ms diagnostic tick after completion and peer ACK. Missing
ACK/capture data holds publication; it never becomes permission to guess a frame or identity.

The boundary intentionally clips selected packet payloads in a derived in-memory research capture;
original source packets, PCAP and metadata are not changed. Packet arrival/completion evidence is
preserved for all included frame bytes. Trailing partial frames and a later gap cannot revoke the
already included prefix. Conflict checking still covers ALL retained transport bytes, including
retransmissions outside the current publication prefix. Nondecreasing capture timestamps prevent a
later packet from retroactively winning the shared reassembler's first-arrival provenance.

`LiveCombatEvent` references the existing immutable DamageEvent and association, with epoch ID,
publication sequence, source timestamp and publication identity. The epoch's source UUID/runtime
serial, endpoints and handshake evidence isolate publication and binding. The sequence increases
over the capture session; an event location is admitted at most once in its epoch.

The existing finite `DamageEvent.Identity` is preserved on the published object. It includes decode
record/container ordinals. Those ordinals can shift when later outbound frames are added, because
the finite decoder visits outbound before inbound. Live dedup therefore uses the event's stable
direction, outer stream offset and full container inner-offset path, scoped by epoch. A fingerprint
reuses DamageEventProvenance's versioned binary/hash identity encoding with ONLY decode ordinals
normalized, plus immutable projected values. Amount or timestamp alone never identifies an event.
Identical amounts at different locations are distinct. A repeated location with changed provenance,
content or association fails closed; it is not silently recounted or retrospectively reattributed.

Unknown is published as Unknown, without later promotion to Self. Events before the confirmed
binding cannot become personal damage. A genuinely new epoch has a new publication scope and must
independently resolve identity, even if endpoints, ISNs, stream locations or character IDs recur.

## Late contradictions and authority

Clean acknowledged complete prefixes are monotonic under ordinary append, duplicate, retransmission
and out-of-order completion, as tested. An ACK is transport evidence, not a guarantee that a future
capture cannot contain contradictory bytes or a second initialization assignment. No finite capture
can prove the absence of every future contradiction.

Published events are never rewritten, withdrawn or emitted as negative corrections. Instead, a late
TCP conflict, binding loss/conflict, changed/missing committed provenance, capture loss/backward time,
or resource limit invalidates the ENTIRE publication epoch. Publication stops, its dedup/raw state is
released, and the meter clears its damage/encounter, displays **UNTRUSTED - reconnect required**, and
reports null DPS. Already delivered events are historical observations and are no longer an
authoritative total. A consumer of the feed must handle `EpochObserved` fault status and `EpochEnded`,
as this console meter does; it must not keep an unconditional lifetime sum after invalidation.

## Accounting and encounter definition

- Only published `association.Status == Self` contributes. Direct-source equality is the existing
  validated association, not a guessed player/summon relationship. Other/Unknown count diagnostics.
- TotalDamage sums `DamageEvent.Amount` once. OptionalComponents are already included and are NEVER
  added a second time. This is encounter-scoped damage, not the finite snapshot's SelfCount sum.
- First Self event starts an encounter. Another Self event within the timeout extends it. At a gap
  **greater than or equal to** the timeout, the old encounter closes and the next Self event starts
  a new total/recent list. Closed totals remain visible until then. No target-death/boss inference.
- Default inactivity timeout is **30 seconds**, configurable with `--idle-seconds 1..3600` (the model
  also accepts subsecond TimeSpan values down to 100 ms for deterministic tests).
- Combat time uses source capture **completion** timestamps, when a complete frame was available,
  not a cast/execution time. Reordered completion is clamped to the previous logical Self time so
  the clock cannot move backwards. No gameplay delay is inferred from packet time.
- EncounterElapsed = last included logical Self completion minus first included Self completion.
  DPS = TotalDamage / EncounterElapsed. Elapsed/DPS update on Self hits and freeze while idle;
  the inactivity waiting/grace tail is not in the denominator. Inter-hit pauses below timeout are.
- For zero or submillisecond elapsed, DPS is **N/A/null**, not damage-as-DPS, Infinity or NaN.
- EncounterSelfHits and recent amounts reset per encounter. Self/Other/Unknown are cumulative only
  within their own epoch; PublishedEvents in performance diagnostics is capture-session cumulative.
- A closed/replaced epoch ends its encounter. Reconnect starts with empty accounting and Unknown
  binding; no identity or total carries over. Multiple active resolved epochs display AMBIGUOUS
  with no arbitrarily selected/combined personal DPS.

Coverage always says **PARTIAL - 06/26 supported; 0x36 pending**. A future shared-decoder promotion
would feed the same accounting path. Unsupported/unknown records contribute nothing. Healing/HPS
are not implemented. The supplied real smoke snapshot log confirms resolved identity, supported Self
events and clean transport, but contains no per-record bytes or timestamp identifying the user's
heal cast. It cannot establish healing semantics or prove that a particular event was a heal. No
current supplied record independently demonstrates healing misclassification; a separately timed
heal-only capture would be needed if such a record is observed. No heal/skill/amount guess is added.

## Console, JSON and cancellation

Interactive stdout updates the same owned rows using relative cursor movement and line erasure;
it does not clear the screen or prior shell history. Rows are width-bounded, output is batched, and
Windows cursor visibility is preserved and restored on cancellation, failure or normal exit.
Redirected/piped stdout (and TERM=dumb) emits one plain text line per report without terminal escape
codes. `--verbose` adds bounded diagnostic reasons. Native Windows/terminal rendering still needs
the manual smoke below; unit tests exercise the actual formatter and cursor lifecycle via writers.

`--json` gives a start record and `Kind="meter"` records containing the SAME LiveMeterSnapshot,
performance and finite epoch diagnostics. Phase 3W.1's string endpoint DTO projection remains intact,
including deliberate IPv6 scope fields. JSON consumers must use the Meter values/feed, not sum the
nested Diagnostics counts. `live-smoke --json` retains its existing diagnostic shape and behavior.

Ctrl+C cancels the existing single-consumer runner. Pending source reads finish before enumerator
disposal; Npcap's existing finally owns handle shutdown. Complete only publishes acknowledged complete
frames, never flushes trailing partials. Terminal restoration is in an IDisposable/finally scope.

## Resources, performance and the next scalability blocker

Raw history remains bounded to the existing **64 MiB payload / 250000 selected packets / 8 active
connections**, plus 8 retired diagnostic summaries. Publication retains at most **100000 event
fingerprints total** across active epochs (also at most that many in one epoch), with 16 bounded
epoch states. The meter retains aggregates and at most five recent amounts per epoch, not event
objects or a hit timeline. End/fault retires fingerprints and raw packets independently from small
committed aggregates. Ended state eviction cannot suppress a fresh epoch: runtime IDs never recur.

Dirty epochs still require bounded finite recomputation and shared prefix assembly; unchanged ticks
reuse the snapshot. Instrumentation exposes diagnostic snapshot tick milliseconds, last dirty-epoch
recompute milliseconds, recompute count, published count, retained identities, selected packet count
and payload bytes. Payload bytes are a retention metric, **not** total managed-memory usage; replay
arrays, objects and temporary decoder allocations add overhead. Timing excludes console write time.

This phase does not promise an indefinitely running low-cost meter. Bound exhaustion visibly faults
the affected epoch and requests fresh reconnect/restart instead of silently rotating framing/binding.
The next actual blocker is migrating the SHARED reassembler/framer/decoder/binding state to a
checkpointed incremental cursor so old raw history can safely retire without re-decoding the whole
retained epoch. That larger change must preserve conflict/provenance/invalidation contracts and be
measured under long live sessions before an always-on WPF overlay. This MVP cannot bypass the bound
by keeping an unbounded dedup/event list or by guessing self after rolling away initialization.

## Public implementation and bug audit (2026-10-08)

Current source, issues, PRs and releases were inspected for behavior; no GPL/no-license code was
copied, adapted or persisted in production. Implementations/tests here are independently written.
There is no universal reset timeout, so this MVP adopts the conservative 30s damage-gap policy and
first-to-last duration, without copying target/party/dungeon or upload semantics.

| Prior art | Finding / independent response |
| --- | --- |
| [A2Tools storage](https://github.com/taengu/A2Tools-DPS-Meter/blob/main/src-tauri/src/combat/data_storage.rs), [calculator](https://github.com/taengu/A2Tools-DPS-Meter/blob/main/src-tauri/src/combat/dps_calculator.rs) | Storage resets targets after a 30s damage gap; calculator uses last-minus-first damage time and separately finalizes history after 10s idle. These are different policies. Per-hit timelines increase clone/memory cost. Adopt 30s personal encounter separation and first-to-last rate; keep only aggregates/recent five. |
| [SkeeveAN aggregator](https://github.com/SkeeveAN/Aion-DPS-Meter/blob/main/Client/Combat/LiveAggregator.cs), [DPS math](https://github.com/SkeeveAN/Aion-DPS-Meter/blob/main/Client/Combat/DpsCalculator.cs), [TCP](https://github.com/SkeeveAN/Aion-DPS-Meter/blob/main/Client/Aion2/Capture/TcpReassembler.cs) | Event-driven accounting; clearing UI without clearing underlying events caused stale totals. Zero-duration DPS is undefined; heal-inclusive totals vs damage-only rate were inconsistent. Sequence overlap/reorder counters are separate from combat events. Our complete-frame publication, encounter reset, no guessed healing and transport tests cover our own contract. |
| [A2Tools PR 29](https://github.com/taengu/A2Tools-DPS-Meter/pull/29), [issue 34](https://github.com/taengu/A2Tools-DPS-Meter/issues/34) | Details/history work could block a 500ms meter refresh; light snapshots/background I/O reduce it. Our 500ms meter has no upload/history I/O; recompute time is exposed rather than assuming scalability. |
| [A2Tools PR 25](https://github.com/taengu/A2Tools-DPS-Meter/pull/25), [issue 19](https://github.com/taengu/A2Tools-DPS-Meter/issues/19), [issue 37](https://github.com/taengu/A2Tools-DPS-Meter/issues/37), [issue 23](https://github.com/taengu/A2Tools-DPS-Meter/issues/23) | Stale fight context/identity, fork upload version identity and retry-state bugs are distinct from combat dedup. We isolate epoch/encounter state and add no uploader/session-file behavior. No documented reconnect/duplicate fix in the reviewed issue/release inventory replaces our independent retransmission/reconnect tests. |
| [A2Tools v2.0.50](https://github.com/taengu/A2Tools-DPS-Meter/releases/tag/v2.0.50), [v2.0.51](https://github.com/taengu/A2Tools-DPS-Meter/releases/tag/v2.0.51), [SkeeveAN v0.13.1](https://github.com/SkeeveAN/Aion-DPS-Meter/releases/tag/v0.13.1) | Multi-connection contamination and Ethernet padding caused lost/misleading fights in A2Tools; these remain covered by existing shared reader/epoch regressions. SkeeveAN's inspected release is primarily profile/history presentation, not evidence for a new transport dedup policy. |

## Verification and manual smoke

Synthetic tests cover ACK gating; repeated snapshots; duplicated/overlapping TCP; split/reordered
records; complete compressed/nested frames; distinct equal amounts; outbound ordinal shifts;
new/same-ISN and new-port epochs; conflict before/after publication; later gaps/partials; binding
conflict; Self/Other/Unknown; aggregate components; first-hit/timeout/new encounter/near-zero rate;
0x36/unknown exclusion; bounds; backwards timestamps; replay source parity; JSON endpoint safety;
multiple resolved scopes; repeated reconnect retention; dashboard/redirect and cancellation cleanup.
The existing protocol/JSON regressions remain the grammar and IPv4/IPv6 safety checks.

From `C:\Projects\Aion2Meter`, after build:

```powershell
dotnet run --project src/Aion2Meter.Replay --no-build -- research live-meter --interface 7
```

1. Start the command before a fresh world TCP connection; adjust interface index only if necessary.
2. Enter the world and confirm the correct character/EntityId becomes Resolved. Midstream remains
   Unknown with "Waiting for fresh character identity..."; manually reconnect if needed.
3. Attack a normal mob; verify Damage, encounter Self Hits, first-to-last elapsed and DPS update.
   First hit shows N/A DPS until a positive interval exists. No dummy is required.
4. Observe Other traffic without it changing personal damage. Coverage must remain PARTIAL.
5. Stop attacking for 30s: status becomes IDLE and the last encounter remains visible. Next Self
   hit starts a new encounter total while epoch counters remain cumulative.
6. Reconnect: old encounter ends, new binding must independently resolve, counts/total start fresh.
7. Ctrl+C: check normal cursor/terminal restoration and clean source shutdown.

Add `--json` for machine diagnostics, `--verbose` for reasons, or `--idle-seconds 10` for a shorter
manual timeout test. Keep logs/research/captures private and ignored. The user-provided successful
Phase 3W smoke is external acceptance evidence, not hard-coded IDs/amounts and not a substitute for
this new dashboard's manual smoke. The agent does not launch AION2 or start native live capture.
