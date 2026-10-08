# Live party status and direct class profiles

This change preserves the uncommitted product stack at HEAD `140f932` on
`phase-3-protocol-research`. No captures are requested, modified or committed.
The new implementation and synthetic fixtures were independently written.

## Evidence and public prior art

Current revisions were checked on 2026-10-08, including release notes, applicable
tests and relevant issues/PRs. No GPL or unlicensed implementation was copied,
translated, vendored or saved as implementation source.

| Project | Revision | License | Fact used |
|---|---|---|---|
| [Aion2Flow](https://github.com/cloris-chan/Aion2Flow/tree/1ba3636b773e1c5f79246855ef239961f6bf70e8) | `1ba3636b773e1c5f79246855ef239961f6bf70e8` | GPL-3.0 | Protocol behavior only: early `1B92` party delta, later roster, direct PC metadata. Replay tests assert early party membership with an unrelated negative actor, and Assassin/Ranger/Elementalist profiles in marker `17`. |
| [a2meter](https://github.com/a2meter/Aion2Meter/tree/3194fc3d03805397baf671c53b2fbff207baeb37) | `3194fc3d03805397baf671c53b2fbff207baeb37` | Unclear / absent | Behavior only: v1.2.7 fixes missing party DPS following the `4536` UserInfo change and per-zone actor alignment. Tracker separates roster names and actor records, with late updates/aliases; those name heuristics are not adopted. |
| [SkeeveAN](https://github.com/SkeeveAN/Aion-DPS-Meter/tree/2ef71759738ffe902f72535ba8702e9e68d3b105) | `2ef71759738ffe902f72535ba8702e9e68d3b105` | MIT | Direct profile bands, distinct local server/class boundary, character refresh, entity directory lifetime. Its skill votes, 90-second roster memory and combat/class-based member or summon inference are not adopted. |
| [TK](https://github.com/TK-open-public/Aion2-Dps-Meter/tree/e72c6d5e0c9b647f5c1fcc0fca76fb22cd734d4e) | `e72c6d5e0c9b647f5c1fcc0fca76fb22cd734d4e` | MIT | Independent direct class-band cross-check; skill-range fallback concept; pending nickname/server records and reset. Issues 85/87 report numeric party names after a patch; release 1.7.5 adds another remote nickname opcode. |

Skeeve PRs 3/5 independently discuss delayed party names, accented UTF-8 byte
lengths, unnamed actors and summon contamination. They motivate strict byte
boundaries and independent identity proof, rather than combat heuristics.
TK's class-specific skill classifier returns no class outside its recognized
ranges. No skill inference is implemented here: supported direct profiles are
available, and neither generic nor summon skill traffic assigns player class.

## Whole-corpus audit

Ignored `.tools/tmp/live-party-profile/corpus.json` covers **43 physical captures,
45 game TCP flows**, all directions and all complete decoded records, including
container provenance. Nothing is selected by a party label before searching.

* `party-reconnect-01`, first fresh flow: 19 valid inbound `1B92`, all claim
  Mystogan runtime candidate 5065. They occur after the join, approximately
  57.706–96.555 seconds in that flow's research origin.
* `party-far-return-01`, fresh flow: 16 valid inbound `1B92`, all claim
  Mystogan candidate 9096. The first precedes the remote `4536`, while the
  `0092` roster already exists. Physical capture-relative timing is recorded
  in the production control artifacts; flow-relative research timing is not
  silently treated as capture-relative timing.
* One outbound `1B92`-shaped opcode in `er-marker-05` has a different body and
  fails the promoted grammar. It grants no membership.
* No valid inbound status was found for a known non-party actor in this corpus.
  This is a bounded negative observation, not proof about every possible game mode.
* 812 named `4536` profiles use marker `07`, include all 16 accepted codes below,
  and have the separate trailing faction candidate byte `01`.
* 16 local fixed-boundary `3336` profiles have marker `37`: 15 code 29 and one
  code 14. Opaque four-byte header values vary, so no player/address/header
  value is hard-coded.

Each status candidate retains physical path, flow, direction, complete framing
provenance, lengths, canonical values, opaque tail, exact bytes, independently
named matching IDs and neighboring roster/invite/join/termination records.
No public fixture bytes, captured UUIDs or captured player identities are used
as implementation/test constants.

## Party grammar, precedence and lifetime

The promoted inbound `1B92` body is exactly:

```
canonical unsigned varint actor candidate (> 0)
canonical unsigned varint current scalar
canonical unsigned varint maximum scalar (> 0; current <= maximum)
25 opaque bytes, final byte 00 or 01
```

The surrounding complete frame must agree with its length/prefix/tag and have
no suppressed decoding or warnings. The scalars/tail are not mapped to UI HP,
mana, flags or gameplay fields. The opcode supplies a party-status relation;
there is no decoded name, UUID, token, group ID or slot in this narrow status
form. It is a delta, not a full snapshot or an explicit removal.

The complete already validated `0D92` member grammar remains unchanged:
mask `00`, nonzero slot, u32LE ID, equal u16LE server pair, byte-length UUID
(36 ASCII bytes), eight-byte opaque token, byte-length UTF-8 name and exactly
78 opaque suffix bytes, with full consumption. ID zero and unobserved branches
remain unsupported. Membership needs a matching prior `0892` identity/invite
or a separately validated `1B92` for the same ID. An uncorroborated join stays
a candidate; it cannot erase a pending valid replacement or grant combat.

Deterministic precedence is validated termination/conflict,
then the current complete `0092` replacement, then newly corroborated join or
status delta, then invitation/candidate only. A replacement removes absent
members, including unresolved rows. Leave, self-only roster, disband and epoch
end discard earlier runtime statuses and candidates. Scene controls retire runtime
without removing stable membership; retired-epoch evidence remains inspectable. Delayed status cannot resurrect
an absent member. After a termination, a fresh validated join candidate followed
by matching status can establish a new interval; a fresh matching invite/join
can also do so. No duration-based sticky membership is introduced.

An authoritative member is visible with zero TOTAL, unavailable DPS and 0%
before runtime proof. A status without a name explicitly displays **Party member
(identity pending)**; it does not invent a player name. A complete corroborated
join or later roster can supply the name. A same-epoch independent `4536`
must match the exact candidate ID and, when already named, the exact name.
Runtime combat validity begins at the latest required proof, including the
later status when it corroborates a join. Both first-byte and completion time
must lie inside that interval. Earlier Other damage is never replayed/backfilled.

Status -> identity -> full roster strengthens the evidence while retaining the
same row key and validity interval; repeated full rosters do not duplicate it.
UUID/token contradictions reject the replacement after those stable fields are
known. A different ID with the same name cannot attach to an existing member.
Generic runtime remapping by name/class/target/timing is deliberately absent:
a new ID needs new current membership proof and independent actor profile.
Old class or old damage is not transferred across an unproved remap. The later
[scene lifetime change](scene-party-lifetime.md) retains stable UUID/token-backed
class and CURRENT totals through independently proven runtime rebinds. It also
adds bounded diagnostics export and audits dungeon/roster termination separately.

**Party outcome P-B.** The observed grammar is implemented and the lost early
join/late-profile path is repaired and tested, including inviter-side join/status
without inbound invite. The exact reported live far-invite experiment is not
physically present in this corpus. Public ID-zero far/instance member forms are
not independently validated here and remain closed. No new capture is requested.

## Direct class grammar and evidence

Both layouts share a leading canonical positive runtime candidate and a fixed
four-byte opaque header, but use distinct branch markers:

```
4536: varint ID | opaque u32 | 07 | byte UTF8 length | name | u32LE code | faction byte | opaque remainder
3336: varint ID | opaque u32 | 37 | byte UTF8 length | name | two server bytes | u32LE code | faction byte | opaque remainder
```

Names are 1–64 bytes of strict UTF-8 without control characters. No search for
another convenient offset, no text-length-as-character-length arithmetic, no
header/address special case. `3336` metadata is exposed only on independently
bound Self with matching exact ID/name; it never establishes Self or remote
membership. A local profile cannot supply remote class.

| Codes | Class |
|---|---|
| 5 / 6 | Gladiator |
| 9 / 10 | Templar |
| 13 / 14 | Ranger |
| 17 / 18 | Assassin |
| 21 / 22 | Spiritmaster (public Elementalist) |
| 25 / 26 | Sorcerer |
| 29 / 30 | Cleric |
| 33 / 34 | Chanter |

The mapping is `code = 4 * class band + variant`, accepting only remainder 1/2
and the eight established bands. Wider public roster bands are not imported into
this narrower profile grammar. The variant is preserved raw: **its semantic
meaning is unresolved**. It is not established as faction, side or gender; both
variants occur with the same separate faction candidate `01` in our profiles.
The separate byte accepts public-supported `01`/`02` without exposing faction
UI or inventing its effect on class. Own corpus only corroborates `01`.

Class evidence includes source (`Profile4536`/`Character3336`), epoch, actor,
exact name, ValidFrom, raw code, raw variant/faction byte, framing provenance,
SHA-256 and conflict state. Repeats keep the original validity. Conflicting
direct classes or names become Unknown for the epoch. Unsupported layouts are
ignored rather than rescanned; unsupported codes are Unknown. No manual preference
is written into this evidence, identity or combat state.

Physical controls match: Yaaz local code 29 -> Cleric; CHAD remote code 5 ->
Gladiator; Mystogan remote code 26 -> Sorcerer; Jeffjen local/remote code 14 ->
Ranger. Only Yaaz and CHAD have independently supplied class labels; the other
two are protocol results, not new external class ground truth.

**Class outcome C-B.** Validated remote marker `07` and local marker `37` are
implemented. Public marker `17`, cross-server local `3F`, fixed-width ID branches,
other class bands and other layouts remain Unknown. No class-specific/generic
skill or summon fallback is used. Direct automatic class wins; My Class supplies
only Self presentation when automatic class is Unknown. Remote has no selector.

## Bounds, shared pipeline and product behavior

3336/4536/1B92 facts use the same complete ACKed publication timeline and
deduplication/checkpoint retirement as roster and combat. No extra listener,
transport decoder or UI parser exists. Direct metadata is capped at 4096 actors
per epoch; overflow fails closed for class until the next epoch. Party status,
join and active membership are capped at five remote entries. Roster replacement
remains the supported one/two-member grammar. Name directory remains 4096,
recent transitions eight, closed intervals 16 and frozen participants 15.
Only compact evidence remains after checkpoint; packet bytes are not retained.

Icons retain packaged WPF resources, cached/frozen bitmaps, existing compact size
and Unknown-hidden behavior. Thick contribution rows, TOTAL/DPS/%, rank, YOU,
CURRENT reset, shared clock, AFK timeout, RTT, hotkeys and single-instance guard
are preserved. Optional Data unavailable export is not implemented; the incident
is unreproduced, and no transport/binding trust rule is broadened. Existing compact
party diagnostics now include status counts and retained status count.

## Validation and manual product smoke

Final Release locked restore/build/test: **949 tests pass (895 backend/presentation,
54 WPF); zero warnings/errors**. Seventy additional cases cover fixed class bands,
local/remote layout differences, conflicts, scope/bounds, generic/summon isolation,
automatic/manual priority, early status/join, delayed actor proof, precedence,
corroboration time, malformed directions/boundaries, lifecycle and checkpoint
retirement. Existing icon resource/render, hotkey, singleton and accounting tests pass.

Current physical replays retain Self 6/5830; CHAD 6/4634 with 267 nonmember Other
excluded; Mystogan lifecycle 2674 with post-leave/disband excluded; reconnect
members restored; far-return 18/64604 with a membership row before runtime proof;
dungeon 14/15774 and attested teleport binding. CHAD's nonmonotonic historical
capture uses the finite shared adapter, not a claimed incremental-live run.
06/26 decoder behavior, 0x36 Unsupported and Coverage Partial are unchanged.

Evidence, full corpus audit, final file/status/hash verification, build/test logs
and physical control output live in ignored `.tools/tmp/live-party-profile/`.
Temporary executable:
`C:\Projects\Aion2Meter\.tools\tmp\live-party-profile\publish\Aion2Meter.App.exe`.
Root `publish/` is untouched; nothing is staged, committed or pushed.

1. Exit the previous meter via tray Exit; start only the temporary executable.
2. Keep My Class Automatic. Fresh world entry should show Yaaz's Cleric icon + YOU.
3. Invite Mystogan while far away. Corroborated membership should show zero TOTAL /
   DPS unavailable; if only status is available, name explicitly remains pending.
4. Approach and perform normal combat: independent profile should name/bind the
   same row, show automatic Sorcerer icon and count only future supported events.
5. Leave/rejoin/disband; verify excluded actors never gain rows/contribution and
   post-termination damage is not counted. Use CURRENT reset as usual.
6. Normal teleport/dungeon/world return: verify existing safe binding refresh,
   current membership and automatic icon behavior. Hide/show the same overlay with
   Ctrl+Shift+H and tray. This is a gameplay smoke checklist, not a capture request.
