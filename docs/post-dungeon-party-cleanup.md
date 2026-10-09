# CURRENT party row reconciliation

Quick Join membership and generic party damage are LIVE VERIFIED by the user in a real five-player dungeon. This follow-up preserves that implementation. The stale-row correction is FIXED / LIVE VERIFIED by the user. Beta.2 ships this verified behavior; no further party protocol change is included.

## Exact owner and physical evidence

The original `Aion2Meter-diagnostics-20261009-193543.json` was read unchanged together with the 193051/193236/193326 exports. In epoch-7, 0092 and independent 4536 correctly establish four remote members. At 16:33:34.0688Z, a 591-byte 0092 body with roster mask 1 is rejected after 25 bytes but formerly clears membership 4 -> 0. A later rejected mask-9 body has length 574. At 16:33:48.820506Z, a complete 303-byte, count-2 roster legitimately establishes Self plus the last remote member. The subsequent 2F92 controls retire runtime bindings and preserve stable membership. The old TCP scope resets.

Epoch-8 independently resolves Self actor 7716 at 16:34:09.717647Z. Its resolver has no remote memberships and no fresh party records in the export. Self attacks at 16:35:29.572883Z and 16:35:29.82486Z are counted; expected remote actor IDs are empty. The resolver does not contain the stale remote member in this scope.

The exact stale owner is `LiveCombatMeter.stableParty`, a bounded presentation identity cache. `UpdateStableParty` correctly retains an old-scope identity when the new scope has no authoritative replacement. The old resolved `Snapshot` path nevertheless appended every absent cached identity to `Members`, marking it active for presentation. `OverlaySnapshot.FromMeter` used that merged list. `OverlayViewModel` was correctly reconciling its input; the stale identity was supplied upstream on every tick. New Self combat and Current reset cleared encounter participants, but not this cache, so neither fixed the row.

## Authority, identity, runtime, history and CURRENT

`PartyAuthorityState` distinguishes Unknown, KnownRoster and KnownEmpty. Missing fresh records, zero runtime bindings, Self resolution, inactivity and scene names do not prove an empty party. Therefore epoch-8 in this export remains Unknown, not a fabricated authoritative disband.

The meter retains `Members` as detached encounter/accounting and carried identity evidence. `CurrentMembers` is a separate projection: independently resolved Self plus the selected scope's validated membership keys and exact corresponding identity. Departed encounter participants and carried-only preview rows cannot supply resolved CURRENT rows. Contribution percentages are normalized over that CURRENT projection. Ready, InCombat, Idle and Current reset use the same rule. Old immutable snapshots/accounting retain already counted participants and damage.

During unresolved transport/runtime handoff, stable zero-damage identity previews remain supported. Once Self resolves, a carried identity lacking current membership proof is withheld from CURRENT, while the stable cache survives Unknown. Fresh validated 0092/0D92/1B92 evidence reconstructs the same party identity; an independent current profile authorizes its runtime actor. The bounded six-entry dormant view-model cache reuses the same presentation object when it reappears and cannot create rows or membership by itself. Future supported damage resumes after the proof boundary; no historical backfill or Other promotion occurs.

Complete validated roster replacement contracts the current party immediately. A fully consumed Self-only roster or the existing independently controlled strict 1392/0000 termination establishes KnownEmpty and removes stale carried membership. A complete but independently contradicted identity claim withdraws eligibility under `IndependentIdentityConflict`, separate from layout rejection. No epoch/dungeon/map clear, timer truth, first-damage binding or name selection was introduced.

## The rejected masks and dissolution boundary

The diagnostic has body lengths, hashes, masks and partial cursor positions, not the actual 591/574-byte bodies. The existing 43-PCAP/45-game-flow corpus does not provide these new complete bodies. Current public source audits do not independently validate their lifecycle grammar. Mask 1 or 9 is therefore still unvalidated; it is not promoted from a numeric mask or the real-world group dissolution observation alone.

Rejected framing/layout now rolls back the bounded membership, active intervals, pending claims, status/invite/join evidence, runtime boundary, replacement revision and authority transaction. `PartyLayout.MutationEffect = NoMutation` explicitly reports this. Diagnostics/rejection counters may advance; trusted membership does not. A later independently validated replacement remains authoritative and can remove members normally. Strict malformed 1392 also has NoMutation. Independent identity conflicts remain a separate fail-closed trust event, never KnownEmpty.

