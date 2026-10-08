# Phase 3Y: long-session checkpoint retention

The console live meter now decodes an active transport window rather than its entire historical
epoch. The finite replay and live-smoke diagnostic paths remain available. No transport reader,
TCP assembler, framer, LZ4 decoder, combat grammar, DamageEvent model or accounting system was
replaced. Category 0x36 stays Unsupported. Party filtering, healing, overlay and midstream recovery
remain outside this phase.

## Measurement before production changes

An independent synthetic test was added and run against the original Phase 3X implementation
before production files were edited. It sent 60 batches of 100 distinct supported events, with
one diagnostic tick after each batch. Input/event lists in the harness were cleared after each
tick so the measurement did not retain its own session history.

| Events | Retained packets | TCP payload bytes | Publication identities | Last recompute ms | GC live process bytes |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 9 | 3,024 | 100 | 95.11 | 6,984,248 |
| 1,000 | 27 | 30,024 | 1,000 | 7.23 | 7,365,752 |
| 3,000 | 67 | 90,024 | 3,000 | 33.87 | 8,151,656 |
| 6,000 | 127 | 180,024 | 6,000 | 34.34 | 9,654,712 |

The first tick includes JIT/warmup. Single-tick durations are noisy, not throughput guarantees.
The growing collections were the selected packet list and publication location/fingerprint
dictionary. Every dirty snapshot assembled the entire list, extracted an ACKed prefix, assembled
it again, decoded all records, projected all supported events, and reassociated all events.
There was no checkpoint state. Binding itself already held two immutable evidence records (24
complete-record bytes in this synthetic identity), but keeping it valid required the historical
raw packet list. A later explicitly disabled-checkpoint comparison confirmed these same packet,
payload and identity counts and measured the binding evidence separately.

Ignored measurements: `.tools/tmp/phase3y/pre-change.json`, `legacy-instrumented.json`,
`post-change.json`, `soak.json`. These are local research output, not committed captures.

## Safe boundary and processing

1. Ingest still uses the existing TCP reader, endpoint selection, fresh handshake scopes, ordered
   source packet indices and nondecreasing capture times. A checkpoint is enabled by default only
   for a pipeline with a LiveCombatFeed; the comparison switch is not a new CLI recovery option.
2. The shared TcpStreamReassembler assembles only retained packets. The checkpoint translates
   its relative offsets into the epoch's absolute 64-bit stream offsets. TCP sequence arithmetic
   remains modulo 32 bits within a bounded window; the 64 MiB limit now bounds the pending window,
   rather than the established connection's lifetime byte offset.
3. LivePublicationBoundary starts at each direction's committed cursor, requires a contiguous
   prefix and observed cumulative peer ACK, then uses the existing CandidateBlockExtractor to
   end at a complete application frame. A gap cannot become a new framing origin.
4. SharedProtocolPipeline decodes that prefix with the existing protocol/LZ4 machinery. Failed,
   suppressed or partly parsed completed containers fail closed. An incomplete outer/container
   frame remains raw pending bytes and is not decompressed, published or retired.
5. The existing projector and ReplayDamageEventBindingAssociator produce the batch events.
   LiveCombatFeed validates their locations/fingerprints, publishes each once, and the unchanged
   LiveCombatMeter accounts them. Only after successful publication does the pipeline advance
   the per-direction cursor and remove the committed prefix.

Raw retirement starts only after the existing fresh-epoch resolver proves the unique exact
1536/later-3336 pair. Unknown startup still has the old bounded bootstrap history and can reach
its limit; starting in the middle of a session still requires reconnect/character entry. This is
intentional until Phase 3Y.1 provides independently validated authoritative identity hooks.

After initialization retirement, an internal resolver continuation preserves the immutable
validated scope/evidence summary and extends observed coverage. It accepts no externally asserted
identity. Any newly observed initialization sequence remains unmodeled: disagreement produces
Conflict, duplicate/ambiguous refresh produces Unknown, and publication invalidates the epoch.
There is no majority vote or zone-reset guess. The association adapter continues to attest owned
projected events and the exact original confirmation evidence. Offsets, packet indices,
arrival/completion timestamps and container inner offsets survive trimming. Finite decode record
ordinals can differ between windows; original published event objects remain immutable.

## Bounded retained state and dedup

