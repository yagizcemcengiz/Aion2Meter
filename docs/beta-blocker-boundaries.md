# Checkpoint and scene-binding failure audit

These are independent failures. Checkpoint trust never establishes actor identity; matching a character name never validates stream/container boundaries.

## Checkpoint boundary

Live publication selects only the contiguous, peer-ACKed complete outer-frame prefix at the established cursor. Partial length varints, application frames, outer container headers and compressed payloads remain bounded unpublished transport bytes. They do not revoke the previously published prefix. Completing bytes cause a new shared decode and exactly-once publication. No byte scanning, resynchronization or timer-based trust is added.

The boundary reader now reports `Complete`, `Waiting` and `Invalid` explicitly. An invalid canonical length at the established ACKed boundary fails closed, rather than being retained indefinitely as if it were incomplete. A complete outer container that fails LZ4 or inner framing is also invalid: later TCP bytes cannot extend an outer frame whose declared length is already fully present. Privacy suppression remains an independent reason to withhold publication. Real conflicts and resource exhaustion retain their existing safety behavior.

The supplied long-session export recorded only the broad “Unvalidated application/container prefix cannot be checkpointed” exception. That old exception combined `Suppressed`, `FailedContainer`, `ContainerWithUnparsedBytes`, `MalformedFraming` and `MalformedInnerFraming`. It did not record which condition occurred, the cursor or the offending container audit. The export therefore proves the observed failure, but cannot determine whether that exact failure was a valid safety rejection or an unmodeled framing condition. Zero TCP gaps/conflicts does not prove valid application or decompressed-container content. The last `0036` activity is a record tag, not evidence of the later fault's cause; it is distinct from combat category `0x36`.

Diagnostic entries now include checkpoint/frame offsets, unpublished counts, expected/available prefix and frame bytes, container stage, Waiting/Invalid, resolved-waiting transitions, and the irreversible invariant. Complete-invalid container exceptions include the actual decoder status and warning. No raw bytes are exported; all states are bounded scalars and the existing 256-entry ring remains.

## Scene binding

In the new-scene export, the new `1536` hypotheses contain no agreement with the new leading actor/name hypothesis from `3336`. The fixed local profile path also rejected the latter. The legacy generic ambiguity message covers zero qualifying pairs as well as ambiguous/multiple pairs. Stable presentation therefore survived, while no runtime authority was established.

The exported actor and name offsets match the accepted fixed profile's positions. They do not reveal the marker, server pair or class code. The export cannot establish which profile field differs. A re-audit of the existing 45-flow corpus found sixteen local profiles, all using the supported `3336/37` structure, and no matching raw record for the new-scene hash. No second profile layout was invented or promoted from name-only evidence.

Fixed profile inspection now exposes the first rejected boundary, accepted/unsupported layout type and deliberate marker/server/class/faction scalars when their positions are supported. Unsupported markers stop before interpreting hypothetical alternate-layout class fields. Runtime confirmation/retirement validity and the preceding selected actor are diagnostic facts only. Stable Self and party presentation, independently validated membership, future-only combat eligibility and duplicate-name protections remain as described in [scene identity continuity](scene-self-identity.md).

The Self UI key now follows stable name/server/faction presentation rather than epoch/actor. The same view row survives waiting and a compatible independently validated rebind; a different character receives a different key. This UI key is never used by the decoder, resolver or combat association.

Both physical failures still require the missing precise facts from an updated bounded diagnostic to justify a physical `FIXED` claim. Synthetic tests prove the supported mechanisms and rejection boundaries; they do not reconstruct the original exports' missing bytes.

## Public behavioral prior art

[Aletheia v9.2](https://github.com/p62003/aletheia_AION2_DPS_Meter/releases/tag/v9.2) reports scene-dependent nickname loss and a transport reassembly correction for large compressed messages split across TCP segments. Its [current guide](https://github.com/p62003/aletheia_AION2_DPS_Meter/blob/main/Guide_Troubleshoot_EN.md) separates nickname detection from runtime actor matching. These are relevant failure modes, not proof of our failing frame or profile layout.

[a2meter v1.2.6](https://github.com/a2meter/Aion2Meter/releases/tag/v1.2.6) describes redundant buffering of already-framed processor output. [v1.2.7](https://github.com/a2meter/Aion2Meter/releases/tag/v1.2.7) describes keeping roster names aligned with zone-scoped actor IDs. Our code uses one shared framing/decompression stack and independent runtime bindings. Only public release notes/documentation were inspected; no external implementation was copied.

[Waffle's changelog](https://github.com/Waffle-ens/waffle_meter/blob/main/README.md) reports the closest matching symptom: v2.8.0 corrects missing Self damage after zone/re-entry transitions, v2.2.0 addresses late instance identity, and v2.5.3 addresses capture queue loss at entry. Its current [MIT license](https://github.com/Waffle-ens/waffle_meter/blob/main/LICENSE) and [third-party notices](https://github.com/Waffle-ens/waffle_meter/blob/main/THIRD_PARTY_NOTICES.txt) were checked before inspecting code.

Current [DataManager](https://github.com/Waffle-ens/waffle_meter/blob/main/dotnet/src/WaffleMeter.Data/DataManager.cs) stages a Self candidate using an exact existing nickname/server relationship from member metadata, then requires that same actor's activity with collision/mob/summon checks before promotion. [Identity tests](https://github.com/Waffle-ens/waffle_meter/blob/main/dotnet/tests/WaffleMeter.Data.Tests/UserIdentityResolutionTests.cs) cover refreshed actor IDs and bounded stale identities. This is more specific than choosing the first attacker. Its metadata grammar and uniqueness assumptions are not independently proven in our failing scene, so that promotion path, UID cap and activity-triggered authority were not adopted. The separately stable presentation/runtime lifetime principle applies; implementation code was not copied.

Waffle's [accumulator](https://github.com/Waffle-ens/waffle_meter/blob/main/dotnet/src/WaffleMeter.Capture/PacketAccumulator.cs) also documents snapshot-buffer resets. Our diagnostics do not establish such loss, and our bounded pending-tail stress passes; no speculative capture-buffer increase or dropped-byte recovery was added. Public source/issues/releases were inspected, without treating their protocol assumptions as our evidence.
