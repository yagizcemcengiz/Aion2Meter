# Live outbound application authority

The 2026-10-09 diagnostic exported a completed, peer-ACKed C2S outer frame at
offset 24540, length 11, with checkpoint cursor 24354. The bidirectional research
decoder treated the first two body bytes as `FF FF`, read the next four as u32LE,
and rejected the declaration before allocation. Its allowed output size is
1 through 2,097,152 bytes, inclusive. The export contains neither the raw frame
nor its numeric declaration. The exact value and gameplay meaning cannot be
reconstructed from that export. It is not evidence of a known control opcode,
compression, or corrupted TCP. The contemporaneous S2C checkpoint was complete;
gaps and conflicts were zero. Last successful S2C tag `0036` is not the failed
C2S frame and establishes no causal relationship.

All product authority is inbound: supported DamageEvent projection explicitly
requires S2C, Self initialization and profile decoding require S2C, party state
requires S2C, and the feed's protocol/profile activity is S2C. Outbound packets
remain necessary for handshake/epoch/lifecycle, ACK progress and passive TCP RTT;
these consumers use TCP metadata, not decoded application bodies.

The live decoder now applies an explicit inbound application authority policy.
Canonical outbound outer frames become `NonAuthoritativeFrame`. Their bodies
are opaque: no container interpretation, decompression, gameplay publication or
profile/party binding. This classifies the live failure as an unsupported,
non-authoritative outbound application envelope, without claiming its unknown
wire semantics. Finite protocol research retains bidirectional inspection.

Both directions still require the existing sequence origin, reassembly and ACK
boundaries. A partial outer frame waits; an invalid canonical length faults with
no byte skipping. Conflicts, retired overlap checks, gap/pending behavior,
resource bounds and raw-stream privacy checks are unchanged. Inbound LZ4/header,
inner framing and decompression budgets are unchanged and still fail closed.
There is no new resynchronization, map/name/IP rule, first-hit identity heuristic,
retroactive backfill, or combat category 0x36 support.

The ignored audit covers 43 physical game captures / 45 flows: 43,465 C2S outer
frames, including 41,375 length-11 frames, with zero C2S `FF FF` markers or framing
issues. S2C has 187,910 outer frames, 42,970 length-11 frames and 1,235 containers;
none of the containers has length 11 or an out-of-bound declaration. The failed
variant is absent, so no earlier captured tolerance/checkpoint explanation can
be established. Every flow's inbound decoded bytes and statuses remain identical
under the live authority policy. A short length alone does not identify a message.

Compact checkpoint diagnostics include a frame SHA256, direction-specific marker
observation, numeric declaration when present, authority and action. An outbound
declaration is only an observed byte interpretation, not a validated compressed
size. No raw bytes are exported; the transition ring remains bounded to 256.

Regression fixtures independently construct the exported direction, offsets,
cursor and size with zero/above-limit/max-u32 declarations. They do not pretend to
be recovered wire bytes. Coverage includes 37 -> fresh 3F actor 14281, ongoing
inbound damage, outbound fake combat/profile, malformed inbound containers,
strict outbound framing, partial frames, transport gaps/conflicts, privacy,
wrong/coincidental markers, inclusive size bounds and interleaved checkpoint soak.
