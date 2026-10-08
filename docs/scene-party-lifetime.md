# Scene transitions, party lifetime and diagnostic export

Worktree: `C:/Projects/Aion2Meter`, branch `phase-3-protocol-research`, unchanged HEAD
`140f932`. This extends the existing uncommitted production stack. Nothing is
staged, committed, pushed or merged. Capture and original metadata files are
read-only. No new physical capture is requested.

## Confirmed defect and limits of attribution

**Dungeon/scene transition alone DOES NOT clear stable party membership.**
Runtime actor ownership, membership authority and CURRENT accounting have
separate operations. A runtime boundary closes actor intervals; it is not
an authoritative leave/disband.

The previous `PartyRosterResolver.Observe` put inbound `2192` and `2F92` in an
external-only removal-candidate branch and called `Clear`, erasing membership.
The comment itself acknowledged that our captures had not validated removal
semantics. This is a confirmed internal lifecycle defect. TCP end and Faulted
meter paths also called `EndEpoch`, deleting membership as a side effect of
transport/binding ownership loss. These destructive live calls are removed.

Our existing **solo** dungeon capture contains a complete inbound `2F92` around
world return. Running the former branch against an established party would
erase its membership. This identifies a reproducible unsafe code path, but does
**not** prove that this was the exact message received during the user's unsaved
three-member dungeon incident. The actual live incident remains user ground
truth; no packet trace of that particular party scene transition exists.

Intermittent Data unavailable has no confirmed physical root cause. Changed
Self ID, malformed framing/containers, retired-byte conflicts and competing
resolved scopes remain fail-closed. No name scan, clock grace, gameplay
heuristic, arbitrary Other promotion or changed-ID Self recovery is introduced.
The new diagnostic export makes these paths observable without another broad
PCAP session.

## Existing corpus

The ignored audit `.tools/tmp/scene-party-lifetime/corpus.json` decodes **43
physical captures / 45 game flows**, all without audit exceptions. It retains
framing provenance, direction, completion packet/time, stream/container offsets,
raw candidate bytes and status separately, solely in ignored research output.
It includes all `1536`/`3336`, `0092`, `0892`, `0D92`, `1392`, `1B92`,
`2192`, `2F92`, `2D92`, `3292`, and the public-number cross-check candidates.

| Family | Inbound complete candidates | Outbound candidates | Interpretation |
|---|---:|---:|---|
| 0092 | 10 | 1 | Five count-one and five count-two candidates; existing completely consumed, independently corroborated forms remain the authority |
| 0D92 | 5 | 1 | Existing validated join forms, not a full replacement |
| 1B92 | 35 | 1 | Existing delta/status grammar; not a replacement or termination |
| 2F92 | 1 | 1 | Inbound four-byte-body control near solo dungeon return; no leave semantics promoted |
| 2192 | 0 | 1 | No validated inbound removal/control meaning |
| 2D92 | 2 | 0 | Both in the dungeon capture; opaque research candidates, not promoted |
| 3292 | 13 | 1 | Opaque recurring control family, not promoted |
| 0197 / 0297 | 0 / 0 | 2 / 2 | Outbound numeric coincidences do not validate public party tags |
| 0497 / 1D97 | 0 / 0 | 0 / 0 | No matching framed candidates |

`2026-10-08_134704_dungeon-transition-01`:

* One game TCP flow, fresh initialization, same endpoints/client ephemeral port,
  Self Yaaz `4971`; no dungeon handoff to a different TCP flow.
* `3336` completions at capture-relative **22.9013**, **36.3955** and **115.2947**
  seconds all identify the same Self ID/name. They bracket world, dungeon and
  world-return observations. The fixed-layout local profiles agree.
* Inbound `2F92` completes at **112.6972** seconds, packet **8951**, stream offset
  **91225**: complete frame `0A2F92F4360700`. Its four-byte body is kept opaque;
  its value is not hard-coded and is not labelled dungeon ID or party-leave data.
* Two `2D92` candidates complete at flow-relative 80.835 seconds. No party
  membership or termination field is assigned to them.
* There is **no inbound 0092**, empty roster or Self-only roster at dungeon return.
  Therefore the suspected temporary roster-refresh mechanism is not established.
* The later Character Change / Server Selection section is excluded from the
  dungeon-exit mechanism inference. The physically observed dungeon combat is
  still **14 Self events / 15,774**, with eight unrelated events excluded.

`party-far-return-01`: the initialized flow's `0092` completes at capture-relative
30.7574 seconds, before Self confirmation at 36.3096. The Mystogan row becomes
visible after independent Self proof, remains unresolved through the 170.6053
second `3336` teleport refresh, and binds at the later independently matching
profile (production observation 187.510 seconds). Its **18 eligible events /
64,604** are unchanged. No dungeon is invented for this experiment.

