# Generic Self identity continuity

Self initialization and scene recovery use protocol structure and fresh transport provenance. There is no map name input, map whitelist, backend IP whitelist, Abyss branch, first-attacker rule, or timeout that grants identity.

## Evidence and diagnostic limits

The existing frozen research corpus contains 45 game flows. Thirteen fresh flows contain sixteen complete local `3336/37` profiles. In each of the thirteen flows, the first fixed profile's name and canonical leading actor ID independently agree with the earlier `1536` declaration. All sixteen profiles have the supported fixed class/server/faction structure; their selected streams have zero gaps and conflicts. Dungeon entry/exit and the persistent-party teleport sample contain repeated profiles of the same local actor. The opaque four bytes before marker `37` vary across scenes and are deliberately excluded from stable identity.

The supplied production diagnostic identifies a complete `3336` at packet 548 and stream offset 27750, while the resolver reports ambiguous fields/order or multiple initialization sequences. It contains a hash, but no raw bytes, extracted name, actor, class or layout. A hash cannot reconstruct those fields. The old event label “Independent party/profile evidence” was emitted for a published `3336` without proving that the fixed profile decoder accepted it. It does not establish that this particular record uses `3336/37`.

Furthermore, the old ambiguity branch runs only after both initialization families have candidates. Missing `1536` is therefore not established at that point. The earlier “fresh SYN/SYN-ACK missing” snapshot is a temporary candidate state; subsequent decoded initialization and resolver ambiguity cannot originate from the pipeline's permanent midstream-discard path. Backend handoff is observed, but permanent missing handshake or unsafe framing recovery is not demonstrated by this export.

The physical corpus validates the local profile grammar and its relationship to Self. Single-profile startup, reordered declarations, changed actor assignments and incomplete initialization intervals are independently constructed regression scenarios. They are not claimed as recovered bytes from the production diagnostic or physical validation of every map.

## Two separate lifetimes

`StableCharacterIdentity` contains the attested character name, class, server field, faction code and structural fingerprint. It is session-local presentation evidence. The server/faction fields are retained as profile identity facts, without new gameplay interpretation. It is not an account UUID, persistent cross-session cache, runtime actor or damage authorization. A legacy unique pair can retain the name with class unknown.

`CurrentPlayerBinding` represents the current runtime assignment: actor ID, selected transport scope, confirmation provenance, `ValidFrom`, coverage and retirement boundary. A runtime assignment belongs to its proven connection epoch. Old endpoints, ISNs, actor IDs and combat eligibility are not inherited by a new flow.

On a handoff, safely known stable name/class can remain visible in a zero-damage waiting row. The row contains no runtime ID or DPS. Competing resolved flows produce ambiguity and cannot overwrite selected stable identity. Conflicting stable profile facts grant no winner.

## Bounded initialization alternatives

The original unique `1536` followed by compatible `3336` path remains for its established layouts. A separate fixed local profile alternative requires:

1. A complete fresh client SYN, matching server SYN-ACK and completion ACK, valid packet/record provenance, contiguous conflict-free transport and validated application/container framing.
2. A complete inbound `3336` with canonical nonzero actor varint, opaque u32, marker `37`, bounded strict UTF-8 name, nonzero server u16, supported class u32 and accepted faction byte at fixed boundaries.
3. No conflicting stable profile facts or unambiguous contradictory declaration. Generic text matches in opaque bytes cannot substitute for these boundaries.

Live publication additionally requires the existing peer-ACKed complete-frame prefix. This supports the known fixed local profile without requiring `1536` to exist or precede it. Repeated profiles preserve the original confirmation when the assignment is unchanged. A changed actor in a complete compatible fixed profile closes the old runtime interval and confirms the new one. Opaque changes are not identity changes.

A complete but unsupported initialization boundary following an attested local profile can suspend the runtime while retaining stable identity. Its fields grant no actor authority. A later complete supported local profile can establish a new interval automatically. Malformed provenance/framing, conflicting stable facts and resource exhaustion continue to fail closed. Fully midstream flows remain untrusted; no scanning for a convenient frame origin was added.