The local controlled dungeon-transition capture contains 2F92 near a legitimate transition, proving it cannot be global disband. Its four-byte shape retires runtime, not stable membership; unmodeled 2192 likewise is not a decoded termination. No exact matchmaking disband variant can be proven from this export. CURRENT is fixed by selected-scope eligibility rather than inventing such a packet meaning. No additional capture is requested for this task.

## Public prior art, read-only audit

- Waffle [2.8.3](https://github.com/Waffle-ens/waffle_meter/releases/tag/2.8.3) explicitly fixes departed members remaining in combat-waiting previews. [2.8.1](https://github.com/Waffle-ens/waffle_meter/releases/tag/2.8.1) replaces previous combat previews with the current party composition and retains old combat in history. At revision `4c58628ac2aa5e70456d4052a717a260174bbf80`, DataManager and PartyRosterStateMachineTests keep current slots separate from retained metadata, remove recognized keys and do not apply an unusable snapshot. These are behavioral parallels; their opcodes, TTL, name fallback and soft-reset clearing are not adopted. MIT source was inspected, not copied.
- Aion2Flow revision `1ba3636b773e1c5f79246855ef239961f6bf70e8`, GPL-3.0: PacketPlayerGroupParser and SceneRuntime identity handling distinguish group relationships from scene entities. Its public party layouts do not prove our new 0092 masks or 2F92 termination. No source, scanner or recovery algorithm was copied, translated or ported.
- a2meter revision `3194fc3d03805397baf671c53b2fbff207baeb37`: PartyStreamParser/PartyTracker separately process framed roster, leave and scene controls. Its configured logical opcodes and reset rules do not validate our physical lifecycle bodies; no parser, clearing rule or implementation was adopted.

## Remote class code 8

Repeated live remote 4536/07 profiles independently expose raw class code 8 at the fixed recognized boundary. Two independent current public mappings agree on Gladiator: Aion2Flow [PacketCharacterClassMapper](https://github.com/cloris-chan/Aion2Flow/blob/1ba3636b773e1c5f79246855ef239961f6bf70e8/src/Aion2Flow.Capture/Streams/PacketCharacterClassMapper.cs) and its [explicit code-8 test](https://github.com/cloris-chan/Aion2Flow/blob/1ba3636b773e1c5f79246855ef239961f6bf70e8/tests/Aion2Flow.UnitTests/Capture/PacketCharacterClassMapperTests.cs), and Waffle's JobClass.ConvertFromCode (5 through 8 => Gladiator). This is a corroborated protocol metadata fact, not inferred from names or screenshots.

Our independently written implementation adds only remote 4536/07 code 8 => Gladiator. Raw code 8 and variant 0 remain in provenance. It does not expand local 3336/37 or 3336/3F authority, unknown variants, membership authority or supported damage categories. Metadata alone cannot add a row or count Other damage. Tests cover profile-before/after-member, current actor metadata, unknown Other, raw provenance and unchanged local authority.

## Boss research boundary

The counted party hits against runtime target 37519 strongly correlate with the user's visible boss fight. This remains a runtime target candidate only. No NPC name, NpcCode, NpcKind, CurrentHP or MaxHP is proven. The physical ID is not in production conditions; no boss UI or HP inference was added.

## Verification and temporary delivery

Fourteen new regression cases supplement the previous 1,177-test baseline. Existing malformed roster/join tests now assert the explicit requested NoMutation contract; former-member UI tests assert CURRENT absence while checking retained accounting. No tests were removed or skipped. The suite covers five pre-combat rows and all remote damage, contraction, retirement, fresh Unknown vs authoritative KnownEmpty, no resurrection after Self combat/reset, opposite persistent party reconstruction, same view-model reuse, strict end records, class metadata and no backfill.

Stress runs 120 mixed scene/reconnect, contraction, last-member termination, rejoin and encounter-reset cycles, followed by 100 unchanged UI ticks with zero collection changes. Existing Quick Join/profile/scene/no-backfill/Other/36 safety tests remain enabled. Membership and stable identity bounds stay five, dormant presentation objects six, closed intervals sixteen, diagnostic ring 256, profile observations sixteen and attribution observations thirty-two. Checkpoint payload and retained party record identities remain zero.

Required restore, Release build, full test counts, package smoke and original-file/source hash audit are recorded under ignored `.tools/tmp/post-dungeon-party-cleanup`. The temporary self-contained win-x64 package is `.tools/tmp/post-dungeon-party-cleanup/publish/Aion2Meter.App.exe`; root publish and the previous live-verified temporary package remain unchanged. No release distribution was created.
