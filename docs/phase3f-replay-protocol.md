# Phase 3F replay protocol foundation

The protocol layer is confined to `Aion2Meter.Replay.Research`. It does not perform live combat accounting, DPS, identity resolution, skill normalization, summon ownership or gameplay flag interpretation.

```
dotnet run --project src/Aion2Meter.Replay -- research decode captures/session.pcap --json
dotnet run --project src/Aion2Meter.Replay -- research containers captures/session.pcap --json --summary
```

The companion session JSON must identify exactly one TCP game connection on remote port 13328, or pass both `--local IP:port --remote IP:port`. Explicit endpoints support other ports. Full JSON retains all records, unknowns, malformed tails, candidate fields and container paths; raw bytes are base64. `--summary` deliberately omits raw/candidate rows and retains summary/container audit counts. CLI output is console-only: research dumps belong in ignored `.tools/tmp`, never in Git.

## Framing and provenance

One independently written bounded unsigned base-128 reader serves field and length decoding. It supports up to ten bytes/uint64; framing uses the canonical five-byte/uint32 subset. The observed framing arithmetic is `V + prefixWidth - 4`. Contiguous-run starts remain candidate boundaries. There is no padding skipping, optional F0..FE guessing, or bytewise resynchronization. TCP gaps and conflicting overlaps retain the existing fail-closed extraction rules.

Every nested record inherits the original capture/direction/outer TCP offset, source and completion packet timestamps. `ContainerPath` identifies each parent record and its decompressed byte-space offset. Inner offsets must not be presented as physical TCP offsets. A malformed remainder is retained as its own raw record. Unknown opcodes remain unknown. Existing credential heuristics suppress raw bytes/candidates for the entire affected direction, including credential-like decompressed material; suppression is explicit.

## LZ4 dependency and safety

Dependency: **K4os.Compression.LZ4 1.3.8**, via NuGet; [package](https://www.nuget.org/packages/K4os.Compression.LZ4/1.3.8), [upstream MIT license](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/master/LICENSE). Copyright (c) 2017 Milosz Krajewski. No reference-meter source is copied. This package supplies raw block decompression, not a gameplay parser or an LZ4 framed-stream reader.

The restored package's version-1.3.8 NuGet metadata points to the upstream MIT license. Package references and lock files retain dependency provenance; retain applicable upstream license notices when distributing dependency binaries.

Containers are only recognized at an extracted body boundary: `FF FF`, uint32 LE declared size, raw LZ4 block. Exact decoded size is required. Named configurable defaults:

| Limit | Default |
|---|---:|
| Output bytes per container | 2 MiB |
| Container depth | 4 |
| Attempted output bytes per outer frame | 8 MiB |
| Attempted output bytes per capture | 128 MiB |
| Frames per container | 100,000 |
| Records per capture | 500,000 |
| Individual framed record | 1 MiB |

Output/total limits are checked before allocation; failed attempts consume budgets. The same framing reader handles decompressed content, with bounded nesting. Invalid compressed data, size mismatches, bounds, and inner framing remainders remain explicit. A capture record bound aborts with an error rather than returning a silently incomplete success. CLI knobs are `--max-output-bytes`, `--max-depth`, `--max-total-bytes` (per outer frame) and `--max-inner-frames`.

## Candidate scope

Only tag `04 38`, switch/category `06/26`, type `02/03`, and the observed category-6 layout guards are supported. Cursors follow variable-width target, category, unknown, actor, type, zero, pre-value, aggregate and component varints. Unknown/skipped regions are retained. No global offsets 29/31, two-byte formula or per-sample exception is used. Optional list presence is limited to switch `26`; its count is bounded to 256. Unexpected layout/trailing bytes are unresolved.

`AggregateAmount` remains the observed final-value candidate. `DerivedBaseAmount = AggregateAmount - sum(OptionalComponents)` only after successful validation. Sum overflow or sum greater than aggregate preserves the aggregate and fails derived-base calculation. Components are neutral evidence and are never added to aggregate. Raw IDs, uint32 skill codes and modifier/type/direction candidates remain uninterpreted. No names, Critical/Double/Perfect/Front booleans or DOUBLE multiplication are emitted.

Tests use synthetic independently constructed records and LZ4 fixtures, never real PCAP data. Controlled validation and per-capture audit results are recorded locally in ignored `.tools/tmp/phase3f/report.md`.