- Pending raw segments retain incomplete frames, missing-gap context and unacknowledged bytes.
  Existing global packet/payload limits still fail closed for a blocked or oversized window.
- The latest/highest peer ACK per direction, first FINs and their observed acknowledgments remain
  bounded control evidence. Newer regressing ACKs cannot discard earlier stronger ACK evidence.
- A fixed maximum of 64 KiB of committed bytes per direction remains for overlap verification.
  It also supplies the existing privacy scanner's 1,024-byte raw boundary context. The maximum
  is 128 KiB per flow, at most 1 MiB with eight active flows, separately from pending payload.
- The binding summary retains the validated evidence, not the TCP initialization packet history.
  Evidence raw bytes are fixed by the validated initialization records, bounded by the shared
  frame/decode limits; the 24-byte synthetic summary is not a universal size promise.
- Publication dictionaries are cleared only after the whole decoded batch lies inside the committed
  transport prefix. Two monotonic direction watermarks replace historical locations. Old bytes
  cannot be decoded again; a publication below a watermark invalidates the epoch. This is not
  arbitrary identity eviction. A burst exceeding the publication batch bound still fails closed.
- Epoch snapshots, recent five Self values and encounter accounting remain bounded. A reconnect
  releases packets, verification bytes, binding summary and identities and starts an independent
  scope. Closed diagnostics remain bounded. Aggregate totals survive checkpoints, not reconnects.

Verified retired retransmissions are trimmed/ignored, including partial overlap followed by new
bytes. A conflict within the verification window invalidates the whole epoch's meter. Payload
older than the verification window **also fails closed** because byte equality can no longer be
proved. It never recounts, but an extremely delayed legitimate retransmission can stop the meter.
We deliberately do not silently trust released bytes or retain unlimited historical hashes.
Gaps hold publication until filled; no resynchronization was added. Privacy/decode failure likewise
requires a fresh epoch. These are the remaining availability limitations.

## Synthetic long-session result

The post-change 6,000-event comparison retained 2 control packets, 0 pending payload, 0 event
identities, 2 binding evidence records / 24 evidence bytes, and 64 KiB verification context.
The last recompute was 3.06 ms versus the original 34.34 ms; GC process live bytes were 7,309,592
versus 9,654,712. This illustrates the removed history cost, not a fixed speedup guarantee.

A longer run sent 120,000 events in 1,200 batches, plus the initialization checkpoint. It exceeded
the former 100,000 session-identity bound without discarding accounting. Each event had Amount=1;
TotalDamage and publication count were exactly 120,000. Timing includes ingest and snapshot.

| Events | Checkpoints | Packets | Pending payload | Identities | Verification bytes | Median batch ms | GC live process bytes |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10,000 | 101 | 2 | 0 | 0 | 65,536 | 1.10 | 7,934,312 |
| 30,000 | 301 | 2 | 0 | 0 | 65,536 | 1.22 | 7,935,464 |
| 60,000 | 601 | 2 | 0 | 0 | 65,536 | 1.11 | 7,937,584 |
| 120,000 | 1,201 | 2 | 0 | 0 | 65,536 | 0.92 | 7,941,752 |

Process memory includes the .NET runtime/xUnit and is not isolated pipeline memory. These samples
show a plateau, not a proof of constant process working set. Tiered JIT, GC and other processes
affect timings. A separate transport test retired more than 64 MiB of valid frames, and a fresh
ISN-near-wrap test crossed the TCP sequence wrap. Transient work still scales with the current
batch/tail; snapshot framing, hashing and bounded context copies can be profiled in the final
optimization pass. No per-packet UI work or new dependency was introduced.

## Regression coverage and files

22 new test cases exercise repeated checkpoints, exact totals past the old identity cap, retired
duplicate/partial overlap, incomplete plain/LZ4 tails, peer ACK gating/regression, shared finite
replay parity with nested containers and mixed Self/Other records, stable physical provenance,
near-cursor gaps/conflicts, immutable binding evidence, unknown startup, inactivity, reconnect,
FIN cleanup, lifetime byte limits, wrap, publication/pending bounds, privacy context, initialization
refresh rejection, malformed containers and unchanged Unsupported 0x36 behavior. Existing 623
tests remain part of the full run. Build: 0 warnings / 0 errors; tests: 645 passed / 0 failed / 0 skipped.

