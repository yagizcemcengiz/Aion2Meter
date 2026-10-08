# Phase 4A.2 — authoritative two-person roster recovery

Outcome A: a complete two-person `0092` replacement restores the party after a fresh
connection, using new-epoch Self binding and independent remote identity. It does
not require another invite. This is a narrow observed format, not support for every
party size or suffix variant. Unknown layouts continue to withdraw remote eligibility.
The TCP/framing/decoder, DamageEvent, binding and association behavior are unchanged.

## Independent evidence

Physical capture: `2026-10-08_114650_party-reconnect-01.pcap`, with its original JSON.
Neither original is modified. The recording has two fresh game epochs:

| Epoch | Local endpoint | Self Yaaz | Remote Mystogan | Self initialization complete |
|---|---|---:|---:|---|
| 1 | 192.168.1.5:24069 | 4064 | 5065 | 11:47:15.026101 UTC |
| 2 | 192.168.1.5:24088 | 14687 | 5065 | 11:49:07.280222 UTC |

Mystogan's numeric ID happens to remain 5065. Its second-epoch strict `4536` identity
is independently captured at 11:49:08.278711 UTC; an equal old number does not grant
membership. Yaaz's changed ID demonstrates why an old epoch cannot be inherited.

The structural inventory is selected by inbound opcode family, before matching names
or approximate user timings. Epoch 1 contains a Self-only `0092` (record 3438), `0892`
(3441) and complete `0D92` (3724); the normal join agrees with prior strict `4536`.
Epoch 2 has one two-person `0092` (1672, 431 bytes), with no `0892` or `0D92` rejoin.
Both roster entries match their independent current-epoch identities. Remote UUID
and token agree with the first epoch's invite/join as a paired research check;
production never consults old-epoch facts. Runtime/name agreement is checked anew.

An independent earlier reciprocal Client B capture,
`2026-10-07_181807_identity-local-remote-B-01.pcap`, contains the same two-entry form
(record 3839, 430 bytes), with Yaaz first and locally bound Jeffjen second. It confirms
that list position is not a Self/remote selector. Both frames fully consume with the
same positional boundaries and distinct UUIDs/tokens/runtime IDs.

## Recognized grammar and exact boundaries

All lengths include canonical application framing. The prefix must agree with the
shared framing result, raw length and recorded tag. Suppressed/incomplete frames fail.

After inbound tag `0092`:

1. One control byte: `00` or `08`.
2. 24 opaque header bytes.
3. For `08` only: canonical integer 5 and exactly 120 zero bytes (five 24-byte lanes).
   Nonzero or differently sized lanes are unsupported.
4. Canonical member count. Existing count 1 forms remain supported. Count 2 is supported
   only with control `08`; other cardinalities/layouts are unvalidated.
5. Exactly that many member blocks, followed by one opaque footer byte; require EOF.

Each member's common identity prefix is:

| Relative byte | Field / constraint |
|---:|---|
| 0 | observed control `00` |
| 1 | slot; two-entry form requires sequential 1, 2 |
| 2..5 | nonzero runtime ID, u32LE |
| 6..9 | two equal u16LE server-shaped values |
| 10 | UUID byte length 36 |
| 11..46 | valid ASCII GUID in D format |
| 47..54 | opaque 8-byte token |
| 55 | UTF-8 byte length, 1..64 |
| 56.. | strict UTF-8 name, no control characters |

For the two-entry replacement, after each name: 56 opaque bytes; **first entry only**
has a required `3F` layout byte; then 21 opaque bytes. Thus the first suffix is 78
bytes, the second 77. This is an empirical structural boundary, not a meaning assigned
to `3F` or to the opaque bytes. Packed-boolean, stats, class or role semantics are not
claimed. Different suffix layouts need independent evidence. The existing standalone
`0D92` and count-1 block keep their complete opaque 78-byte suffix.

Absolute offsets below use half-open ranges, including each frame's two-byte prefix:

| Frame | Count offset | First member | Second member | Footer |
|---|---:|---|---|---:|
| Reconnect 1672, 431 bytes | 150 = 2 | [151,289), Yaaz | [289,430), Mystogan | 430 = 05 |
| Reciprocal B 3839, 430 bytes | 150 = 2 | [151,289), Yaaz | [289,429), Jeffjen | 429 = 01 |

Names determine byte lengths at a fixed grammar position; they are never searched
as anchors. UUID/token/runtime IDs must be distinct within a replacement. Bound Self
must occur exactly once with the correct name. Each remote must agree with strict
same-epoch `4536`; conflicting names or active UUID/token identity withdraw eligibility.
The count-directed parser consumes every byte and rejects trailing bytes, truncation,
invalid UTF-8, noncanonical counts, changed masks/slots and unknown cardinalities.

These are authoritative current-state replacements: the first-epoch Self-only creation
snapshot and the earlier validated leave replacement agree with the full reconnect
snapshot. Eligible members absent from a validated replacement terminate atomically.
Partial/unknown messages do not supply removal deltas or fabricate additions.

