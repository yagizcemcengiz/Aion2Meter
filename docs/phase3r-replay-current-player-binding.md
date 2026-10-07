# Phase 3R: conservative replay CurrentPlayer binding

This implements the Phase 3Q evidence-backed contract for complete selected fresh replay TCP epochs. It does not establish universal initialization grammar or generalized live lifecycle semantics. Nothing is wired into the App, capture engine, DamageEvent projection/accounting, self totals, DPS or overlay.

## Evidence basis and independence

Controlled reciprocal recipient perspectives established that the name/numeric relationship in inbound `1536`/`3336` follows the recipient's current character. Known remote characters were independently represented by the existing `4536` name envelope and absent from those initialization tags. A shared bounded non-combat entity/name substructure corroborated the runtime numeric slot against independent remote mappings.

Those controls validate the extraction contract; they are **not required resolver inputs**. No remote character, `4536`, `4136`, `338A`, party record, skill metadata, external name annotation, action window or DamageEvent is needed. Initialization is resolved without name/ID/timestamp/filename expectations.

## Models and scope

Core defines immutable `CurrentPlayerBinding`, `CurrentPlayerBindingStatus`, `ReplayConnectionScope`, `CurrentPlayerBindingEvidence`, `BindingByteRange` and `BindingContainerLocation`. Status is `Unknown`, `Resolved` or `Conflict`. Non-resolved results never expose an authoritative EntityId, CharacterName or lifetime.

Scope retains replay source, optional metadata SessionId, selected local/remote endpoint strings, client/server ISNs and client SYN timestamp/packet index. Endpoints alone, numeric EntityId and PID are not epoch identities. Bindings never carry across invocations or epochs, even when endpoints, names or actor numbers repeat. Filesystem SourceCapture is a replay audit identity, not the eventual live identity design.

Evidence contains arrival/completion timing and packets, stream/outer/container locations, raw field ranges, representation, exact numeric/name bytes, immutable Base64 raw record snapshot, SHA256 and a versioned provenance identity. Collections own read-only snapshots. `Evidence` retains all supported text/numeric hypotheses; `QualifyingEvidence` identifies the unique matching pair only for a Resolved result.

## Supported bounded extraction

`LocalInitializationExtractor` requires inbound `1536` or `3336`, complete canonical application framing, matching raw tag/length/prefix and no decode warning. It scans record bounds rather than universal observed byte offsets:

- `1536`: a nonempty byte-length-prefixed strict UTF-8 text hypothesis with immediately preceding u32LE numeric bytes.
- `3336`: leading canonical unsigned base128 varint using `UnsignedVarint`, followed by bounded byte-length-prefixed strict UTF-8 hypotheses. Intervening bytes retain no assigned semantics and remain in the raw snapshot.

The conservative text profile excludes control characters, replacement characters and whitespace-only strings; spaces/punctuation and multibyte UTF-8 are retained without normalization. This profile is not a universal game name grammar. Byte length is not .NET character count. Numeric zero is retained and is not treated as a sentinel. A resource limit of 4,096 hypotheses fails closed with no partial selection; it is not a protocol field/cardinality rule.

Each record can have multiple text hypotheses. A pair qualifies only when exactly one raw field pairing agrees on numeric value **and ordinal exact name**. Unmatched lexical hypotheses are not guessed into binding fields. A unique common value/name is required, not an expected name, majority vote, frequency ranking or magic elapsed-time threshold.

## Resolution policy

`ReplayCurrentPlayerBindingResolver` is stateless per invocation. `Analyze` uses existing selected-stream reassembly and protocol/container decoding; `Resolve` accepts that replay's generic raw records. It validates:

1. Observed client SYN, matching server SYN/ACK and completion ACK. The completion ACK may carry data.
2. One modeled TCP epoch, selected endpoint/direction/packet provenance, SYN-based stream origins, no truncation/gap/conflicting overlap. Identical retransmissions are allowed.
3. Complete inbound framing/container coverage; suppressed/failed/unparsed inbound regions cannot hide another initialization.
4. Matching capture source and observed packet/completion provenance, and verified ancestor container locations for initialization.
5. A unique inbound `1536`, then later qualifying `3336` in stream/provenance order, with exact numeric/name agreement and completion known.

Same provenance presented twice is deduplicated. Different provenance remains independent evidence, even if bytes/values match. Same provenance with incompatible supported bytes is Conflict. Multiple distinct initialization sequences are not treated as an invented character switch: incompatible qualifying assignments are Conflict; repeated compatible or otherwise ambiguous sequences are Unknown.

Unknown covers no selected flow, missing handshake/tag/provenance, midstream data, unsupported/malformed/invalid UTF-8/noncanonical data, gaps/truncation, reversed order, ambiguous raw fields or unmodeled compatible sequences. Conflict covers unambiguous contradictory tag assignments, incompatible qualifying pairs and conflicting supported same-provenance bytes. All available contradictory evidence is retained; neither result silently chooses a best player.

## Timing, closure and evidence coverage

`CandidateObservedFrom` is the qualifying `1536` observation timestamp. It is not authority. `ValidFrom` is the complete matching `3336` record/container completion timestamp, never its first packet or a UI observation. Records before confirmation are not retroactively bound.

`EvidenceCoverageEnd` is the last observed selected connection packet. Capture stop is not character despawn. While an epoch remains open, `ValidUntil` is null. A observed RST, or both observed FINs with their corresponding acknowledgments, can bound connection validity. A single FIN is only a half-close. These connection signals do not establish game logout/despawn/zone semantics.

New epochs cannot inherit binding. Character reset, zone change, despawn and actor reuse remain unmodeled. Midstream or additional unmodeled initialization cannot be promoted. No Class/Ownership fields are added; no owned entity roll-up or live state machine is implemented.

## CLI and regression

```powershell
dotnet run --no-build --project src/Aion2Meter.Replay -- research self-binding <capture.pcap> --local IP:port --remote IP:port --json
```

Text and JSON output expose status, scope, identity, timing, candidate/qualifying evidence and diagnostics. With no endpoints, existing CLI convention selects only an unambiguous game connection in companion metadata. Otherwise `self-binding` returns Unknown with a no-selection diagnostic; it never scans unrelated All Traffic strings. All Traffic fresh entries generally need explicit discovered endpoints. Missing files/invalid arguments remain CLI errors. `--summary` uses the binding JSON view, retaining audit evidence rather than hiding fields.

Real capture regression artifacts remain ignored. All four independently researched fresh entries resolve correctly; all 31 older midstream game captures remain Unknown; browser/no-selection control remains Unknown. Synthetic tests cover canonical widths including one through five-byte u32 identities and ten-byte varint extraction, changed positions/byte lengths, zero/ambiguity, multibyte UTF-8, completion/container provenance, conflicts, scope separation, immutable snapshots, retransmission, close/coverage and CLI behavior. No real PCAP fixtures are committed.

## Next integration boundary

Phase 3S should add a **replay-only event/binding association audit** using explicit scope and `[ValidFrom, coverage]` bounds, preserving neutral events and marking pre-confirmation/out-of-coverage/conflicting association Unknown. Do not add live binding, self totals, DPS, ownership, encounter timers or overlay until that association boundary is independently validated.
