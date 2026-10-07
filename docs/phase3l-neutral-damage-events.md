# Phase 3L — neutral supported damage events

The replay decoder's accepted `SupportedCombatRecord` is projected to an immutable
Core `DamageEvent`. The projector has no file operations and can later accept an
accepted record from another decoder pipeline. This phase connects only the replay
CLI, without live capture/UI integration.

```text
existing bounded protocol decoder (unchanged grammar)
        ↓ accepted SupportedCombatRecord
DamageEventProjector
        ↓ one DamageEvent per input occurrence
DamageEventAccountingAudit (finite batch, explicit duplicate evidence)
```

Only currently supported inbound `0438` category-6 layouts (`06` and component-bearing
`26`) enter this projection. Unsupported/partial candidates stay on existing research
diagnostic paths. This does not establish complete AION2 combat coverage or self damage.

## Contract and amount

`Aion2Meter.Core.DamageEvent` has get-only properties:

| Field | Meaning |
|---|---|
| `Timestamp` | Accepted record's capture/replay arrival timestamp |
| `SourceEntityId`, `TargetEntityId` | Accepted structural roles, scoped to the capture/connection |
| `RawSkillCode` | Exact opaque unsigned 32-bit value |
| `Amount` | Exactly `AggregateAmount`, counted once |
| `DerivedBaseAmount`, `OptionalComponents` | Diagnostics; ordered components are already included in `Amount` |
| `TypeRaw`, `ModifierRaw`, `DirectionRaw` | Raw values, without gameplay booleans or arithmetic |
| `Provenance` | Immutable snapshot of the exact accepted record's source location |
| `Identity` | Versioned deterministic provenance identity |

Diagnostic numeric fields are non-null because the accepted supported model already
has them; they do not determine accounting eligibility. Components are defensively
copied into a read-only collection. There is no correlation token on the event.

For aggregate `811`, derived base `747`, components `[16,16,16,16]`, the event amount
is **811**. Neither the base alone nor aggregate plus components is the accounting
amount. Optional components have no proc/tick/sub-hit labels. Raw modifier values do
not multiply amounts; no DOUBLE x2 behavior is implemented.

Arrival time is not physical input, server execution, cast start/completion, animation
time or an encounter clock. Record completion time and packet indices are preserved
separately in provenance. Entity IDs are not player/party/NPC/account/owner identities.

## Provenance identity

`DamageEventProvenance` snapshots these existing record fields:

- Exact `SourceCapture` as `CaptureScope`, decoder `RecordId` and traffic direction.
- Stream offset, outer frame ID and offset, full ordered container IDs/inner offsets.
- Arrival timestamp, initial packet index, completion packet index and timestamp.
- Frame prefix length, frame length and record tag.

The scope is an exact caller-provided string. The CLI uses the physical absolute capture
path, without substituting semantic labels. No absent metadata SessionId or TCP epoch
is invented. Cross-file or future live epoch binding remains a separate concern.

`damage-record-v1:` followed by SHA256 of a length-prefixed binary encoding identifies
this complete provenance snapshot. Strings use exact UTF-8; integers have fixed binary
encoding; timestamps use UTC ticks (equivalent offsets represent the same instant).
Nested path order and every ancestor are included. There is no delimiter concatenation,
code normalization, token, amount or source/target heuristic in identity. Different
provenance produces a different identity even when all event values and arrival times
are equal. The same provenance produces a stable identity across projection calls.

The immutable event does not retain a mutable raw-byte array. Its source location
identifies the original research record, which still retains raw bytes and unknown regions.

## Projection and finite accounting audit

`DamageEventProjector.Project(SupportedCombatRecord)` copies one accepted aggregate.
`ProjectMany` preserves input order and every occurrence, including repeated input.
It trusts the existing accepted grammar boundary and checks the inbound category/tag
contract; it does not decode new gameplay fields or relax decoder guards.

`DamageEventAccountingAudit.Analyze` uses only provenance identity to group inputs:

- Unique provenance: count its aggregate once.
- Repeated provenance with equal event content: count once and report every input index/event.
- Repeated provenance with conflicting projected content: retain an explicit conflict
  diagnostic and reject that whole identity from validated count/total/code buckets.
  The helper does not guess which copy is authoritative.
- Identical values or repeated codes at **different** provenance: count independently.

Results include input count, unique provenance count, validated unique count, duplicate
input count, rejected provenance count, validated total, duplicate occurrences and opaque
exact-code buckets. Totals use `decimal` so two valid `ulong` amounts cannot overflow a
`ulong` accumulator. Conflicting provenance groups are distinguishable from rejected
unsupported decoder candidates, which never enter the event batch.

The audit is a finite input validation helper. It contains no encounter lifecycle,
timers, DPS, self/party filtering, resets or live state. Its totals cover all accepted
actors/targets supplied to the batch. Code buckets are technical diagnostics, not the
product Skill Breakdown feature. Similar exact codes remain separate; a reusable code
is counted like any other code.

## Independence and CLI

A record group is not an accounting unit. An action window, auxiliary match, name,
class, ownership or skill metadata is not an accounting acceptance condition. A supported
record projects when none of those objects exists. Visual annotations are external
validation and never projector input.

```powershell
dotnet run --project src/Aion2Meter.Replay -- research damage-events <capture.pcap> --json
dotnet run --project src/Aion2Meter.Replay -- research damage-events <capture.pcap> --summary
```

Endpoint selection and bounded decode options are shared with `research decode`.
Without explicit `--local` and `--remote`, companion metadata must contain exactly one
game TCP connection. JSON includes events and exact provenance, accepted/projected/
unsupported counts, the accounting audit and raw-code totals, unsupported candidates,
and decoder suppression/gap/conflict/container diagnostics. `--summary` omits full event
and unsupported-candidate rows while retaining counts/audit. Text output gives individual
event identities and aggregates. CLI order uses the existing deterministic record-arrival
comparer; the projector itself preserves supplied input order.

## Validation and remaining boundaries

Synthetic tests cover accepted/unsupported separation, exact aggregate/components,
raw fields, immutable snapshots, stable/scoped/nested provenance identity, repeated codes,
same values at different provenance, duplicate/conflicting input, and CLI replay with
compressed records. Real captures and external visual comparisons stay in ignored local
analysis output, without real PCAP unit-test fixtures or production dataset constants.

Historical visual notes `373/363` remain separate from accepted network aggregates
`573/363`. Projection must preserve network amounts without adjusting the decoder or
visual notes. Analogous historical raw codes remain exact and distinct.

Self identity may layer onto this neutral stream, but an authoritative current-player
binding and lifecycle validation are required before self total, DPS or overlay claims.
Class, ownership/summon roll-up, skill names, gameplay flags, DoT/heal attribution,
encounter policy, live delivery and complete combat coverage are outside Phase 3L.
