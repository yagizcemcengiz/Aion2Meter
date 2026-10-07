# Phase 3J — offline action-window / record-group correlation

This is a development/research contract in `Aion2Meter.Replay.Research`, not a stable gameplay API. It operates **after** the existing bounded replay decoder. It does not modify framing, LZ4, amounts, raw codes, identity promotion, metadata providers or the live UI. No skill-name database, DPS accounting, cast deduplication, ownership, flags or DOUBLE arithmetic is introduced.

**Correlation does not establish gameplay semantics.** A group is not automatically one skill cast. One anchor can have zero, one or many records; one externally described manual phase can have several raw codes. Repeating a code does not repeat a manual input. All original record amounts/components and provenance remain separate.

## Research contracts

- `ResearchActionAnchor`: capture ID, unique anchor ID, timestamp, source (`OutboundSequence`, `UserActionCue`, `ManualAnnotation`, `ImportedResearchAnnotation`), optional packet index/sequence packet indices, external label and annotation. A capture timestamp/cue is not an inferred physical keypress time. Supplied anchor IDs must be unique within a capture.
- `ManualActionObservation`: user/imported manual skill/phase labels, nullable proc observation, visible observations, confidence and notes. These never enter protocol decoding or `ISkillMetadataProvider`.
- `ResearchActionWindow`: anchor, previous/next anchors, bounds, optional source/target constraints, ordered response records with anchor deltas, auxiliary observations, other-tuple background records, groups, visual comparisons and warnings. Source/target constraints can be supplied globally or overridden per anchor. Numeric IDs are never global character identities.
- `CombatRecordGroup`: one actual source/target tuple inside a window. It retains ordered `SupportedCombatRecord` objects, separate amounts and component lists, contextual auxiliary edges and tokens. This is the neutral **network response group**, not a `SkillCastEvent` or damage sum.
- `DifferentialAssociation`: exact presence, multiplicity and ordered subsequence differences between two externally labeled trial groups, with per-record amount/raw-flag/auxiliary/timing distributions. The computed evidence class A describes demonstrated comparison facts, not semantic skill identity.

Contracts currently use the same public visibility as the existing replay research models to allow tests and CLI serialization; they are not exposed through Core or the diagnostic app.

## Window policy and record order

`ActionWindowPolicy` defaults:

| Policy | Default |
|---|---|
| BeforeMilliseconds | 0 |
| AfterMilliseconds | 4000 |
| CapAtNextAnchor | true |
| AuxiliaryProximityMilliseconds | 3000 |

Window bounds are **start-inclusive/end-exclusive**. Start is anchor minus lookback; end is anchor plus response duration. With next-anchor capping, end is the earlier of that end and the **next window's start** (next anchor minus lookback), so positive lookback does not duplicate ownership. Equal anchor timestamps can produce an explicitly warned empty window; anchors are not silently merged. Imported anchors sort by timestamp, packet index when present, then input order.

Setting `CapAtNextAnchor=false` permits overlapping associations. Every record preserves all window memberships. Windows contain association copies of records; `AllRecords` retains the complete supported inventory once. No record is removed merely because its arrival is outside a short presentation window. In particular, a record slightly after three seconds remains inside the default four-second window and remains in `AllRecords` even with a shorter configured policy.

All durations must be finite, nonnegative (after strictly positive), at most one hour. Existing replay memory limits remain active. At most 10,000 anchors, 1,000,000 inside-window associations, 1,000,000 auxiliary edges and 100,000 ordered pattern pairs are accepted; limit failures are explicit rather than silent truncation.

Combat arrival order is timestamp → physical packet index → outer frame byte offset → lexicographic nested container offsets (outer path before descendants when otherwise identical) → decoder-stable record ID. Code, amount and token never affect ordering. Container/packet/raw bytes and completion timestamps remain on each record. A byte token's numerical order is not chronology.

## Set, multiset and ordered pattern

Each group derives `ExactOrderedCodes`, `CodeMultiset`, `UniqueCodeSet`, `RecordCount`, `ArrivalSpanMilliseconds` and zero-based `RepeatedCodePositions` from its records. These are independent representations. Plain sets deliberately do not replace ordered lists/counts. There is no aggregate amount for a group.

## Auxiliary evidence

The independent research decoder uses bounded canonical varints and verifies the complete framing boundary/tag before parsing:

```text
0238: source → zero prefix → code u32LE → token byte → mode varint → target → raw remainder
0338: source → zero prefix → target → code u32LE → token byte → raw remainder
0638: source → code u32LE → token byte → raw remainder
```

Only server-to-client records with these tags are considered. Nonzero prefixes, truncation and unverified boundaries produce `AuxiliaryDecodeIssue` objects retaining raw records. Unknown suffixes/modes have no gameplay names.

`AuxiliaryRecordEdge` requires **same capture + source + exact raw code + token + target when available + configured time proximity**. `DeltaMs` is auxiliary arrival minus combat arrival. A token alone never identifies an event/cast. Missing target in 0638 remains null, not inferred. Multiple matches stay multiple edges; matching is not a combat acceptance or deduplication condition. Evidence outside the response window is retained if it links a selected record. All parsed auxiliary observations and issues remain in the full result.

No tags are called CastStart/CastEnd/Projectile/Proc. A recurring companion is a comparison pattern; a direct/linked/passive/proc mechanic needs independent evidence.

## Background classification

`AllRecords` contains every supported combat record, including outside/background records. Each has global context classification and every inside-window association:

- `ControlledTupleInsideWindow`: at least one explicit constrained window matches.
- `ControlledTupleOutsideWindow`: outside all windows, matches an explicitly supplied context.
- `OtherTupleInsideWindow`: inside a constrained window but fails its source/target constraints.
- `OtherTupleOutsideWindow`: outside all windows and fails all supplied contexts.
- `Unresolved`: no controlled context was supplied. Unconstrained windows still expose groups by actual tuple; they do not guess a controlled actor.

A single source or target constraint applies only that dimension, with a partial-context warning. Without anchors, global source/target constraints still classify outside records. Unknown/unsupported combat candidates remain separately in `UnresolvedCombatCandidates`, with raw bytes, partial fields and warnings; they are not dropped or assigned to a skill.

When overlapping windows have different contexts, memberships retain each classification; the global classification prefers a demonstrated controlled-inside association, then unresolved-inside, then other-inside. A global summary does not replace per-window evidence.

## External visual annotations and correction history

`VisualDamageObservation` keeps nullable final amount, base, ordered component list, position/flag observations, confidence/notes and correction history. Unreported base/components/flags stay null. An empty component list means an explicit empty observation; null means unknown. Visual flags exist **only in external research annotations**; supported network records retain Type/Modifier/Direction raw candidates and have no `IsCritical`/`IsDouble` etc.

Optional `RecordId`, `GroupRecordPosition`, exact raw code and source/target selectors narrow context before numeric comparison. `GroupRecordPosition` indexes the window's constrained ordered response records, zero-based, before separating tuples into subgroups. Use source/target or exact record IDs when a window has multiple tuples. Supplied positions/IDs are external structural assertions, not decoder filters.

Matching statuses:

| Status | Meaning |
|---|---|
| Exact | One contextual candidate equals every known numeric observation |
| StructuralOnly | One contextual candidate; no numeric values supplied |
| Ambiguous | Multiple exact candidates or multiple candidates without numeric evidence |
| Unmatched | No candidate, or no numeric equality without an explicit position/record ID |
| Contradicted | Explicit position/record ID identifies a contextual record whose known numbers disagree |

No nearest-value matching, unknown-value inference, flag conversion or decoder change is performed. All observations, statuses and candidate record IDs remain visible. Matching is per observation; it does not require one floating UI number per network event or consume/deduplicate damage records.

Corrections are explicit external observations with retained `ResearchAnnotationRevision` history: field, previous/corrected text, provenance and optional known timestamp. An update does not rewrite network bytes or silently discard the historical note.

## Differential comparison

Two labeled, nonempty trial lists are required; a **trial with zero response records is valid** and remains in denominators. Duplicate/overlapping capture+anchor trial selections are rejected. Labels and annotations are external; the engine does not recognize skill/phase names or raw code families.

The engine computes:

- intersection of positive code sets, and candidates present in every positive/absent from all negatives;
- codes shared between groups and mixed-presence ambiguous codes;
- per-code positive/negative min/max record multiplicities; a consistent increase requires positive minimum > negative maximum;
- distinct exact ordered patterns/counts and exact negative-pattern subsequence comparisons; incomparable orders stay explicitly non-subsequences;
- separate amount lists, raw flag combinations/counts, auxiliary edge/record footprints and anchor-relative timing ranges for each group.

