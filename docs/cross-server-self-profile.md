# Cross-server local Self profile: 3336/3F

## Confirmed failure and scope

The 20261009-043607 live diagnostic separates this failure from the checkpoint failure.
Epoch 1 accepted 3336/37 and independently resolved its actor. After a new backend scope,
epoch 2's packet 2982 at stream offset 27689 declares marker 0x3F at the fixed marker
boundary. The old decoder rejected that marker. Both directions' checkpoint states were
Complete, gaps/conflicts were zero, stable presentation survived, and runtime was awaiting
proof. The rejection itself is confirmed; this export does not contain the complete wire
record or its origin-server/class/faction fields. These fields must still pass the new
decoder in live verification. No bytes were reconstructed from candidate text.

## Independent evidence

All 43 local game PCAPs / 45 selected game flows were re-read. They contain sixteen
3336/37 profiles and **zero 3336/3F wire examples**. The remaining PCAP is the browser
control. The new diagnostic supplies one local 3F observation, distinct from a full wire
sample. There is no local 3F combat correlation assertion.

Public behavioral prior art was inspected at Aion2Flow revision
`1ba3636b773e1c5f79246855ef239961f6bf70e8`:
[replay expectations](https://github.com/cloris-chan/Aion2Flow/blob/1ba3636b773e1c5f79246855ef239961f6bf70e8/tests/Aion2Flow.ReplayTests/Capture/PacketLogReplayServiceTests.cs).
`Replay_20260705015611_Applies_CrossServer3336_SelfIdentityMarker3F` expects local
PlayerIdentity, actor, nickname, origin server and faction, retained in metadata.
`Replay_20260703041828_Applies_Extended3336_SelfIdentity_Without4536` confirms a
direct local identity path; `Replay_20260809021609_ParsesFixed32CrossServerIdentityAndCombat`
is also relevant behavioral evidence. GPL-3.0 implementation/test source was not copied,
translated, ported or used as an implementation template.

Three public wire-data logs were inspected using our own framing/container decoder and
an ignored research utility. The standard 3F fixture yielded four matching fixed headers:
canonical actor 5905, UTF-8 nickname, origin server 1007, raw class code 10, faction 1.
All four passed our explicit 3F structural decoder. Following supported records used that
actor, but no damage behavior or authority was imported from that correlation. The
extended 37 fixture confirmed the existing header order. The public fixed32 fixture has a
different transport envelope; our research normalization did not fully validate its LZ4
containers. That experiment is not production support or field proof. Production transport
framing was left unchanged: the actual live 3F record was already framed and decoded as
an Unknown 3336 record.

[Aletheia v9.2](https://github.com/p62003/aletheia_AION2_DPS_Meter/releases/tag/v9.2)
and its [troubleshooting guide](https://github.com/p62003/aletheia_AION2_DPS_Meter/blob/main/Guide_Troubleshoot_EN.md)
support separating nickname/stable identity from reassigned scene actors. Their
implementation was not copied or reverse engineered.

## Modeled layouts and trust

The explicit layouts are `3336/37` local and `3336/3F` cross-server local. Each starts,
relative to its complete canonical application prefix, with:

`tag-2 | canonical actor varint | opaque-u32 | branch-u8 | name-length-u8 |
UTF-8 name | origin-server-u16LE | class-u32LE | faction-u8 | opaque remainder`

The marker is after the leading actor and four opaque bytes. The nickname follows its
own byte length, not a scan of later text. Minimum validated header length is
`prefix width + actor width + UTF-8 name byte length + 15`; the entire declared frame
must be present even though its remaining gameplay body is opaque.

For the newly authorized 3F branch the actor is nonzero/canonical and within uint32;
name is strict UTF-8, 1–64 bytes, nonblank, without controls; origin server is nonzero;
class uses the established eight class bands/variants; faction is a supported 1/2.
Zero/unknown sentinels, another marker or truncation cannot use legacy text-pair authority
as a fallback. Existing 37 and remote 4536/07 behavior remains covered.

Decoding alone does not bypass transport: fresh SYN/SYN-ACK/completion ACK, same epoch
record provenance, complete contiguous peer-ACKed publication, valid containers and
unambiguous record order still apply. Contradictory stable name/server/class/faction
facts fail closed. A retained prior stable profile restricts cross-server live handoff;
it never supplies the new actor or transport authority. Fresh application startup without
such prior identity uses the complete local-profile branch as independent evidence.

Branch fingerprints are provenance, not stable character equality. Switching 37/3F
does not manufacture a different stable identity. Changing server/faction/class/name
does not get treated as mere provenance. Conflicting identities and unordered assignments
do not select a winner.

## Runtime and party lifecycle

The new actor's ValidFrom is accepted profile completion. Previous actor intervals close
at the replacement/retirement boundary; connection closure independently ends their
scope. Previously published encounter provenance is immutable. Before-confirmation or
awaiting-period combat stays excluded; future confirmed combat resumes automatically.
There is no backfill, first-attacker rule, timeout bypass, name-only trust or IP/map rule.

Self UI identity does not depend on actor or branch marker, so the same compatible row
survives waiting/rebind. Stable party membership/name/class rows also survive. Current
remote runtime binding still requires current membership claims (including validated
UUID/token where available) and the independent supported remote profile. A local
3336/3F record is neither party membership nor a remote 4536 replacement. Public examples
prove its local use; they do not prove it can never have another role. No remote 3F
authority was invented. Arbitrary party members and duplicate names retain the existing
data-driven safety rules.

The implementation uses protocol structure on every map. No map whitelist, backend
address, test character name or fixture actor is in production logic. A future map with
these supported structures needs no special case; a new protocol layout remains withheld.

## Verification and delivery boundaries

The baseline 1,065 regressions are retained with two obsolete 3F-rejection controls changed
to genuinely unsupported markers; their counts were not removed. Thirty-nine independent
new regressions cover logical live handoff, ambiguous 1536 candidates, 37/3F runtime
replacement, invalid/truncated profiles, pre/post validity, no backfill, duplicate-name
conflicts, arbitrary scenes/players and party-row continuity. Full restore/build/test:
1,104 passing (1,049 Core/Replay/Presentation and 55 WPF/App), zero warnings/errors.

Existing checkpoint Waiting/Invalid, transport conflict/gap/privacy and bounded-history
tests passed. Combat category 0x36 remains Unsupported / Coverage Partial. This task
does not claim the separate Data unavailable physical blocker is fixed.

Live acceptance still requires the temporary build to resolve the actual complete 3F
profile and count only later supported damage during world → scene → world. No new PCAP
was requested; no stage, commit, push, tag, merge or release is part of this task.
