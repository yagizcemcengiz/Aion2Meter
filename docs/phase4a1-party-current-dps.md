# Phase 4A.1 — validated party lifecycle and CURRENT DPS

Solo shows resolved Self. Party rows require all three independent inbound facts:
strict 4536 name/runtime identity, matching 0892 name/runtime/UUID/token association,
and a complete 0D92 active-member announcement. Invitations alone do not qualify.
No character names, historical IDs, capture offsets/times, class or combat activity
are membership selectors. The shared combat decoder and 06/26 projection are unchanged;
0x36 is still Unsupported, coverage Partial. Overall and healing are not implemented.

## Supported lifecycle and evidence

`0D92` has the observed control/slot, u32LE runtime ID, repeated server-shaped u16,
36-byte ASCII UUID, opaque 8-byte token, byte-length UTF-8 name and opaque 78-byte suffix.
`0892` independently uses a canonical varint for the runtime ID. Other masks or
changed layouts do not grant membership. Unknown suffix bytes are not class/role flags.

The controlled `party-lifecycle-01` capture independently establishes:

| Transition | Complete inbound boundary, capture-relative | Evidence |
|---|---:|---|
| Join | 33.306083s | 0D92, matching 0892 and prior strict 4536 |
| Voluntary leave | 42.118014s | Fully consumed 0092 Self-only replacement |
| Rejoin | 56.094087s | New 0D92 boundary with matching evidence |
| Disband | 66.971860s | 1392 with exactly two zero body bytes |

Times are capture arrival, not input/render timestamps. Local Yaaz 4348 is resolved
through fresh initialization; remote Mystogan 5065 is resolved independently through 4536.
The names/IDs above are capture results only. Membership intervals are `[ValidFrom, ValidUntil)` and bounded, epoch scoped, with compact identity/invite/join/termination
provenance; no raw packet history or disk roster persistence.

Voluntary leave is **not** implemented as a guessed 2192 delta: our capture emits
a 0092 replacement containing only independently bound Self. Two observed 0092 forms
are accepted as Self-only: control 00 with a 24-byte opaque header then canonical
member-count 1, or control 08 with that header, canonical 5 and 120 zero bytes, then
member-count 1. The member block is followed by one opaque footer byte and must
fully consume the record. Footer/suffix values are not assigned gameplay meanings.

Multi-member 0092 replacement, nonzero optional lanes, other masks and unvalidated
transition bodies **withdraw remote eligibility**; they do not add guessed members.
Fresh validated join evidence is needed afterward. A party already formed before
reconnect may therefore remain Self-only until new join evidence appears. This is a
coverage limitation, not permission to fall back to class/names/nearby combat.
External-only 2192/2F92 candidates also withdraw eligibility without claiming a
validated member-body layout. 1192 occurs before disband but has no promoted semantics.
Kick, leader transfer, larger groups and patch variants beyond these forms are not
claimed. No invented party expiration timeout is used.

The [external prior-art PR inspected in Phase 4A](https://github.com/Kuroukihime/AIon2-Dps-Meter/pull/68)
suggested the 92 family. Our captures independently validate 0D92 and 1392; roster
replacement, rather than that PR's 2192 candidate, supplies voluntary leave here.
No GPL/no-license source or visual assets were copied.

## Accounting and presentation

DamageEvents remain broad and independently associated. An Other event enters CURRENT
only while its direct source ID has validated active membership, with both first-byte
and completion provenance after ValidFrom. Lifecycle records and hits publish on one
complete-record timeline, including when a whole replay window contains join/leave/
rejoin/disband. Applying only the final roster to all earlier hits would be incorrect.

Total per participant sums unique Amount once. OptionalComponents are already inside
Amount. The group encounter starts on the first included hit and extends on any
eligible member's hit. 30-second inactivity applies to group activity. Displayed
elapsed remains first-included-completion to last-included-completion, preserving
validated solo behavior. Every member uses that same denominator; near-zero DPS is
undefined. Self AFK for 40s with continuing member hits keeps the encounter alive and
lowers Self DPS. DPS stays fixed between included hits, as in the validated solo model.

Contribution is memberAmount/groupAmount. Zero-damage readiness assigns Self 100%
and others 0%; once damage exists contributions sum 100% before display rounding.
Random Other never enters totals, timing or denominators. On leave, already counted
remote damage and its row remain in the current/last encounter; future hits are
excluded. The next encounter drops former members. Rejoin of the same current-epoch
ID resumes that participant's existing current total without retroactive additions.

Rows sort by DPS descending, then ordinal name/ID for stable ties. ME follows Self,
not rank. The original WPF template, keyed reusable ViewModels, automatic window
height and 5 Hz snapshot refresh are reused. No per-packet dispatcher work or visual
assets were added. Console Self metrics/counters remain available; JSON additionally
exposes the immutable Members, GroupTotalDamage and compact PartyRoster summary.

## Bounds and startup

Per epoch: at most 4096 independent name facts, 5 invite facts, 5 active remote members,
16 recent closed intervals, 15 remote participants with counted current-encounter
damage. Bound failures cannot evict previously counted damage or invent identities.
Beyond 15 distinct remote participants within one encounter, additional participant
hits are not accepted until the next encounter. This is a deliberate rare-churn bound.
Epoch count remains 16. Party-record publication identities retire at ACKed checkpoints;
compact membership survives without retaining original packets or all DamageEvents.

New epochs require independent Self and party evidence. Midstream waiting is unchanged.
Change Character alone may keep the TCP epoch; server selection/reselection has been
manually shown to create a fresh connection. No stale roster is restored from disk.
Unknown/conflicting transport removes display data using the existing protections.

## Validation and manual smoke

Production live replay of the new capture includes remote Amounts 1228 and 1446
(total 2674); excludes 1284 after leave and 1230/246 after disband, plus all 11 unrelated
Other events. Existing CHAD finite replay using the production feed/meter includes
6 events/4634 and excludes 267 Other; old Self capture retains 6/5830. Captures remain ignored.

Some historical PCAPs (`self-entry-01`, `party-remote-gladiator-01`) contain backwards
packet timestamps and cannot be claimed as successful live-source simulations:
the unchanged strict live ingestion policy rejects them. CHAD is verified through
the shared finite decoder and production orderedfeed/accounting instead; no packet
timestamps were rewritten or sorted to bypass live trust rules.

Real-game overlay smoke is still required; automated replay is not a new manual test:

1. Start the new build, select adapter, resolveSelf; verify one solo row.
2. Invite one friend/accept; verify exactly two rows.
3. Both attack normal mobs; check totals, shared duration, ranks and contribution.
4. A nearby unrelated player attacks; verify no third row or denominator change.
5. Self stops for 40s while friend attacks with gaps below 30s; check preserved Self total,
   decreasing Self DPS and continuous encounter.
6. Friend leaves and attacks; its counted row freezes and new hits are excluded.
7. Wait 30s without included hits, then attack: no stale former-member row.
8. Rejoin; verify fresh membership and counted hits. Disband and verify exclusion.
9. Reconnect through server selection; no old runtime IDs or remote roster carry over.

No further capture is required for the controlled two-person lifecycle. Additional
capture evidence is needed to expand full multi-member refresh, kick/leader-change
or larger-group coverage. Before Overall/dungeon totals, complete this manual CURRENT
smoke and define dungeon/session boundaries separately from connection and encounter.