These outputs never assert a canonical SkillId, manual second input or linked-effect subtype. A code occurring in all explicit first-phase controls cannot become later-phase-exclusive through this comparison. Overlapping response windows may intentionally repeat an association across trials; this is research membership, not accounting.

## CLI and definitions

All commands are console-only and support `--json` (enums are strings, raw bytes base64). `record-groups` and `action-windows` intentionally expose the same complete evidence envelope so background and unmatched evidence is available in either view.

```powershell
dotnet run --project src/Aion2Meter.Replay -- research action-windows captures/session.pcap `
  --sequence 40,50,60 --source 123 --target 456 --after-ms 4000 --json

dotnet run --project src/Aion2Meter.Replay -- research record-groups captures/session.pcap `
  --definition .tools/tmp/trial-definition.json --json

dotnet run --project src/Aion2Meter.Replay -- research compare-groups .tools/tmp/comparison.json --json
```

Without explicit `--local/--remote`, companion metadata must identify exactly one TCP port13328 connection, consistent with the existing decode CLI. `--sequence` is **caller supplied**; no known skill sequence is built in. Anchors use the first matching outbound packet, retain all sequence packet indices and get IDs `sequence-1`, etc. `--include-cues` imports actual metadata UserActionCue timestamps as separate anchors (`cue-<guid>`); absent cues are not fabricated. When both types are supplied, both remain anchors; explicitly choose a definition if they represent the same research action.

CLI overrides: `--before-ms`, `--after-ms`, `--auxiliary-ms`, `--no-next-cap`, `--sequence-window-ms`, `--source`, `--target`. A definition capture must match the capture argument. Paths for captures/comparison references resolve relative to the containing definition file. Unknown JSON fields are rejected to catch misspelled research settings.

Example neutral action definition:

```json
{
  "Capture": "../../../captures/session.pcap",
  "SourceEntityId": 123,
  "TargetEntityId": 456,
  "Policy": { "AfterMilliseconds": 4000, "CapAtNextAnchor": true },
  "Anchors": [
    {
      "AnchorId": "trial-1",
      "Timestamp": "2026-01-01T00:00:05Z",
      "AnchorSource": "ImportedResearchAnnotation",
      "ExternalAnnotation": {
        "ManualSkillName": "User-supplied label",
        "ManualPhaseLabel": "First manual action only",
        "VisualDamageGroups": [
          {
            "ObservationId": "visual-1",
            "GroupRecordPosition": 0,
            "FinalAmount": 100,
            "Confidence": "High"
          }
        ]
      }
    }
  ]
}
```

Omitted anchor CaptureId is populated from its definition's capture; explicitly conflicting capture IDs are rejected. Capture aliases/semantic labels annotate external research, without renaming original metadata/PCAPs.

Comparison input selects explicit anchor IDs, with the positive group first and the control group second:

```json
{
  "Groups": [
    { "Label": "observed positive", "Trials": [
      { "Definition": "positive.json", "AnchorIds": ["trial-1", "trial-2"] }
    ] },
    { "Label": "observed control", "Trials": [
      { "Definition": "control.json", "AnchorIds": ["trial-1", "trial-2"] }
    ] }
  ]
}
```

Keep real-capture definitions, annotations, outputs and comparison scripts under ignored `.tools/tmp/`; real PCAPs are not test fixtures. Unit tests use independently constructed synthetic records/PCAPs. Runtime helpers never write or rename original captures/metadata. Metadata connection inference is selection context, not a skill database.

## Scope and next research

Phase3I code sets/labels/amounts/IDs live only in ignored external definitions. The generic foundation can reproduce those controls without embedding them in reusable logic. Evidence of shared codes/multiplicity explicitly preserves unresolved mechanisms and rejects later-phase exclusivity when the controls also contain the code.

Recommended Phase3K: **replay pattern repeatability and differential evidence audit**, using more than one independent session and held-out controls. Compare exact contextual patterns across captures, keep partial/negative/contradictory evidence, report trial denominators, and audit stability of auxiliary/timing footprints before any semantic promotion. This should remain replay research, with no automatic skill mapping or live accounting.

If a future task specifically wants manual second-input causality, collect matched groups of three synchronized first-action-only trials and three explicitly timestamped first→second manual activations, same character/equipment/target/conditions. That future differential is not required for Phase3J implementation.
