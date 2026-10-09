using System.Text;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Aion2Meter.Replay;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;
using static Aion2Meter.Tests.PartyMeterTests;
namespace Aion2Meter.Tests;

public sealed class MatchmakingPartyTests
{
    private sealed record Claim(uint Id, string Name, byte Slot, ushort Server, uint Stable, byte Mask = 0, ulong? Numeric = null);
    private static readonly string[] Names = ["AlphaTest", "BetaTest", "GammaTest", "DeltaTest"];
    private static Claim[] Claims(bool unresolved = false, uint self = 200, uint first = 41001) =>
        [new(self, "Local", 1, 1, 1), .. Names.Select((n, i) => new Claim(unresolved ? 0 : first + (uint)i, n, (byte)(i + 2), (ushort)(3000 + i), (uint)(i + 2), i % 2 == 0 ? (byte)3 : (byte)0))];
    private static byte[] Text(string s) => [(byte)Encoding.UTF8.GetByteCount(s), .. Encoding.UTF8.GetBytes(s)];
    private static byte[] Member(Claim c) => [c.Mask,c.Slot,..BitConverter.GetBytes(c.Id),..BitConverter.GetBytes(c.Server),..BitConverter.GetBytes((ushort)6000),
        36,..Encoding.ASCII.GetBytes($"87ef4168-a5ca-4f47-bbc0-{c.Stable:000000000000}"),..BitConverter.GetBytes(c.Stable),0,0,..BitConverter.GetBytes(c.Server),..Text(c.Name),
        ..new byte[4],..Varint(c.Numeric??(c.Id==0?0UL:123456UL)),..Varint(c.Numeric??(c.Id==0?0UL:123456UL)),..new byte[48],7,
        ..c.Mask is 3 or 7 ? Text("SyntheticGuild").Concat(new byte[4]).ToArray() : Array.Empty<byte>(),..new byte[c.Mask==7?26:20]];
    private static byte[] Roster(Claim[] cs) => Frame([0, 0x92, 0, .. new byte[24], .. Varint((ulong)cs.Length), .. cs.SelectMany(Member), 0]);
    private static byte[] NamedJoin(Claim c) => Frame([0x0D, 0x92, .. Member(c)]);
    private static Claim[] ZeroSelf(Claim[] cs) => [cs[0] with { Id = 0 }, .. cs.Skip(1)];
    private static void Profiles(Harness h, Claim[] cs, ushort port = 24001)
    { foreach (var c in cs.Skip(1).Where(c => c.Id != 0)) h.Frame(PlayerProfileTests.Profile(25, c.Id, c.Name), port: port); }
    private static void NoDamage(LiveMeterSnapshot s, int count = 5)
    { Assert.Equal(count, s.Members!.Count); Assert.All(s.Members, m => { Assert.Equal(0m, m.TotalDamage); Assert.Null(m.Dps); if (!m.IsSelf) Assert.Equal(0m, m.ContributionPercent); }); }
    [Theory]
    [InlineData(true, (byte)0x06)]
    [InlineData(true, (byte)0x26)]
    [InlineData(false, (byte)0x06)]
    [InlineData(false, (byte)0x26)]
    public void CurrentProfileBeforeOrAfterMembershipCountsOnlyFutureSupportedDamage(bool before, byte category)
    {
        var h = Fresh(); var claim = Claims()[1];
        var profile = PlayerProfileTests.Profile(25, claim.Id, claim.Name);
        h.Frame(Hit(9999, claim.Id));
        if (before) { h.Frame(profile); h.Tick(); }
        h.Frame(NamedJoin(claim)); h.Tick();
        var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(h.Tick()));
        var row = vm.Rows.Single(r => r.DisplayName == claim.Name);
        if (!before) { h.Frame(Hit(9999, claim.Id)); h.Frame(profile); }
        h.Frame(Hit(120, claim.Id, category == 0x26 ? [5UL, 5UL] : [], category));
        var s = h.Tick(); Assert.Equal(120m, s.GroupTotalDamage); Assert.Equal(1, Assert.Single(s.Members!, m => !m.IsSelf).Hits);
        vm.Apply(OverlaySnapshot.FromMeter(s)); Assert.Same(row, vm.Rows.Single(r => r.DisplayName == claim.Name));
        for (var i = 0; i < 3; i++) { h.Ack(); Assert.Equal(120m, h.Tick().GroupTotalDamage); }
        var d = h.Meter.Diagnostics.Snapshot(h.Now);
        var counted = Assert.Single(d.RecentCombatAttribution!, a => a.Decision == "Counted");
        Assert.Equal((ulong)category, counted.Category); Assert.Equal("ValidatedParty", counted.Classification); Assert.NotNull(counted.MatchedPartyKeyHash);
        var evidence = d.Transitions.Last(t => t.After.Any(m => m.RuntimeId == claim.Id)).After.Single();
        Assert.Equal("4536", evidence.ProfileTag); Assert.NotNull(evidence.ProfileAt);
        Assert.True(before ? evidence.ProfileAt <= evidence.MembershipFrom : evidence.ProfileAt > evidence.MembershipFrom,
            $"profile={evidence.ProfileAt:O}, membership={evidence.MembershipFrom:O}, before={before}");
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DifferentZoneActorRequiresIdBearingStableProofEvenWithCachedProfile(bool profileFirst)
    {
        var h = Fresh(); var old = Claims()[1]; var current = old with { Id = 61001 };
        h.Frame(NamedJoin(old)); var row = Assert.Single(h.Tick().Members!, m => !m.IsSelf); Assert.Null(row.EntityId);
        if (profileFirst) h.Frame(PlayerProfileTests.Profile(25, current.Id, current.Name));
        h.Frame(Hit(9999, current.Id)); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        h.Frame(NamedJoin(current));
        if (!profileFirst) { h.Frame(Hit(9999, current.Id)); h.Frame(PlayerProfileTests.Profile(25, current.Id, current.Name)); }
        h.Frame(Hit(19, current.Id)); h.Frame(Hit(9999, old.Id)); var s = h.Tick();
        Assert.Equal(19m, s.GroupTotalDamage); Assert.Equal(row.MembershipKey, Assert.Single(s.Members!, m => !m.IsSelf).MembershipKey);
        Assert.Equal(current.Id, Assert.Single(s.PartyRoster!.ActiveMembers).EntityId);
    }
    [Fact]
    public void LaterCompleteVariableWidthJoinSupersedesSoloAndStatusRefreshDoesNotDuplicate()
    {
        var h = Fresh(); var claims = Claims(); h.Frame(Roster([claims[0]]));
        var remote = claims[1] with { Mask = 0, Numeric = 16777216 };
        h.Frame(PlayerProfileTests.Profile(25, remote.Id, remote.Name)); h.Frame(Hit(9999, remote.Id));
        // The fixed 78-byte legacy suffix would leave bytes; this fixture exercises
        // canonical numeric widths in the independently observed shared row shape.
        h.Frame(NamedJoin(remote)); NoDamage(h.Tick(), 2);
        for (var i = 0; i < 5; i++) h.Frame(LivePartyStatusTests.Status(remote.Id));
        h.Frame(Identity(62002, "UnrelatedBeforeJoin")); h.Frame(Hit(9999, 62002)); h.Frame(Hit(23, remote.Id));
        var s = h.Tick(); Assert.Equal(2, s.Members!.Count); Assert.Equal(23m, s.GroupTotalDamage); Assert.Single(s.PartyRoster!.ActiveMembers);
        var d = h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Last(t => t.RecordTag == "0D92");
        Assert.Equal(0, d.MembersBefore); Assert.Equal(1, d.MembersAfter); Assert.Equal(0, d.PartyLayout!.RemainingBodyBytes);
    }
    [Fact]
    public void UnconsumedLegacyBytesAreMeasuredAndUnsupportedCombatRemainsDiagnosticOnly()
    {
        var h = Fresh(); var legacy = Join(); var f = Aion2Meter.Replay.Research.ApplicationFraming.Read(legacy);
        h.Frame(Frame([.. legacy.AsSpan(f.PrefixLength).ToArray(), 1, 2, 3])); h.Tick();
        var failure = h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Last(t => t.RecordTag == "0D92").PartyLayout!;
        Assert.Equal("Unconsumed party structure.", failure.RejectInvariant); Assert.Equal(3, failure.RemainingBodyBytes);
        Assert.Equal(failure.BodyLength, failure.ConsumedBodyBytes + failure.RemainingBodyBytes);
        var cs = Claims(); h.Frame(NamedJoin(cs[1])); h.Frame(PlayerProfileTests.Profile(25, cs[1].Id, cs[1].Name));
        for (var i = 0; i < 40; i++) h.Frame(Hit(9999, cs[1].Id, category: 0x36));
        Assert.Equal(0m, h.Tick().GroupTotalDamage); Assert.Empty(h.Events);
        var export = h.Meter.Diagnostics.Snapshot(h.Now); Assert.Equal(32, export.RecentCombatAttribution!.Count);
        Assert.All(export.RecentCombatAttribution, a => { Assert.Equal("UnsupportedCombatVariant", a.Decision); Assert.Equal(0x36UL, a.Category); Assert.Null(a.SourceEntityId); Assert.Null(a.Amount); });
        h.Ack(); h.Tick(); Assert.Equal(export.RecentCombatAttribution, h.Meter.Diagnostics.Snapshot(h.Now).RecentCombatAttribution);
        Assert.DoesNotContain("RawBytes", h.Meter.Diagnostics.ToJson(h.Now));
    }
    [Fact]
    public void AttributionDistinguishesUnboundPartyAndUnrelatedSourceWithoutGuessing()
    {
        var h = Fresh(); var c = Claims()[1]; h.Frame(NamedJoin(c)); h.Frame(Hit(9999, c.Id));
        h.Frame(PlayerProfileTests.Profile(25, c.Id, c.Name)); h.Frame(Hit(18, c.Id)); h.Frame(Hit(9999, 63003));
        var s = h.Tick(); Assert.Equal(18m, s.GroupTotalDamage);
        var a = h.Meter.Diagnostics.Snapshot(h.Now).RecentCombatAttribution!;
        var unbound = Assert.Single(a, a => a.Decision == "NoRuntimeBinding"); Assert.NotNull(unbound.MatchedPartyKeyHash);
        var other = Assert.Single(a, a => a.Decision == "NonPartyOther"); Assert.Null(other.MatchedPartyKeyHash);
        Assert.Contains((ulong)c.Id, other.ExpectedRuntimeIds);
        Assert.Single(a, a => a.Decision == "Counted" && a.Classification == "ValidatedParty");
    }
    [Fact]
    public void UnmodeledRemoteProfileBranchIsBoundedDiagnosticEvidenceAndCannotBindByName()
    {
        var h = Fresh(); var c = Claims()[1]; h.Frame(NamedJoin(c));
        for (var i = 0; i < 20; i++)
        {
            var profile = PlayerProfileTests.Profile(25, c.Id, c.Name);
            var f = Aion2Meter.Replay.Research.ApplicationFraming.Read(profile);
            var id = Aion2Meter.Replay.Research.UnsignedVarint.Read(profile.AsSpan(f.PrefixLength + 2), requireCanonical: true);
            profile[f.PrefixLength + 2 + id.BytesConsumed + 4] = 0x17;
            h.Frame(profile);
        }
        h.Frame(Hit(9999, c.Id)); var s = h.Tick(); Assert.Equal(0m, s.GroupTotalDamage);
        Assert.Null(Assert.Single(s.Members!, m => !m.IsSelf).EntityId);
        var observations = h.Meter.Diagnostics.Snapshot(h.Now).RecentRemoteProfiles!; Assert.Equal(16, observations.Count);
        Assert.All(observations, p => { Assert.Equal((byte)0x17, p.Marker); Assert.False(p.NameEnvelopeRecognized); Assert.False(p.ClassProfileRecognized); });
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FiveRowsBeforeCombatBindIndependentlyWithoutBackfill(bool checkpoints)
    {
        var h = Fresh(checkpoints: checkpoints); var cs = Claims(); h.Frame(Roster(cs)); var early = h.Tick(); NoDamage(early);
        Assert.All(early.PartyRoster!.Memberships!, m => { Assert.Null(m.CurrentRuntimeEntityId); Assert.NotNull(m.OriginServerId); Assert.NotNull(m.MemberSlot); });
        var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(early)); var rows = vm.Rows.ToDictionary(r => r.DisplayName);
        h.Frame(Hit(9999, cs[1].Id)); h.Frame(PlayerProfileTests.Profile(25, cs[1].Id, cs[1].Name)); h.Frame(Hit(11, cs[1].Id));
        var one = h.Tick(); Assert.Single(one.PartyRoster!.ActiveMembers); Assert.Equal(11m, one.GroupTotalDamage); Assert.Equal(5, one.Members!.Count);
        Profiles(h, cs); foreach (var c in cs.Skip(2)) h.Frame(Hit(10, c.Id)); h.Frame(Identity(49999, "Unrelated")); h.Frame(Hit(9999, 49999));
        var final = h.Tick(); Assert.Equal(41m, final.GroupTotalDamage); Assert.Equal(4, final.PartyRoster!.ActiveMembers.Count);
        Assert.DoesNotContain(final.Members!, m => m.EntityId == 49999); Assert.All(final.Members!.Where(m => !m.IsSelf), m => Assert.Equal(PlayerClass.Sorcerer, m.Class));
        vm.Apply(OverlaySnapshot.FromMeter(final)); Assert.All(rows, r => Assert.Same(r.Value, vm.Rows.Single(x => x.DisplayName == r.Key)));
    }
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)3)]
    [InlineData((byte)7)]
    public void StableZeroIdJoinAppearsBeforeInviteProfileOrDamage(byte mask)
    {
        var h = Fresh(); var c = Claims(true)[1] with { Mask = mask }; h.Frame(NamedJoin(c)); var s = h.Tick(); NoDamage(s, 2);
        var row = Assert.Single(s.Members!, m => !m.IsSelf); Assert.Equal(c.Name, row.CharacterName); Assert.Null(row.EntityId);
        h.Frame(Identity(49998, c.Name)); h.Frame(Hit(9999, 49998)); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        h.Frame(NamedJoin(c with { Id = 41001 })); h.Frame(PlayerProfileTests.Profile(25, 41001, c.Name)); h.Frame(Hit(12, 41001));
        var bound = h.Tick(); Assert.Equal(12m, bound.GroupTotalDamage); Assert.Equal(2, bound.Members!.Count); Assert.Equal(row.MembershipKey, Assert.Single(bound.Members, m => !m.IsSelf).MembershipKey);
    }
    [Fact]
    public void FourZeroIdsStayDistinctUntilCurrentStableClaimsArrive()
    {
        var h = Fresh(); h.Frame(Roster(Claims(true))); var before = h.Tick(); NoDamage(before); Assert.Equal(4, before.PartyRoster!.Memberships!.Select(m => m.Key).Distinct().Count());
        Profiles(h, Claims()); h.Frame(Hit(9999, 41001)); var waiting = h.Tick(); Assert.Empty(waiting.PartyRoster!.ActiveMembers); Assert.All(waiting.Members!.Where(m => !m.IsSelf), m => Assert.Null(m.EntityId));
        h.Frame(Roster(Claims())); h.Frame(Hit(21, 41001)); var bound = h.Tick(); Assert.Equal(21m, bound.GroupTotalDamage);
        Assert.Equal(before.StablePartyIdentities!.Select(m => m.StableKey), bound.StablePartyIdentities!.Select(m => m.StableKey));
    }
    [Fact]
    public void ZeroIdJoinRowsAreNotDuplicatedByEarlyStatusAndNameOnlyProfiles()
    {
        var h = Fresh(); var zero = Claims(true);
        foreach (var c in zero.Skip(1)) h.Frame(NamedJoin(c));
        NoDamage(h.Tick());
        var current = Claims();
        foreach (var c in current.Skip(1)) h.Frame(LivePartyStatusTests.Status(c.Id));
        Profiles(h, current); h.Frame(Hit(9999, current[1].Id));
        Assert.Equal(5, h.Tick().Members!.Count); Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
        Assert.Equal(0m, h.Tick().GroupTotalDamage);
        foreach (var c in current.Skip(1)) h.Frame(NamedJoin(c));
        h.Frame(Hit(17, current[1].Id));
        Assert.Equal(5, h.Tick().Members!.Count); Assert.Equal(17m, h.Tick().GroupTotalDamage);
    }
    [Fact]
    public void ZeroSelfRequiresIndependentlyKnownOriginAndName()
    {
        var h = new Harness(); h.Initialize(); h.Handshake(); h.Frame(PlayerProfileTests.Profile(29, 200, "Local", true)); h.Tick(); h.Frame(Roster(ZeroSelf(Claims(true)))); NoDamage(h.Tick());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroSelfCannotUseNameAloneOrAmbiguousOrigin(bool duplicate)
    {
        var h = Fresh(); var cs = ZeroSelf(Claims(true)); if (duplicate) { h.Frame(PlayerProfileTests.Profile(29, 200, "Local", true)); h.Tick(); cs[1] = cs[1] with { Name = "Local", Server = 1 }; }
        h.Frame(Roster(cs)); Assert.Single(h.Tick().Members!); Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackendHandoffReusesStableRowsAndCountsOnlyFutureValidatedActors(bool changedServer)
    {
        var h = Fresh(); var old = Claims(); h.Frame(Roster(old)); Profiles(h, old); h.Frame(Hit(50, old[1].Id)); var first = h.Tick();
        var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(first)); var rows = vm.Rows.ToDictionary(x => x.DisplayName);
        h.Add([], flags: TcpFlags.Rst | TcpFlags.Ack); if (changedServer) h.RemoteAddress = System.Net.IPAddress.Parse("203.0.113.83"); h.Handshake(5000, 15000, 25002); h.Bind("Local", 201, 25002); NoDamage(h.Tick());
        var current = Claims(self: 201, first: 51001); Profiles(h, current, 25002); h.Frame(Hit(9999, 51001), port: 25002); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        h.Frame(Roster(current), port: 25002); h.Frame(Hit(25, 51001), port: 25002); h.Frame(Hit(9999, old[1].Id), port: 25002); var next = h.Tick();
        Assert.Equal(25m, next.GroupTotalDamage); Assert.Equal(5, next.Members!.Count); vm.Apply(OverlaySnapshot.FromMeter(next)); Assert.All(rows, r => Assert.Same(r.Value, vm.Rows.Single(x => x.DisplayName == r.Key)));
    }
    [Fact]
    public void DuplicateNamesDifferentServersNeverChooseActorByName()
    {
        var h = Fresh(); var cs = Claims(true); cs[1] = cs[1] with { Name = "SameName" }; cs[2] = cs[2] with { Name = "SameName" }; h.Frame(Roster(cs));
        h.Frame(Identity(60001, "SameName")); h.Frame(LivePartyStatusTests.Status(60001)); h.Frame(Hit(9999, 60001)); Assert.Equal(5, h.Tick().Members!.Count); Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
        cs[1] = cs[1] with { Id = 41001 }; cs[2] = cs[2] with { Id = 41002 }; h.Frame(Roster(cs)); Profiles(h, cs); h.Frame(Hit(10, 41001)); h.Frame(Hit(20, 41002)); var s = h.Tick();
        var same = s.Members!.Where(m => m.CharacterName == "SameName").ToArray(); Assert.Equal(2, same.Length); Assert.NotEqual(same[0].MembershipKey, same[1].MembershipKey); Assert.Equal(30m, s.GroupTotalDamage);
    }
    [Fact]
    public void UnicodeNamesAreStrictAndNeverMatchedByPrefix()
    {
        var h = Fresh(); var cs = Claims(); cs[1] = cs[1] with { Name = "\u00C9tranger-\u65B0\u89D2\u8272" }; h.Frame(Roster(cs)); h.Frame(Identity(cs[1].Id, "\u00C9tranger")); h.Frame(Hit(9999, cs[1].Id));
        Assert.Empty(h.Tick().PartyRoster!.ActiveMembers); Assert.Equal(0m, h.Tick().GroupTotalDamage);
    }
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CardinalityAndUnorderedSlotsAreCountDirected(int count) { var h = Fresh(); h.Frame(Roster(Claims().Take(count).Reverse().ToArray())); NoDamage(h.Tick(), count); }
    [Fact]
    public void RejectedGrammarDoesNotDisableEarlyStatusOrRepeatAnOldError()
    {
        var h = Fresh(); h.Frame(Frame([0, 0x92, 0, .. new byte[24], 9])); h.Tick(); h.Frame(LivePartyStatusTests.Status(41001)); h.Frame(Identity(41001, "FreshStatusMember")); h.Frame(Hit(12, 41001));
        Assert.Equal(12m, h.Tick().GroupTotalDamage); Assert.Equal(2, h.Tick().Members!.Count); var d = h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Last(t => t.RecordTag == "1B92");
        Assert.Null(d.PartyLayout!.RejectInvariant); Assert.True(d.PartyLayout.HasRuntimeActor); Assert.DoesNotContain("cardinality", d.Reason); Assert.Contains("Validated inbound status", d.Reason);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SoloAndDisbandOutrankStaleStatusUntilNewJoin(bool disband)
    {
        var h = Fresh(); var cs = Claims(); h.Frame(Roster(cs)); Profiles(h, cs); h.Tick(); h.Frame(disband ? Disband() : Roster([cs[0]])); h.Frame(LivePartyStatusTests.Status(cs[1].Id)); h.Frame(Hit(9999, cs[1].Id));
        Assert.Single(h.Tick().Members!); Assert.Equal(0m, h.Tick().GroupTotalDamage); h.Frame(NamedJoin(cs[1])); h.Frame(Hit(15, cs[1].Id)); Assert.Equal(15m, h.Tick().GroupTotalDamage);
    }
    [Theory]
    [InlineData("count")]
    [InlineData("slot")]
    [InlineData("mask")]
    [InlineData("token")]
    [InlineData("uuid")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-uuid")]
    public void MalformedOrDuplicateLayoutsNeverAuthorize(string fault)
    {
        var cs = Claims(); var b = new List<byte>([0, 0x92, 0, .. new byte[24], 5, .. cs.SelectMany(Member), 0]);
        switch (fault)
        {
            case "count": b[27] = 6; break;
            case "slot": b[29] = 0; break;
            case "mask": b[28] = 9; break;
            case "token": b[81] ^= 1; break;
            case "uuid": b[39] = (byte)'Z'; break;
            case "truncated": b.RemoveRange(b.Count - 30, 30); break;
            case "trailing": b.AddRange([1, 2, 3, 4]); break;
            case "duplicate-id": cs[2] = cs[2] with { Id = cs[1].Id }; b = [0, 0x92, 0, .. new byte[24], 5, .. cs.SelectMany(Member), 0]; break;
            case "duplicate-uuid": cs[2] = cs[2] with { Stable = cs[1].Stable }; b = [0, 0x92, 0, .. new byte[24], 5, .. cs.SelectMany(Member), 0]; break;
        }
        var h = Fresh(); h.Frame(Frame(b.ToArray())); Profiles(h, cs); h.Frame(Hit(9999, cs[1].Id)); Assert.Single(h.Tick().Members!); Assert.Empty(h.Tick().PartyRoster!.ActiveMembers); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        var d = h.Meter.Diagnostics.Snapshot(h.Now).Transitions.First(t => t.RecordTag == "0092").PartyLayout!; Assert.Equal("Rejected", d.AuthorityDecision); Assert.NotNull(d.RejectInvariant); Assert.Equal("ServerToClient", d.Direction);
        Assert.Equal((byte)0, d.RosterMask); if (fault == "mask") Assert.Equal((byte)9, d.MemberMask);
    }
    [Fact]
    public void OutboundRosterNeverGrantsAuthority()
    { var h = Fresh(); var b = Roster(Claims()); h.Add(b, server: false); h.ClientSequence += (uint)b.Length; h.Add([], flags: TcpFlags.Ack); Profiles(h, Claims()); Assert.Single(h.Tick().Members!); }
    [Fact]
    public void TargetDiagnosticsAreBoundedResearchObservationsAndNeverBossOrPartyAuthority()
    {
        var h = Fresh(); h.Frame(Identity(60000, "ArbitraryOther")); for (var i = 0; i < 40; i++) { h.Frame(Hit(9999, 60000)); h.Tick(); }
        Assert.Single(h.Tick().Members!); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        var e = h.Meter.Diagnostics.Snapshot(h.Now); Assert.Equal(16, e.RecentCombatTargets!.Count); Assert.All(e.RecentCombatTargets, t => { Assert.Equal(60000UL, t.SourceEntityId); Assert.Equal("Other", t.ActorAssociation); Assert.NotEqual(0UL, t.TargetEntityId); Assert.NotEmpty(t.ProvenanceIdentity); });
        var json = h.Meter.Diagnostics.ToJson(h.Now); Assert.DoesNotContain("BossName", json); Assert.DoesNotContain("MaxHp", json); Assert.DoesNotContain("RawBytes", json);
    }
    [Fact]
    public void MatchmakingSceneReturnLeaveRejoinAnd150RefreshesStayBoundedWithoutDuplicates()
    {
        var h = Fresh(); var cs = Claims(); h.Frame(Roster(cs)); Profiles(h, cs); h.Tick();
        for (var i = 0; i < 150; i++)
        {
            if (i % 10 == 0) { h.Frame(ScenePartyLifetimeTests.Control()); h.Tick(); cs = cs.Select((c, n) => n == 0 ? c : c with { Id = c.Id + 10 }).ToArray(); h.Frame(Roster(cs)); Profiles(h, cs); }
            h.Frame(Roster(cs)); foreach (var c in cs.Skip(1)) { h.Frame(LivePartyStatusTests.Status(c.Id)); h.Frame(Hit(1, c.Id)); }
            h.Frame(Hit(9999, 60000)); var s = h.Tick();
            Assert.Equal(5, s.Members!.Count); Assert.Equal(4, s.PartyRoster!.ActiveMembers.Count); Assert.Equal(5, s.Members.Select(m => m.MembershipKey ?? "Self").Distinct().Count()); Assert.DoesNotContain(s.Members, m => m.EntityId == 60000);
            Assert.InRange(s.PartyRoster.RecentIntervals.Count, 0, 16); Assert.Equal(8, s.PartyRoster.Proofs!.RecentTransitions.Count); Assert.InRange(s.PartyRoster.Proofs.IndependentIdentities, 0, 4); Assert.InRange(s.PartyRoster.Proofs.RetainedStatuses, 0, 4);
            if (i % 25 == 24) { h.Frame(Disband()); h.Meter.ResetCurrent(h.Now); Assert.Single(h.Tick().Members!); h.Frame(Roster(cs)); Profiles(h, cs); }
        }
        h.Tick(); Assert.Equal(0, h.Feed.RetainedPartyRecordIdentities); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes); var e = h.Meter.Diagnostics.Snapshot(h.Now); Assert.Equal(256, e.Transitions.Count); Assert.Equal(16, e.RecentCombatTargets!.Count);
        var json = h.Meter.Diagnostics.ToJson(h.Now); Assert.DoesNotContain("RawBytes", json); Assert.DoesNotContain("ScopeId", json); using var parsed = JsonDocument.Parse(json);
    }
}