Previous resolved intervals are bounded to sixteen per retained prefix and are owned by the selected decode. Retirement also checks stream/container ordering, including records with equal timestamps in one packet. Checkpoints retain the current attestation and release retired prefix history after publication; repeated transitions do not accumulate an unbounded actor history.

## Accounting and party safety

Previously published events retain their original actor/provenance. Events before confirmation or between retirement and new confirmation remain Unknown and are never backfilled. An old actor after retirement cannot remain Self. A matching `4536` name, arbitrary Other, summon, first hit or repeated amount cannot establish Self. `0x36` remains Unsupported.

In the same epoch, existing valid contributions stay frozen through runtime replacement; new contributions require new eligibility. A new connection starts its own CURRENT accounting. Remote runtime evidence is retired on accepted local refresh or a supported suspension boundary.

`PartyMemberStableIdentity` separately retains validated names, UUID/token or an epoch-qualified status key, membership provenance and class evidence. A bounded presentation directory preserves already-authoritative active membership names during Self waiting and connection handoff. Waiting rows have zero TOTAL, undefined DPS and no runtime actor. The directory is not queried for combat eligibility, never supplies claims to `PartyRosterResolver`, and never promotes an Other. New application sessions start with an empty directory and discover members through the existing validated `0092`, corroborated `0D92`, or `1B92` plus independent profile paths.

UUID/token identities preserve the same row key across changed runtime IDs and backend scopes. A same-epoch status row can be strengthened by subsequently validated UUID/token evidence without replacing its UI object. Equal names alone cannot select a replacement actor or merge duplicate-name members. Across a connection handoff, fresh membership authority and independent current-scope profile evidence are still required before any remote damage counts. Authoritative replacement/termination removes carried presentation names; unsupported scene controls only retire actors. Changing the independently bound local character discards the previous party presentation.

Class evidence follows the validated stable membership key; a new profile can update it. The party directory is limited to five entries, and UI aliases are independently bounded. No physical capture name, actor ID, UUID, token or map appears as a production whitelist.

## Diagnostics and validation

The bounded 256-transition export now includes complete published `1536`/`3336` observation counts, ordered packet/stream provenance, up to eight candidate name/ID/range hypotheses, structural fingerprints, decisions, stable Self and runtime binding state. Hypotheses remain explicitly unvalidated. No raw packet payload is exported. Live-smoke JSON uses explicit string endpoint projection and includes the compact stable profile.

Regression coverage includes fresh saved-scene startup; absent/reordered `1536`; subzone, town, Abyss, dungeon, instance and teleport transition scenarios; repeated actor replacement; backend handoff; pending runtime and late proof; old actor rejection; same-packet/nested-container ordering; ACK/split-frame gating; no backfill; unsupported layouts; bounded history/diagnostics; conflicting profiles; competing authority; and caller-cloned historical windows. Scene labels exist only in test descriptions, not in production trust inputs.

An unseen future map uses these same evidence rules automatically when it emits a supported protocol layout. A genuinely new layout requires independent protocol validation; the name remains presentation-only while runtime is withheld. No per-map maintenance is required.

## Public behavioral comparison

[Aletheia v9.2](https://github.com/p62003/aletheia_AION2_DPS_Meter/releases/tag/v9.2) reports an Abyss nickname-display fix. Its [current troubleshooting guide](https://github.com/p62003/aletheia_AION2_DPS_Meter/blob/main/Guide_Troubleshoot_EN.md) describes `3336` nickname detection, scene-dependent actor reassignment and a distinction between detected nickname and matched actor. These support investigating separate identity lifetimes, without proving our packet's layout. Their documented attack-based rematching is not used here.

Its [license](https://github.com/p62003/aletheia_AION2_DPS_Meter/blob/main/LICENSE) reserves implementation rights. Only public behavioral documentation was inspected; no implementation source, binary or code was copied. Aion2Meter's implementation and synthetic fixtures use its independently validated corpus and existing parsers.