Production: `LivePacketPipeline.cs`, `LiveStreamCheckpoint.cs`, `LivePublicationBoundary.cs`,
`LiveCombatFeed.cs`; research adapter/resolver: `ReplayDamageEventEpochAdapter.cs`,
`ReplayCurrentPlayerBindingResolver.cs`; diagnostics: `LiveMeterJson.cs`, `LiveMeterDashboard.cs`,
`LiveSmokeCli.cs`. Tests: `LiveCheckpointTests.cs`, `LiveCombatMeterTests.cs`. Documentation: this file.
The original protocol decoder, assembler, DamageEvent and LiveCombatMeter files are unchanged.

## External prior art audit

Public source/issue/PR/release pages were read as architectural prior art only. No external source
was copied or saved into the repository; the implementation and tests above were written independently.

| Primary reference | Relevant observation / decision |
| --- | --- |
| [SkeeveAN TCP reassembler](https://github.com/SkeeveAN/Aion-DPS-Meter/blob/main/Client/Aion2/Capture/TcpReassembler.cs) | Uses a moving sequence cursor, bounded pending segments and removal of consumed frames. Converges on retaining tail rather than history. Its gap resync/heuristic midstream alignment was not adopted. |
| [A2Tools combat storage](https://github.com/taengu/A2Tools-DPS-Meter/blob/main/src-tauri/src/combat/data_storage.rs) | Separates aggregate combat data from per-hit detail and lighter snapshots. Supports keeping current aggregates rather than packet history; per-hit detail can still grow in other designs. |
| [A2Tools PR 29](https://github.com/taengu/A2Tools-DPS-Meter/pull/29) | Addresses snapshot/detail/history performance and background work. Keep tick work bounded and avoid heavy history in dashboard updates. |
| [A2Tools issue 34](https://github.com/taengu/A2Tools-DPS-Meter/issues/34) | Performance/loading report reinforces measuring real long sessions beyond synthetic proof. |
| [A2Tools v2.0.50](https://github.com/taengu/A2Tools-DPS-Meter/releases/tag/v2.0.50) | Multiple TCP connections could interfere with tracking. Preserve endpoint/epoch isolation. |
| [A2Tools PR 25](https://github.com/taengu/A2Tools-DPS-Meter/pull/25) | Dungeon/fight attribution and late roster/zone transitions show why current transport/identity and encounter labels need separate authority. No arbitrary zone-triggered resets added. |
| [A2Tools issue 19](https://github.com/taengu/A2Tools-DPS-Meter/issues/19) | Framing/attribution correctness report; preserve the shared length rule and explicit identity authority. |
| [A2Tools issue 23](https://github.com/taengu/A2Tools-DPS-Meter/issues/23) | Upload retry/reporting behavior is another background failure source. No uploader is present or added here. |

## Manual 30–60 minute normal-play soak and next phases

Start before a fresh character entry/reconnect so the existing authoritative pair is observed:

```powershell
Set-Location C:\Projects\Aion2Meter
dotnet run --project src/Aion2Meter.Replay --no-build -- research live-meter --interface 7 --verbose
```

Play normal open-world content for 30–60 minutes. No dummy experiment is required. The verbose row
shows tick, checkpoint count, publication count, pending packets/bytes and verification bytes.
Expect checkpoints to advance, ordinary ticks to retain a small tail, and verification bytes to
plateau. Confirm Self damage/DPS, 30s inactivity, reconnect requiring fresh identity, and clean
Ctrl+C exit. `--json` offers the same DTO metrics for later ignored logging. Normal dashboard
omits resource details. A real native soak has not been performed by the agent; a later 1–3 hour
run is still required before release.

The architecture is ready for Phase 3Y.1: an authoritative zone/channel/teleport/instance
initialization refresh could establish a new attested identity checkpoint after independent
capture validation. Required evidence includes structural identity fields, local-vs-remote
discrimination, direction, ordering, transport scope, closure/invalidation and held-out refresh
repeatability. Current 1536/3336 observations are hooks for research, not proof of midstream
recovery. No first-hit/kill, old-name reuse or majority heuristics should be added.
WPF overlay can follow Phase 3Y.1 validation using this shared feed and bounded meter snapshot;
that does not remove the real-soak/release validation requirement or enable party/HPS/0x36.
