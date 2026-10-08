# Phase 3Y.1: bounded midstream waiting and automatic fresh-epoch recovery

Outcome **C**. A running meter can safely recover after attaching mid-game when it next observes
a complete fresh game TCP epoch and the existing authoritative initialization pair. It needs
neither a meter restart nor closing/restarting AION2. Immediate same-epoch attach is **not**
implemented or proven. A teleport/channel/instance transition that stays on the same TCP epoch
does not resolve identity in this implementation. Which natural transitions actually reconnect
the current game client remains a manual availability check, not an assumption.

Baseline: `b33dabc`, `phase-3-protocol-research`, 645 tests. Research was performed before changing
production files. No public source code was copied, no extra dependency was added, and no
original PCAP/metadata was renamed or edited. Capture-specific annotations, endpoint inventories,
hashes and machine-readable research results remain under ignored `.tools/tmp/phase3y1/`.

## Public implementation audit

Current source, READMEs/troubleshooting, relevant issues/PRs and latest release notes were reviewed
for all five requested projects. GitHub default-branch HEADs were checked through its API;
issue discovery covered the latest up to 100 issues/PRs per repository and latest 10 releases.
This is a targeted audit, not a claim that every historic discussion was exhausted.

| Project / audited HEAD | Mid-game framing and self behavior | Evidence classification / decision |
| --- | --- | --- |
| [taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter), `c5bd3516e7278ff223819a1747e6f7ae7e34dde7` | Outer length walk can skip invalid bytes; bounded FF/LZ4 extraction also searches inside opaque records for local `3336` profiles. Game-supplied self identity overrides stale configured/window-title identity. Latest v2.0.53 advertises recognition after mid-game startup. Current fallback uses `0638` entity-frequency dominance and loot-owner voting within a player/party scope, before the self record arrives. | Complete local-character records are authoritative **candidates**; bounded decompression is strongly constrained extraction. Scope-frequency/loot voting and remembered/window-title name matching are **HEURISTIC**, not our binding authority. It explicitly documents a bystander kill and region-dependent party inclusion. We do not adopt these fallbacks. |
| [SkeeveAN/Aion-DPS-Meter](https://github.com/SkeeveAN/Aion-DPS-Meter), `7f7adc3146a42c660cef64d27b6a1216e0496ea3` | TCP attach searches up to 4 KiB for consecutive known-opcode frames, or a single frame exactly covering a buffer, and can resync after gaps. Local `3336` profile has priority. Without it, configured names, dominant detailed-stat recipient counts and dominant unnamed class-skill casters can determine local identity; saved profiles do not blindly reuse their old combat ID. README still recommends starting before character login. | Repeated-frame alignment is **STRONGLY CONSTRAINED** but first-candidate selection does not prove absence of competing interpretations. Stat/caster dominance is **HEURISTIC**, even with count/ratio thresholds. Local-character profiles remain an authoritative candidate pending our own same-epoch proof. |
| [TK-open-public/Aion2-Dps-Meter](https://github.com/TK-open-public/Aion2-Dps-Meter), `e72c6d5e0c9b647f5c1fcc0fca76fb22cd734d4e` | Stream alignment starts from the first observed sequence; frame extraction/LZ4 exposes an own-nickname `3336` handler distinct from remote `4436/4536`. Own-name extraction searches a short record region and validates the resulting nickname. Releases add remote-name parsing; issues report numeric/missing character names. | Observed sequence is not an authoritative TCP origin. Own-nickname opcode is an authoritative **candidate**; name-region scanning is **HEURISTIC extraction**. The audited paths provide no guaranteed immediate self recognition if that own record was missed. |
| [Kuroukihime/AIon2-Dps-Meter](https://github.com/Kuroukihime/AIon2-Dps-Meter), `2e8d67d62cef0532f10aecb2df5426b50f4d2f79` | Accumulator scans for `0E 00 36`, then walks bounded lengths. `3336` PlayerInfo marks `isUser`; remote/global records use separate linking, with a name-based fallback. FAQ explains names arrive with particular teleport/dungeon events, otherwise placeholders persist. | A short sync marker alone is **HEURISTIC** and unsafe as sole boundary proof. Own-record identity is an authoritative **candidate**. Name-based global/session linking is **HEURISTIC**; missing names are acknowledged rather than guaranteed immediate recovery. |
| [a2meter/Aion2Meter](https://github.com/a2meter/Aion2Meter), `3194fc3d03805397baf671c53b2fbff207baeb37` | Bounded stream processor handles lengths/LZ4 and scans a short sync marker on corrupt lengths. Dispatcher locates self `3336` and remote `4436` information; some self-name formats involve a bounded scan. Character lookup's local-like byte is explicitly not authoritative self information. | Bounded envelopes are **STRONGLY CONSTRAINED**; marker/tag/name scanning is **HEURISTIC**. `3336` is an authoritative **candidate** when fully structurally validated. Treating a lookup flag as self would be **UNSAFE**. No audited path proves a universally available refresh for our legacy sessions. |

Primary source references:

- A2Tools [framing](https://github.com/taengu/A2Tools-DPS-Meter/blob/main/src-tauri/src/capture/framing.rs),
  [identity/container processing](https://github.com/taengu/A2Tools-DPS-Meter/blob/main/src-tauri/src/capture/stream_processor.rs),
  [loot/scope identity fallbacks](https://github.com/taengu/A2Tools-DPS-Meter/blob/main/src-tauri/src/combat/data_storage.rs),
  [v2.0.53](https://github.com/taengu/A2Tools-DPS-Meter/releases/tag/v2.0.53),
  [v2.0.52 reconnect/upload fix](https://github.com/taengu/A2Tools-DPS-Meter/releases/tag/v2.0.52),
  [PR 25 zone/late roster attribution](https://github.com/taengu/A2Tools-DPS-Meter/pull/25).
- SkeeveAN [TCP recovery](https://github.com/SkeeveAN/Aion-DPS-Meter/blob/main/Client/Aion2/Capture/TcpReassembler.cs),
  [local profile and inference](https://github.com/SkeeveAN/Aion-DPS-Meter/blob/main/Client/Aion2/Aion2EntityDirectory.cs),
  [PR 3 mid-dungeon party-name heuristics](https://github.com/SkeeveAN/Aion-DPS-Meter/pull/3),
  [PR 5 pre-start summons and byte-length names](https://github.com/SkeeveAN/Aion-DPS-Meter/pull/5).
- TK [own/other nickname processing](https://github.com/TK-open-public/Aion2-Dps-Meter/blob/main/src/main/kotlin/packet/StreamProcessor.kt),
  [TCP alignment](https://github.com/TK-open-public/Aion2-Dps-Meter/blob/main/src/main/kotlin/packet/PacketAlignmenter.kt),
  [missing own names issue 86](https://github.com/TK-open-public/Aion2-Dps-Meter/issues/86).
- Kuroukihime [marker synchronization](https://github.com/Kuroukihime/AIon2-Dps-Meter/blob/master/AionDpsMeter.Services/PacketCapture/PacketAccumulator.cs),
  [local PlayerInfo](https://github.com/Kuroukihime/AIon2-Dps-Meter/blob/master/AionDpsMeter.Services/PacketProcessing/Processors/PlayerInfoProcessor.cs),
  [entity linking](https://github.com/Kuroukihime/AIon2-Dps-Meter/blob/master/AionDpsMeter.Services/Services/Entity/EntityTracker.cs),
  [PR 21 frame-size bound](https://github.com/Kuroukihime/AIon2-Dps-Meter/pull/21).
- a2meter [stream recovery](https://github.com/a2meter/Aion2Meter/blob/master/src/A2Meter/Dps/Protocol/StreamProcessor.cs),
  [identity dispatcher / lookup caveat](https://github.com/a2meter/Aion2Meter/blob/master/src/PacketEngine/Dispatcher.cs).

These projects converge on receiving local-character records and practical framing recovery,
but do **not** converge on one universally authoritative immediate self-identification method.
First attacker/source, first kill, majority/most-active actor, leftover roster name, and local-like
lookup bytes are not accepted. Outbound direction establishes that our client sent a request;
it does not establish a particular actor ID field. Our existing outbound request/cast evidence
has no independently validated explicit local actor/session-token linkage. No outbound fallback
was implemented, and timing/cast correlation was not promoted into identity semantics.

## Existing corpus and smallest hypothesis

All 38 capture pairs were checked: 37 selected game flows plus the browser negative (43,075
packets, zero game-port packets). The per-game inventory records transport origins, SYNs,
framing/research-record coverage, inbound/outbound tag counts, initialization/name records,
external annotation availability, recurring combat sources and association counts. Missing
annotations remain missing. Duplicated physical UI labels do not become session identity.
Research tags/temporal transitions are not labelled zone/channel/instance without protocol proof.

All 31 legacy game flows have no observed client SYN, no server SYN-ACK and no known ISNs.
Research-only candidate framing can expose records/events from their observed runs; its starting
boundary is not thereby authoritative for live publication. No decoded `1536` or `3336` occurs
in these 31. Three literal `3336` byte sightings occur in short unknown 11-byte records, at offsets
2/4/3, with different actual framed opcodes: they are not bounded local-character records.

To test A2Tools' embedded-container prior, an ignored probe used **our existing** canonical frame
reader and bounded shared LZ4/container decoder on unknown inbound records. It tried 1,408
complete length-bounded FF candidates across the legacy corpus: zero valid complete containers,
zero local `3336` candidates, zero `1536` candidates. No scanner or alternate grammar was added
to production. This does not prove that future captures cannot contain authoritative refreshes;
it shows the smallest candidate has no positive self evidence in the available legacy corpus.
Standalone periodic/masked `3336`, detailed-stat recipients and refresh inside a same epoch still
need independent local-vs-remote and boundary validation before implementation.

Safe arbitrary midstream framing and same-epoch self recovery are therefore **not established
by this phase**. Canonical lengths, a few plausible opcodes or a short marker do not prove a
unique interpretation; the live path rejects all uninitialized cases, including plausible,
ambiguous and malformed synthetic bytes. The speed/stop condition was applied without requesting
a new long capture or trying to force outcome A.

## Implemented behavior and evidence

On a selected flow first seen without a client SYN, the pipeline records a bounded waiting
scope with endpoint/lifecycle counters. It does not clone, retain, frame, decode or publish its
payload. Snapshot recomputation is constant in discarded history. A FIN/RST ends this waiting
scope; late old traffic cannot reopen it. Discard counters are diagnostics, not combat totals.

A new client SYN automatically supersedes waiting scopes, even if the old connection never
closed and even if it uses a different local port. It frees only uninitialized midstream slots,
never a fresh unresolved/Resolved scope. The normal handshake, byte-conflict/gap, peer-ACK,
framing/LZ4, initialization extraction, resolver and association paths then run unchanged.
Old waiting identity, bytes and totals cannot carry over. Simultaneous validated fresh scopes
still pause the meter rather than selecting an arbitrary player.

Only the existing complete fresh handshake plus inbound `1536` candidate and later matching
`3336` confirmation can resolve self. `IdentityValidFrom` is the shared binding's confirmation
**completion timestamp**. Events before this time stay Unknown; older midstream bytes were
never published. Only later directly matching source IDs contribute Self damage. Different IDs
are Other, not party members or summons by inference. Conflicts do not vote/select an identity;
an already-published epoch whose authority is invalidated still fails closed under Phase 3Y.

Audit DTOs expose `RecoveryMethod` and `IdentityValidFrom`: `WaitingForFreshEpoch`,
`FreshInitialization`, or `FreshEpochAfterMidstreamStart` once resolved (`None` while fresh
initialization is incomplete or authority invalidated). No confidence percentages are invented.
JSON retains explicit safe string endpoints. The normal dashboard shows `Binding: Waiting for
identity...` and the next-fresh-connection hint; resolved display stays `Binding: Resolved`.
Verbose output additionally shows recovery provenance/ValidFrom. The solo encounter timeout,
amount/DPS calculation, exactly-once publication and checkpoint mechanics are unchanged.
Category `0x36` stays Unsupported; no party filter, HPS or overlay was implemented.

## Verification

Full `dotnet restore`, `dotnet build --no-restore`, `dotnet test --no-build --no-restore` succeeded:
**660 passed, 0 failed, 0 skipped, 0 build warnings, 0 build errors**. Fifteen new test cases cover
same/new-port recovery without old FIN, all waiting connection slots occupied, preservation of
fresh scopes under capacity pressure, missing client SYN, rejection of valid-looking/ambiguous/
malformed midstream framing, immutable pre-confirmation Unknown, later Self/Other, stale identity
isolation, conflicting initialization, metadata invalidation, dashboard/JSON provenance, the
ordinary fresh path and unchanged Unsupported `0x36`.

The full game corpus was rerun after changes: all 111 decode/event/association JSON results are
identical to their pre-change results, including all bindings, evidence, event identities,
provenance and ordering. Original capture/metadata SHA-256 hashes are unchanged. The browser
negative still has no selected game traffic.

| Result | Before | After |
| --- | ---: | ---: |
| Legacy midstream captures newly resolved | 0 | 0 |
| Legacy Unknown / Conflict | 31 / 0 | 31 / 0 |
| Fresh Resolved / Conflict | 6 / 0 | 6 / 0 |
| Accepted game DamageEvents | 2,738 | 2,738 |
| Self / Other / Unknown | 6 / 704 / 2,028 | 6 / 704 / 2,028 |
| Phase 3T Self count / amount | 6 / 5,830 | 6 / 5,830 |
| CHAD controlled Other count / amount | 6 / 4,634 | 6 / 4,634 |

The old 1,689 legacy figure was the earlier Phase 3S decoder baseline; the current 2,028 legacy
count already existed before this phase after Phase 3V's bounded 06/26 coverage extension.
It is not midstream recovery or a count change introduced here. Finite research results remain
available; live uninitialized flows deliberately emit no combat events rather than trusting the
research boundary assumption.

The recovery soak discards 70,000 selected 1-KiB midstream payloads (71,680,000 bytes) without
reaching the former 64-MiB history limit, then initializes a fresh epoch and publishes 120,000
events with exact TotalDamage=120,000. It completes 1,201 checkpoints and retains 2 control
packets, 0 pending payload, 0 publication identities, 2 binding evidence records and 64-KiB
verification context. A separate rerun of the original 120,000-event fresh soak also passes.

Isolated recovery-soak GC live-process measurements (runtime/xUnit included):

| Phase | Progress | Retained packets / payload / identities | GC live bytes |
| --- | ---: | --- | ---: |
| Waiting | 10,000 packets | 0 / 0 / 0 | 5,615,960 |
| Waiting | 70,000 packets | 0 / 0 / 0 | 5,616,184 |
| Recovered | 10,000 events | 2 / 0 / 0 | 7,373,824 |
| Recovered | 60,000 events | 2 / 0 / 0 | 7,659,368 |
| Recovered | 120,000 events | 2 / 0 / 0 | 7,659,464 |

This supports bounded retained state and a measured plateau; it is not a native capture
working-set guarantee. A full-suite concurrent measurement has higher process memory because
other xUnit tests run concurrently. No real native mid-game transition test was run by the agent.

Changed files: `LivePacketPipeline.cs`, `LiveCombatFeed.cs`, `LiveCombatMeter.cs`,
`Research/LiveDiagnosticJson.cs`, `Research/LiveMeterDashboard.cs`, `Research/LiveSmokeCli.cs`,
`LiveStartupRecoveryTests.cs`, the existing midstream waiting-text assertion in
`LiveCombatMeterTests.cs`, and this document. Protocol/transport parsers and identity resolver
were not modified.

## Minimum manual check and overlay decision

1. Start AION2, enter the world, then launch the meter:

   ```powershell
   Set-Location C:\Projects\Aion2Meter
   dotnet run --project src/Aion2Meter.Replay --no-build -- research live-meter --interface 7 --verbose
   ```

2. Expect Waiting for identity, no Self total. Keep both applications running.
3. Return to character selection and enter the same character again, without closing AION2.
   This exercises the reconnect/fresh-entry path already present in our corpus. A normal channel,
   zone or instance transition may also work **only if** it actually creates a fresh TCP epoch;
   same-connection teleport is not promised to work.
4. Confirm character name and `Binding: Resolved`; verbose recovery should read
   `FreshEpochAfterMidstreamStart` with a non-empty ValidFrom.
5. Attack a normal mob and confirm Self damage/DPS, then Ctrl+C. No dummy, long recording or
   exact timestamp annotations are needed. If entry does not produce the required epoch/evidence,
   report the displayed status rather than assigning an identity manually.

WPF overlay implementation can proceed using the existing shared feed and bounded snapshots.
This C fallback and its minimum real manual check should remain explicit; it does not meet an
immediate same-epoch startup promise. Party, healing, category 0x36 validation and the final native
long-session optimization/soak remain separate work.
