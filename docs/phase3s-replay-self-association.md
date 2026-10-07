# Phase 3S: replay DamageEvent / CurrentPlayerBinding association

`DamageEventPlayerAssociation` is an immutable overlay for one input occurrence. Its
`DamageEventPlayerAssociationStatus` is `Unknown`, `Self`, or `Other`. The finite
`DamageEventPlayerAssociationAudit` retains input order and every occurrence, including
duplicates, with input/self/other/unknown counts. It performs no amount accounting.

`ReplayDamageEventBindingAssociator` is pure and stateless. `Self` requires a Resolved
binding, a trustworthy matching capture/connection/epoch, supported event provenance,
complete confirmation, the bounded evidence window, and direct `SourceEntityId ==
binding.EntityId`. `Other` has the same eligibility conditions but a different direct
source ID. It does not mean enemy, player, NPC, pet, summon, or owned damage. Class and
ownership remain Unknown. All failed eligibility checks yield Unknown with diagnostics;
Unknown or Conflict binding can never yield Self or Other.

## Explicit epoch bridge

`ReplayDamageEventEpochAdapter.Create` takes an already selected in-memory research
capture and endpoint pair. It independently reassembles/decodes that capture, resolves
the existing Phase 3R contract, and projects supported records. The adapter snapshots
its immutable binding/evidence and neutral event projection. Association requires exact
`ReplayConnectionScope` equality across source capture, optional session ID, endpoints,
both ISNs, and client SYN timestamp/packet index. A path alone is insufficient.
Missing, midstream, conflicting, multi-epoch, or unmodeled transport cannot attest to a
Resolved scope. Same endpoints, EntityId, or name with different ISNs remain separated.

Generic DamageEvent provenance intentionally lacks ISNs. Consequently the adapter
attests **only to the exact event instances in its own projection**, not to arbitrary
detached/reprojected objects with matching location hashes. Another decode/epoch can
produce an identical generic Identity without proving transport membership. Detached
copies are Unknown (`UnsupportedEventProvenance`); use the adapter's `Events` and retain
their references. This explicit bridge adds no epoch field to DamageEvent, changes no
Identity formula, and never reconstructs or mutates an event during association.
Repeated references in an input batch are supported and all occurrences retained.

## Temporal and ordering boundaries

Event Timestamp remains **capture arrival**, not execution/cast time or completion.
Binding ValidFrom remains the qualifying **3336 completion**, not CandidateObservedFrom.
No equal-source pre-confirmation event is retroactively Self.

- Start is inclusive: arrival and completion must be at or after ValidFrom.
- EvidenceCoverageEnd is inclusive: arrival and completion must be at or before it.
  Missing coverage, coverage before confirmation, or expansion past selected observed
  coverage fails closed. Coverage is not actor lifetime or metadata capture stop.
- Known ValidUntil is inclusive: arrival and completion must be at or before closure.
  An adapter's observed closure cannot be removed by supplying an open binding.
- Equality at a timestamp still requires provenance order. At confirmation completion
  time, an earlier arrival/completion packet is Unknown. The inbound stream must advance
  past confirmation: later outer frame/stream offsets, or the first differing offset in
  a common container path. Equal locations or parent/child/ambiguous paths are Unknown.
  No record-ID sorting heuristic, time epsilon, or reordered floating-number semantics.
- At an observed RST or acknowledged FIN closure timestamp, packet order also bounds
  availability; a packet after the closure packet is Unknown even at the same time.

A split compressed container may contain an event *after* 3336 in inner order while its
inherited arrival predates 3336 completion. That event remains Unknown. The association
layer does not change the Timestamp contract to create positive Self results.

Confirmation must be the single qualifying 3336 from the adapter's own validated
initialization evidence, with matching raw hash, location and completion provenance.
The supplied binding cannot invent an earlier authority time or a different ID.

## Independence and accounting interoperability

Association reads direct source ID, binding status/ID, transport scope, provenance and
time. It does not use amount, base amount, components, raw skill code, flags, targets,
4536, party records, 4136, 338A, action windows, external names, or controlled actor IDs.
The existing binding model requires CharacterName to construct Resolved; association
adds no name lookup/comparison or name prerequisite beyond that existing model. A future
EntityId-only binding contract would need an explicit model change, not a Phase 3S
resolver fallback. The Phase 3R resolver is unchanged.

Exact duplicate Identity occurrences receive `DuplicateInput` diagnostics; they are
neither dropped nor multiplied into an accounting total here. Detached conflicting
copies remain occurrences but cannot acquire the adapter's transport attestation.
`DamageEventAccountingAudit` independently deduplicates matching copies and rejects a
whole identity with conflicting content. Future composition must validate **all neutral
inputs for conflicting provenance before Self selection**, then intersect those validated
identities with eligible associations and count each provenance once. Filtering first
could conceal a conflicting Unknown copy. No arithmetic or rejection policy changes here.
The authoritative association boundary for future direct-source selection is
`association.Status == Self`; it does not attribute owned/proc/summon damage.

## CLI and validation boundary

```
dotnet run --no-build --project src/Aion2Meter.Replay -- research self-association <capture.pcap> --local IP:port --remote IP:port --json
```

Metadata may supply exactly one selected game connection; otherwise specify endpoints.
No selected connection returns Unknown with an empty audit and does not scan unrelated
All Traffic names. JSON/text retain all association statuses, binding scope/time/evidence,
per-event identity/source/time/diagnostic, and counts. JSON is deterministic for the same
input. `--summary` also retains every association. No self damage total or product metric.

Four fresh identity captures and all 31 old game midstream captures are regressed in
ignored `.tools/tmp/phase3s/` artifacts, alongside the browser negative control. Physical
capture stems/metadata/hashes remain unchanged; semantic corrections are external only.
Fresh experiment ground truth contains no intentional post-binding self-combat. Synthetic
positive Self cases establish mechanical association behavior, not that missing real
validation. Historical controlled skill captures have Unknown bindings and cannot validate
post-binding Self through an old actor-ID annotation.

Phase 3T should validate this existing association boundary using a fresh-entry capture
with complete initialization and a clearly annotated intentional local combat action
after binding confirmation, retaining idle/remote controls and unchanged original files.
That additional controlled capture is needed before claiming real self-accounting
validation; none is requested or taken during Phase 3S. Live App/Npcap, self totals, DPS,
ownership inference, encounter logic and overlays remain outside this implementation.
