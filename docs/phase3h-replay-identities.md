# Phase 3H replay identity correlation

The identity layer runs only in `Aion2Meter.Replay.Research`, after the existing bounded framing/LZ4 decoder. It preserves observed names, neutral related-ID candidates and separate context labels. It does not assign player/owner/account types, infer classes or parties, normalize skill codes, interpret gameplay flags, or perform live combat accounting.

```powershell
$capture = 'captures/session.pcap'
dotnet run --project src/Aion2Meter.Replay -- research identities $capture --json
dotnet run --project src/Aion2Meter.Replay -- research id-graph $capture --json
dotnet run --project src/Aion2Meter.Replay -- research skills $capture --json
```

Set `$capture` to the actual file path. Endpoint selection and safety options match `research decode`: companion metadata must identify exactly one TCP port-13328 connection, or supply both `--local IP:port --remote IP:port`. `--summary` returns counts without evidence rows. Default text output summarizes observations, names and correlations; JSON contains complete structured values, base64 raw records, observation links and provenance. JSON dumps belong in ignored `.tools/tmp`, never in Git.

## Observation boundaries

`4536` uses the verified leading varint ID, unknown u32LE mask, observed presence byte `07`, one-byte UTF-8 **byte** length, exact UTF-8 text and unresolved remainder. Invalid UTF-8, truncation or inconsistent framing returns an issue with the original record. An unverified presence branch retains an unnamed unresolved observation; it is not parsed by reference-only mask assumptions. No ASCII conversion, trimming, case folding or Unicode normalization occurs.

`4136` preserves an unnamed header entity for the observed kinds `5F/0D/1D/1F`. Two independent relationship shapes are retained: `FF × 8`, eight unknown bytes and a canonical varint; and the kind-5F `07 02 06` suffix with u32LE related ID, auxiliary value, zero padding, u16 context candidate and length-prefixed UTF-8 label. Self-ID anchors are valid observations and do not replace the distinct suffix edge. Kind meanings and gameplay ownership remain unresolved. An inline readable label in the remainder is not assigned as the entity's name.

`338A` has its own `TextContextObservation`: leading ID, auxiliary u32LE, two zero bytes, u16 context candidate, one-byte UTF-8 byte length/text and raw remainder. Its text never enters the character-name directory. A context label and a character name may differ for the same entity.

Only verified inbound envelopes are interpreted. Outbound tag collisions, unknown layouts and existing suppressed records do not produce names. Suppression and inherited container offsets/timestamps remain governed by the Phase 3F decoder.

## Capture scope and conflict rules

`ReplayIdentityDirectory` belongs to exactly one `CaptureId`, the source capture path selected by the replay command. A mixed-capture observation list is rejected. Equal numeric IDs or equal names in separate captures do not create a global identity. Duplicates remain observations and graph edges.

- `GetObservations(id)` returns all header/name observations, including duplicates.
- `GetLatestPrecedingName(id, timestamp)` considers only named observations at or before the combat timestamp. The latest eligible observation timestamp is exposed, but multiple distinct eligible names produce `Conflict`, not a silent last-write selection.
- `GetRetrospectiveSameCaptureNames(id)` considers the complete same-capture observation set, including later records. Its mode is explicitly `RetrospectiveSameCapture`.

Both lookup results carry `Unknown/Resolved/Conflict`, all exact names, observation IDs and latest eligible timestamp. A convenience `Name` exists only for a single resolved name. A future conflicting name does not rewrite an earlier preceding-only result; retrospective lookup reports that conflict. Timestamp equality means simultaneous arrival evidence, not proven server/animation order.

## Supported combat projection

`SupportedCombatRecord` promotes `TargetEntityId`, `SourceEntityId` and exact u32LE `RawSkillCode` only after the complete supported category-6 grammar has passed. It preserves aggregate, derived base, optional components, candidate flags, unknown regions, terminal bytes and the original raw/provenance record. Alignment and arithmetic remain unchanged.

The lower-level `RawCombatCandidate` retains nullable candidate fields so partial/unsupported parsing can still be inspected and prior decode output remains compatible. It is not the promoted model; unsupported candidates never enter `SupportedCombatRecords` or identity enrichment.

`CombatIdentityCorrelation` exposes separate preceding and retrospective source/target name results. Identity and relationship links refer to **all exact same-capture structural matches**; their raw observations supply individual timestamps. Those structural links are not implicitly preceding-only or ownership edges. Context labels do not resolve names. Correlation confidence B/HIGH describes the supported field roles, not the existence of a name: unknown/conflicting names remain explicit states.

An entity can be source, target, both or neither. No PlayerSource/NpcTarget/SummonSource subclass is introduced. `AggregateAmount` is never augmented by optional components; `DerivedBaseAmount` retains the validated subtraction.

## Graph and skill inventory

The graph emits capture-scoped entity nodes, exact name edges with observation IDs, separate related-ID candidate edges and context-label edges, duplicate groups and conflicts. Neutral relationships never roll amounts up to another entity.

The skill command groups **exact** raw u32 codes, showing source/target IDs, occurrence count, aggregate minimum/maximum, component-tail row/value counts, raw type/modifier/direction combinations and explicitly retrospective identity results. Ordered supported records and their correlations remain in JSON for sequence/timing research. It does not treat a sequence of codes as a named skill or a hit count.

`17010020`, `17010240`, `17010340` and `17730001` remain separate. No division, rounding or suffix stripping occurs. `ISkillMetadataProvider` is optional; the default `EmptySkillMetadataProvider` always returns no metadata. A future provider may return versioned presentation fields, but must preserve the exact requested raw key. Mismatched raw keys are rejected. No external data, web lookup or skill database is bundled or required.

## Validation

Synthetic tests cover byte bounds, variable-width IDs, strict UTF-8, raw remainders/container provenance, duplicates/conflicts, temporal lookup, capture isolation, self-ID ambiguity, label/name separation, source/target roles, raw-code preservation, empty metadata and CLI replay. Real captures are not committed as fixtures. Local corpus findings and validation expectations are recorded in ignored `.tools/tmp/phase3h/report.md`; expected counts are not parser rules.