The previously inspected [Kurou PR 68](https://github.com/Kuroukihime/AIon2-Dps-Meter/pull/68)
at head `23ef1562258f81afad4ef9013f92609673d4da5e` treats `0092` as replacement, matching
our independently captured behavior. Its UUID-anchor scan does not establish our
grammar and was not reused. No GPL/no-license source was copied. This phase performs
only that targeted prior-art check, not another broad audit.

## Ordering, state and accounting

In epoch 2, roster completes at 11:49:01.277847 UTC, **before** Self agreement and the
remote's name envelope. The resolver retains at most one compact parsed replacement
with at most two identities and summarized provenance, never its raw bytes. It waits
for the same-epoch proofs. Binding changes and strict identity records can complete
this candidate without another roster or invite message.

Membership begins at the latest of roster completion, Self ValidFrom and all required
remote identity completions (here 11:49:08.278711 UTC). Earlier hits are never added
retroactively. A newer roster or lifecycle transition supersedes a pending candidate.
Malformed transitions, disband, faults and epoch end release it. Duplicate valid
snapshots preserve an existing matching membership's ValidFrom and rows.

Fresh epochs allocate a new resolver and CURRENT accounting. No previous ID/name,
party object, encounter total or disk state is restored. Starting while already in a
party improves only if the full fresh initialization, recognized current snapshot
and independent identity are captured. Midstream startup cannot infer this state.

The same production `PartyRosterResolver` is used by live and replay, through the
existing complete-record feed. No reconnect-only, WPF or second production parser.
Existing compact limits (4096 name facts, 5 active remote members/invites, 16 closed
intervals) remain. The pending replacement adds at most two compact identities.
Publication identities retire at ACKed checkpoints. No full-history rescans or
per-packet UI dispatch are introduced; WPF continues keyed rows at 5 Hz.

## Validation

The physical PCAP is replayed through the production `LivePacketPipeline`, feed,
resolver, meter and presentation with 200 ms capture-time ticks. No timestamps are
rewritten or selected to force expected inputs.

| Actual replay control | Included | Excluded |
|---|---|---|
| Reconnect epoch 1 | Mystogan 101 supported events / 209698; Self 8 / 6234 | 250 unrelated Other |
| Reconnect epoch 2 | Mystogan 1 / 1296; Self 2 / 950 (624 + 326); group 2246 | 377 unrelated Other |
| Prior Mystogan lifecycle | 1228 + 1446 = 2674 | 1284 after leave; 1230 + 246 after disband; 11 unrelated Other |
| CHAD | 6 / 4634 | 267 unrelated Other |
| Phase 3T Self | 6 / 5830 | 383 Other |

The large first-epoch event count is the network-supported count, not a claim about
the user's number of intentional inputs. Current encounter totals reset at the fresh
epoch. Second-epoch overlay has Yaaz ME and Mystogan with new epoch keys. Random Other
cannot enter rows, timing or contribution. Reconnect replay makes 678 checkpoints,
ends with 157 pending payload bytes and zero retained party publication identities.
The bytes are incomplete/awaiting-ACK data, not retained initialization history.

CHAD retains the shared finite-decode/production-feed proof because that historical
AllTraffic recording has backwards timestamps rejected by the unchanged live source
guard. It is not claimed as a passing live-ingestion simulation.

37 added test cases: 36 core/pipeline and one actual STA WPF case. They cover reconnect
and changed IDs, pending proof order/checkpoints, no retroactive hits, independent
identity conflicts, unknown/malformed/truncated snapshots, duplicate members/UUIDs/
tokens, strict UTF-8, full consumption, duplicate refresh, absent-member removal,
supersession/disband/epoch disposal, Self in slot 2, unicode, compact bounds, unrelated
Other timing/contribution, 0x36 exclusion and recovered WPF control reuse. Existing
lifecycle, CHAD, Self, group clock/AFK, ranking and bounded-state tests remain passing.

Full restore/build/test: **756 passed, 0 failed, 0 skipped; 0 warnings, 0 errors**.
Build outputs are isolated under ignored `.tools/tmp/phase4a2/build` so a running old
overlay need not be stopped or overwritten. No transport/unavailable behavior changes.
Coverage remains Partial; 0x36 Unsupported; no healing, Overall, kick, leader or raid.

## Exact next manual smoke

Run the new executable (automated replay does not substitute for this real-game test):

```powershell
& 'C:\Projects\Aion2Meter\.tools\tmp\phase4a2\build\bin\Aion2Meter.App\debug\Aion2Meter.App.exe'
```

1. Resolve Yaaz.
2. Form party with Mystogan.
3. Verify two rows.
4. Both attack once.
5. Without disbanding or re-inviting, return to server selection.
6. Re-enter the same server/character.
7. Allow Self to resolve.
8. Verify Mystogan automatically returns to overlay.
9. Mystogan attacks.
10. Verify his new-epoch damage is counted.
11. Nearby unrelated actor attacks.
12. Verify no unrelated row appears.

Temporary Data unavailable/Waiting during server selection remains intentional fail
closed behavior; fresh initialization restores Self and validated roster afterward.
No further capture is required for this observed two-person recovery. New layouts,
larger groups or failed smoke require new evidence. Before Overall/dungeon-session
work, finish manual CURRENT smoke and independently define dungeon/session boundaries
and persistence/reset semantics; a fresh connection is not a dungeon boundary.
