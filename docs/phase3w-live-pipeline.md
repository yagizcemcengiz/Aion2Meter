# Phase 3W: shared live pipeline foundation

The existing Capture project could record passive Npcap traffic to PCAP, while the Replay project
processed finite selected connections. `NpcapLiveSource` now delivers packets to the same downstream
stack as `ReplayPacketSource`. The WPF recording workflow is unchanged.

```text
IPacketSource: ReplayPacketSource / NpcapLiveSource
  -> SourcePacket (source ID, interface ID, monotonic packet index, CapturedPacket)
  -> TcpResearchPacketReader (link/IP/TCP boundaries, declared IP length, no Ethernet padding)
  -> LivePacketPipeline (local address + configurable TCP service port, isolated epochs)
  -> SharedProtocolPipeline
       TcpStreamReassembler -> CandidateBlockExtractor / ApplicationFraming
       -> ReplayProtocolDecoder (existing bounded raw LZ4 and CombatCandidateDecoder)
  -> ReplayCurrentPlayerBindingResolver (fresh handshake and 1536 -> 3336)
  -> DamageEventProjector / ReplayDamageEventEpochAdapter
  -> ReplayDamageEventBindingAssociator (Self / Other / Unknown)
  -> bounded diagnostic snapshots
```

## Scope and finite processing contract

This foundation deliberately adapts live transport to the existing finite epoch processing contract.
Every dirty epoch is decoded on a diagnostic tick; clean snapshots are cached. A new snapshot replaces
the previous result. Counts must never be summed across ticks. These are revisable diagnostics, not
an append-only event/accounting API: a subsequently observed gap or conflicting retransmission can
withdraw an earlier snapshot's accepted events. No encounter, DPS, party filtering, overlay, summon
ownership or skill interpretation is introduced.

Across active epochs, retention is bounded to 64 MiB TCP payload and 250000 selected packets, with
at most 8 concurrent connections and 8 closed summary snapshots. A bound faults the affected epoch,
clears its retained transport state and exposes Unknown with no valid events until a new client SYN.
It never silently rolls a stream forward or recovers self from midstream traffic. The finite replay
decoder's existing decompression/record bounds still apply. Full epoch recomputation grows with the
retained history, so this is a finite live smoke/research foundation, not a promise of indefinite
low-latency combat accounting.

## Capture and flow selection

SharpPcap 6.3.1 (existing dependency, MIT) provides Npcap-compatible passive local capture. No packages,
drivers or arbitrary binaries were added. Adapter enumeration reuses `AdapterDiscovery`, excludes
rpcap devices and requires IP context. Interface status and addresses are displayed; choose the active
adapter carrying game traffic explicitly. Down/virtual/loopback interfaces may be listed with their
status but are not automatically selected.

The filter is `tcp port 13328` by default; `--port` can override the service port. Source/destination IPs
must agree with the selected interface's local addresses. Each exact endpoint pair has separate state.
No historical remote IP is used. Port candidates are labelled separately from observed application
evidence. A readable initialization or supported combat record supplies the protocol-observed marker.
Local address changes require re-enumeration/restarting capture.

The source owns its handles, copies native packet buffers before returning callbacks, disables
promiscuous mode, uses a 500 ms read timeout and a 2 second SharpPcap stop timeout. A 512-packet queue
fails closed on overflow. Npcap-reported drops are checked approximately once per second of packet
delivery and fail the session. Unsupported link/IP traffic and malformed packet headers are counted
and ignored; a truncated selected TCP packet faults its epoch. There is no packet injection or writer.

## Epoch and TCP behavior

An epoch has a source-session UUID, runtime epoch serial, exact local/remote endpoints, client/server
ISNs and SYN timestamp/index evidence. Duplicate SYNs during an incomplete handshake stay in that
epoch. A new client SYN after establishment/FIN, a changed client ISN, or a changed server ISN retires
the old epoch. A changed server ISN alone cannot inherit the prior client handshake. RST retires an
epoch; FIN retains a half-closed scope until both FINs are acknowledged. Late traffic after closure
cannot silently reopen the closed scope. Histories, decoded records and identity never cross epochs.

The existing TCP reassembler reconstructs segments by serial sequence arithmetic, removes duplicates,
retains first-observed overlap bytes and marks conflicts, with gap and packet-completion provenance.
Live snapshots with gaps, a missing fresh stream origin or conflicting overlaps emit no valid events.
They wait for missing segments; no frame resynchronization is attempted. Only complete frames can be
projected. Cancellation discards incomplete records rather than force-flushing them as events.

Replay CLI behavior is preserved through a small shared orchestration refactor. The transport reader,
reassembler, framing primitives, LZ4 decoder, combat grammar and DamageEvent projector are unchanged.
The existing epoch adapter also uses this shared orchestration. There is no second live parser.

## Identity and supported combat

Binding remains Unknown / Resolved / Conflict under the existing fresh-handshake and exact 1536/3336
rules. Association requires the existing finite event, confirmation, coverage and epoch evidence.
Party membership does not affect direct source equality. Starting midstream leaves self Unknown;
without a fresh stream origin this foundation also declines to assume framing alignment or project
combat events. A manual reconnect is needed for a fresh epoch. No first-attacker/kill/large-damage or
memory heuristic is present. Supported bounded 06/26 records are unchanged; 0x36 remains Unsupported.

## Manual smoke test (performed by the user)

Run from `C:\Projects\Aion2Meter`. The agent does not launch the game or start live capture.

1. Launch AION2 and stay before connecting to the chosen world server. Capture must start before
   the game port's fresh TCP handshake; being at character select may already be too late.
2. Enumerate interfaces and select the active game adapter:

   ```powershell
   dotnet run --project src/Aion2Meter.Replay --no-build -- research live-smoke --list-interfaces
   ```

3. Replace `7` with the current list's Ethernet/game-adapter index. Start capture and preserve output:

   ```powershell
   dotnet run --project src/Aion2Meter.Replay --no-build -- research live-smoke --interface 7 --json 2>&1 | Tee-Object -FilePath .tools/tmp/phase3w/live-smoke.log
   ```

4. Connect/enter the world; wait for `ProtocolObserved=true` and `BindingStatus=Resolved` with the
   correct name/id. If it stays Unknown because the handshake was missed, reconnect manually while
   capture runs. Idle transport/binding verification does not require combat.
5. Optionally make a few normal attacks. Supported records should show Self counts and recent Self
   amounts. Nearby actors can show Other; unsupported records remain diagnostic candidates.
6. Press Ctrl+C. Confirm the final `CaptureStopped` snapshot, process exit and preserved log. If an
   epoch faults, preserve the warning and restart/reconnect; do not treat its old counts as complete.

Use `--verbose` with text output for bounded reasons. JSON output includes warnings by default and
contains character names/endpoint addresses; keep it in ignored local research artifacts.

## Verification and next step

Synthetic tests do not need Npcap or a real interface. They cover source/replay parity, compression,
segmentation/out-of-order/gaps, retransmission/overlap, new and repeated ISNs, separate connections,
binding reset, RST/FIN, midstream startup, irrelevant/malformed packets, padding, bounds, provenance
loss and cancellation. The pre-existing replay tests remain the primary protocol regression.

Before encounter/DPS work, obtain the real Npcap smoke log showing a fresh handshake, correct binding,
clean shutdown and (if combat is exercised) supported Self events. An append-only accounting feed and
long-session incremental resource/latency policy need a separate design; do not sum these snapshots.
Tomorrow's independent 0x36 held-out validation can update the shared decoder/tests in its own phase
without changing capture plumbing. Phase 3W does not promote or reinterpret any 0x36 layout.