`party-reconnect-01` has two independently fresh epochs. Self IDs differ across
them, and a fresh full roster independently restores the party. It does not
prove that an arbitrary new TCP flow may inherit old membership by Self name.
`party-lifecycle-01` retains the real Self-only leave and disband evidence.
No corpus record proves a temporary empty/Self-only roster that must be ignored.
Such suppression, public grace counters and kick semantics are **not** added.

## Current public references, checked 2026-10-08

[a2meter master](https://github.com/a2meter/Aion2Meter/tree/3194fc3d03805397baf671c53b2fbff207baeb37)
was checked at `3194fc3d03805397baf671c53b2fbff207baeb37`.
[Release v1.2.6](https://github.com/a2meter/Aion2Meter/releases/tag/v1.2.6)
reports validation of four party members from `02 97` updates in its 2026-06-10
party-dungeon capture. [Release v1.2.7](https://github.com/a2meter/Aion2Meter/releases/tag/v1.2.7)
reports the UserInfo `45 36` and per-zone actor alignment fixes.

Its [PartyStreamParser](https://github.com/a2meter/Aion2Meter/blob/3194fc3d03805397baf671c53b2fbff207baeb37/src/A2Meter/Dps/Protocol/PartyStreamParser.cs)
has distinct dungeon-exit, party-leave, full-list/update and board-refresh
handling; an empty list after a board refresh does not automatically eject.
[ProtocolOpcodeConfig](https://github.com/a2meter/Aion2Meter/blob/3194fc3d03805397baf671c53b2fbff207baeb37/src/A2Meter/Dps/Protocol/ProtocolOpcodeConfig.cs)
uses configurable defaults in a `97` family, not proof of our `92` semantics.
The parser also contains heuristic scans and packet grace counters we do not
adopt. Its [PartyTracker](https://github.com/a2meter/Aion2Meter/blob/3194fc3d03805397baf671c53b2fbff207baeb37/src/A2Meter/Dps/PartyTracker.cs)
can alias name/server records to newer IDs. We require current roster/join
relations and independent actor identity instead. Importantly, its current
`ClearPartyForDungeonEnter` actually withdraws party flags while retaining
selected identity hints; its [tests](https://github.com/a2meter/Aion2Meter/blob/3194fc3d03805397baf671c53b2fbff207baeb37/src/A2Meter.Tests/PartyTrackerTests.cs)
verify Self preservation and changed-Self metadata. They do not prove universal
stable-party preservation. Our membership policy is independently defined.
License absent/unclear: behavior only; no source copied, translated or vendored.

[Waffle current changelog](https://github.com/Waffle-ens/waffle_meter/blob/4c58628ac2aa5e70456d4052a717a260174bbf80/README.md)
was checked at `4c58628ac2aa5e70456d4052a717a260174bbf80` (MIT, latest v3.3.1).
Its v2.8.0 notes cover missing/wrong party rows and Self recognition after zone
travel/reentry. v2.3.3 covers the **eight-player raid uploader's Self subparty
slot**, explicitly not the on-screen meter and not parties of four or fewer.
v2.2.0 preserves recognized Self through combat reset, addresses delayed
instance Self identity, and fixes stale party/duplicate Self display. v2.1.0
adds party/raid preview before dungeon combat. These are bug-parity targets,
not network-field evidence or imported code.

## Lifecycle and termination audit

| Path | Previous category | Current category / authority |
|---|---|---|
| CURRENT reset, new encounter after idle | COMBAT ONLY | COMBAT ONLY; membership/profile/actor validity unchanged |
| Accepted attested same-ID/name/layout 3336 refresh | No remote scene retirement | RUNTIME ACTOR ONLY; preserve membership and validated stable class; close all remote intervals and discard scene identity caches |
| Complete four-byte-body inbound 2F92 | PARTY MEMBERSHIP + RUNTIME | RUNTIME ACTOR ONLY; preserve membership; body semantics remain unassigned |
| Unknown 2192 or unsupported control shape | PARTY MEMBERSHIP + RUNTIME | RUNTIME ACTOR ONLY plus authority barrier; retain evidence/rows but require fresh corroborated membership/status before profile can resume combat |
| Changed Self ID/name, rejected refresh, malformed container, gap/conflict, Faulted scope | EVERYTHING for live accounting/roster/profile | COMBAT + RUNTIME suspended; old membership evidence retained in its retired scope, no damage or rows under untrusted authority |
| TCP FIN/RST, replaced handshake, capture stop/end | Party/runtime evidence erased by EndEpoch | RUNTIME retired and CURRENT stopped; old membership evidence retained, not blindly inherited into another owner |
| Fresh TCP/authority-flow change | New empty epoch model | Independent Self binding and fresh current membership authority required; no name-only cross-epoch transfer |
| Competing Resolved scopes | Paused | Paused; no arbitrary owner, explicit AuthorityAmbiguous diagnostic |
| Checkpoint continuation | Compact proofs retained | Same; no rescan or age-based membership eviction; attestation unchanged |
| Valid 0092 replacement excluding member | PARTY MEMBERSHIP + RUNTIME | Same authoritative removal; unresolved members are also removed |
| Valid Self-only 0092 | PARTY MEMBERSHIP + RUNTIME | Same real leave/replacement; no unproved empty-refresh exception |
| Valid 1392 complete 0000 | PARTY MEMBERSHIP + RUNTIME | Same validated disband; delayed status cannot resurrect absent members |
| Corroborated 0D92 join | Adds membership/runtime when independently ready | Adds/updates stable claim; does not remove earlier far members merely because pending roster processing is superseded |
| 1B92 delta | Adds/status corroboration | Same; never interpreted as a full replacement/removal or stable new-ID map |
| Invite-only 0892 | Candidate only | Candidate only; never a row/damage permission by itself |
| Name/UUID/token contradiction, unsupported/malformed full roster, capacity exhaustion | Eligibility/membership withdrawn fail-closed | Same trust revocation, explicitly diagnostic; not labelled a proven party leave |
| Replacement waits for independent profile | Clears actors, preserves named membership | Same; no damage before latest required proof |
| Explicit standalone resolver EndEpoch/reset | EVERYTHING | EVERYTHING, explicit scope/session evidence destruction; no longer called as a live transport/scene side effect |
| Retired epoch eviction (16-scope bound), new product session | EVERYTHING in retired scope | Releases bounded evidence; never migrates into unrelated current character/session |
| UI hide/show/settings | No backend clear | Same; one overlay/capture owner |
| Class cache failure/unsupported profile | Class only | Class only; does not grant or remove party authority |

Validated membership terminates through the supported authoritative full
replacement/Self-only leave/disband forms. Genuine replacement wins over older
status, invite or pending claims. Unvalidated control IDs are not labelled leave
or kick. Identity contradictions and resource exhaustion revoke trust rather
than pretending an unknown party is safe. No independently validated kick form
exists in this corpus, so none is fabricated.

TCP end is not declared a party leave. However, without authoritative stable
**local** identity continuity, old membership is archived/paused rather than
shown under an arbitrary new Self scope. Fresh two-entry roster recovery remains
available. Full cross-TCP continuity without a new authoritative roster and
changed-Self recovery within an old TCP epoch remain unsupported. This is a
safety boundary, not proof that the in-game party ended.

## Runtime rebind, class and accounting

UUID + opaque token identify a stable membership key; runtime ID is excluded
from that key. Exact name must also agree for a claim update. Status-only keys
remain scoped and can be strengthened by later stable identity proof without
replacing their existing row. UUID text is canonicalized for comparison; raw
capture bytes and provenance hashes are untouched.

A scene retirement closes old actors with `ValidUntil`, clears runtime names,
invites, joins and pending runtime work, but preserves membership. An independent
**fresh** profile matching the still-current claim's exact ID/name can rebind
that same ID. A **different** ID requires a complete independently corroborated
new join or full roster with matching UUID/token/name, followed by independent
profile identity (either arrival order is supported). Name/class/combat proximity
alone never selects another ID. Both claim/profile first-byte time must not
predate the scene floor. Damage first-byte and completion must be at/after the
latest required proof. No retrospective damage backfill occurs.

Unvalidated 2192/control shapes require fresh membership authority as well,
even if a matching profile appears; a bare profile cannot override that barrier.
Repeated accepted 3336 does not relax it. CURRENT participants aggregate by the
membership key, independently from current runtime ID, so a rebind cannot create
a duplicate row or lose already-counted valid CURRENT damage. Existing reset/
idle semantics continue; scene refresh does not arbitrarily reset Self totals.
Frozen departed CURRENT contributions are historical rows, not active membership.

Class is cached for at most five **UUID/token-backed** current members after an
independently eligible direct profile. Its original source, epoch, actor,
validity and digest remain available. A stable row may keep that class while
runtime is unresolved or after a proven new-ID claim. Status/name-only identity
cannot carry class to another actor. Contradictory direct class evidence produces
Unknown; termination prunes the stable class cache. Automatic Self profile and
presentation-only manual fallback are unchanged. No skill votes are added.

## Diagnostics

Settings > **Export diagnostics** is also reachable from tray > Settings.
It opens a save dialog; export creates no capture and uploads nothing. The most
recent session's export remains available after stopping or opening Advanced.

The ring holds **256 compact transitions**, and last protocol activity for at
most **16 scopes**. Entries include selected/competing scope, lifecycle, endpoint
strings, binding/status/validity, gaps/conflicts, capped reason text, accepted
initialization/refresh or withheld initialization, prior bound Self ID and
bounded rejected initialization field candidates (explicitly non-authoritative),
and membership before/after
keys/names/runtime IDs/proof source/digest. Member counts are **remote roster
slots** (Self is separately identified), not a guessed total party size.
Removal/retirement reasons distinguish disband, full replacement, control,
scene refresh, epoch end, fault, CURRENT reset and capture-source failure.
They contain no raw packets, raw UUID/token, full combat history or reflected
framework endpoint objects. IPv4 and scoped IPv6 serialize through deliberate
endpoint text projection. Thread-safe exports are detached immutable snapshots;
no Dispatcher history or disk write is done per packet.

The record ring reports withheld initialization from a poisoned publication
scope, not a fabricated successful refresh. If failure happened before records
were available, the epoch warning/capture-failure event records that limitation.
Archive eviction is bounded; a long-running session's very old transitions may
have rolled off the ring. No runtime state is reconstructed from diagnostics.

## Validation and exact manual smoke

Full locked restore, Release solution build and both test projects pass: **972
tests (917 backend/presentation + 55 WPF), zero build warnings/errors**;
final counters and preservation hashes are in ignored
`.tools/tmp/scene-party-lifetime/validation-summary.json`.
Regressions cover three members at enter/exit, late new-ID claim/profile in both
orders, old actor exclusion, first-byte-before-validity exclusion, stable class,
real full replacement, Self-only leave, disband/rejoin, status termination,
CURRENT-only reset, random Other/no encounter extension, 300 repeated enter/exit
cycles without duplicate Self/members or unbounded caches, late pre-scene proof
rejection, and far-member survival when another member joins. Diagnostics tests
cover IPv4/scoped IPv6, empty/populated JSON, before/after actor keys, withheld
refresh, concurrent bounded exports and failed-source export. A real WPF settings
test verifies the export button does not start capture.

Seven physical production controls retain exactly the previous eligible event
counts/amounts: Self 6/5830; CHAD 6/4634; lifecycle 2/2674; reconnect Self
10/7184 + party 102/210994; dungeon Self 14/15774; far 18/64604; reciprocal
no damage. CHAD uses the prior finite adapter because its old recording has
non-monotonic source timing; this is not claimed as a clean live replay.

Fresh build: `.tools/tmp/scene-party-lifetime/publish/Aion2Meter.App.exe`.
Root `publish/` is preserved. Exit the old build from its tray before starting
the fresh executable; the existing single-instance guard remains active.
The in-game smoke is **not performed by this automated session**:

1. Start this build before fresh world entry; Settings > My Class Automatic.
2. Form the party while members are far away; verify all authoritative rows,
   with unresolved members at zero TOTAL / DPS unavailable / zero contribution.
3. Do normal open-world combat.
4. Enter a green-quest dungeon/instance without leaving/disbanding the party.
5. Exit to the world; the same membership rows must remain. Runtime may wait
   for current identity; validated stable class may remain.
6. Approach a remote member; let that member attack a normal target. Verify
   the same row binds from current independent proof and only future damage counts.
7. Leave, rejoin and disband; confirm real termination and fresh rejoin behavior.
8. Continue normal world travel and watch for Data unavailable.
9. On failure, immediately tray > Settings > Export diagnostics. Record the
   approximate real-game step/time alongside that JSON. No new PCAP is requested.

## Exact delivery files for this addendum

Production: `src/Aion2Meter.Replay/PartyRoster.cs`, `LiveCombatMeter.cs`,
`LiveCombatFeed.cs`, new `LiveDiagnosticBuffer.cs`;
`src/Aion2Meter.Presentation/LiveOverlaySession.cs`;
`src/Aion2Meter.App/OverlayApplication.cs`, `OverlaySettingsWindow.xaml`,
`OverlaySettingsWindow.xaml.cs`.

Tests: new `ScenePartyLifetimeTests.cs`, `LiveDiagnosticsTests.cs` under
`tests/Aion2Meter.Tests`, new `DiagnosticExportWindowTests.cs` under
`tests/Aion2Meter.App.Tests`; existing `PartyMeterTests.cs`,
`PartyRosterRecoveryTests.cs` and `LiveInitializationRefreshTests.cs` updated
for explicit retirement/fresh-identity requirements and distinct stable UUID
fixtures. Net test count increases by 23; the old unknown-control termination
case is superseded by the new preservation/authority-barrier cases.

Documentation: this file, `docs/live-party-profile.md` and `README.md`.
No transport, framing, decoder, CurrentPlayer attestation, class parser,
class assets, project/config/lockfile or root publish source was changed by
this addendum. The original uncommitted edits to those other files remain.
495 baseline capture/root-publish files have unchanged SHA-256; no baseline
file is missing. The published app DLL matches the final Release DLL hash.
