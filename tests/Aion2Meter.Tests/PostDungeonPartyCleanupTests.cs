using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Aion2Meter.Replay;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;

namespace Aion2Meter.Tests;

public sealed class PostDungeonPartyCleanupTests
{
    private sealed record Claim(uint Id, string Name, byte Slot, uint Stable);
    private static Claim[] Claims(uint self = 200, uint first = 42001, uint key = 100) =>
        [new(self, "Local", 1, 1), .. Enumerable.Range(0, 4).Select(i =>
            new Claim(first + (uint)i, $"Unseen-{key}-{i}", (byte)(i + 2), key + (uint)i))];
    private static byte[] Text(string value) => [(byte)Encoding.UTF8.GetByteCount(value), .. Encoding.UTF8.GetBytes(value)];
    private static byte[] Member(Claim c) => [0, c.Slot, .. BitConverter.GetBytes(c.Id), 1, 0, 1, 0,
        36, .. Encoding.ASCII.GetBytes($"bc074234-29ed-4459-920a-{c.Stable:000000000000}"),
        .. BitConverter.GetBytes(c.Stable), 0, 0, 1, 0, .. Text(c.Name), .. new byte[4],
        .. Varint(123456), .. Varint(123456), .. new byte[48], 7, .. new byte[20]];
    private static byte[] Roster(params Claim[] cs) =>
        Frame([0, 0x92, 0, .. new byte[24], .. Varint((ulong)cs.Length), .. cs.SelectMany(Member), 0]);
    private static void Profiles(Harness h, Claim[] cs)
    { foreach (var c in cs.Skip(1)) h.Frame(PlayerProfileTests.Profile(8, c.Id, c.Name)); }
    private static void Establish(Harness h, Claim[] cs)
    { h.Frame(Roster(cs)); Profiles(h, cs); }
    private static void Reconnect(Harness h, uint self, uint seq)
    { h.Add([], flags: TcpFlags.Rst | TcpFlags.Ack); h.Tick(); h.Handshake(seq, seq + 10000); h.Bind("Local", self); }
    private static LiveMeterSnapshot Current(Harness h, OverlayViewModel view, int rows)
    {
        var s = h.Tick(); view.Apply(OverlaySnapshot.FromMeter(s));
        Assert.Equal(rows, s.CurrentMembers!.Count); Assert.Equal(rows, view.Rows.Count); return s;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LiveSequenceContractsRetiresAndCannotResurrectCarriedRow(bool authoritativeEmpty)
    {
        var h = Fresh(); var cs = Claims(); Establish(h, cs); var vm = new OverlayViewModel();
        Current(h, vm, 5);
        h.Frame(Hit(10)); foreach (var c in cs.Skip(1)) h.Frame(Hit(20, c.Id));
        var history = Current(h, vm, 5); Assert.Equal(90m, history.GroupTotalDamage);
        h.Frame(Roster(cs.Take(2).ToArray())); var contracted = Current(h, vm, 2);
        Assert.Equal(5, contracted.Members!.Count); Assert.Equal(2, contracted.CurrentMembers!.Count);
        h.Frame(Frame([0x2F, 0x92, 1, 2, 3, 4]));
        var retired = Current(h, vm, 2); Assert.Null(retired.CurrentMembers!.Single(m => !m.IsSelf).EntityId);
        Reconnect(h, 201, 5000);
        var fresh = Current(h, vm, 1);
        Assert.Equal(PartyAuthorityState.Unknown, fresh.CurrentPartyAuthority);
        Assert.Empty(fresh.PartyRoster!.Memberships!);
        Assert.Single(fresh.StablePartyIdentities!); // Unknown did not destroy stable identity.
        Assert.Equal(5, history.CurrentMembers!.Count); // Detached historical snapshot is unchanged.
        if (authoritativeEmpty)
        {
            h.Frame(Roster(cs[0] with { Id = 201 })); fresh = Current(h, vm, 1);
            Assert.Equal(PartyAuthorityState.KnownEmpty, fresh.CurrentPartyAuthority);
            Assert.Empty(fresh.StablePartyIdentities!);
        }
        h.Now = h.Now.AddSeconds(31); h.Frame(Hit(100, 201)); var combat = Current(h, vm, 1);
        Assert.Equal(100m, combat.TotalDamage);
        h.Meter.ResetCurrent(h.Now); var reset = Current(h, vm, 1); Assert.Equal(0m, reset.TotalDamage);
        var diagnostic = h.Meter.Diagnostics.Snapshot(h.Now).CurrentPartyPresentation!;
        Assert.Single(diagnostic.VisibleNames); Assert.Equal(0, diagnostic.MembershipCount);
        Assert.Equal(authoritativeEmpty ? "KnownEmpty" : "Unknown", diagnostic.Authority);
        using var json = JsonDocument.Parse(h.Meter.Diagnostics.ToJson(h.Now));
        Assert.Equal(1, json.RootElement.GetProperty("CurrentPartyPresentation").GetProperty("VisibleNames").GetArrayLength());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PersistentPartyReusesRowsAfterFreshProofAndCountsOnlyFutureActors(bool profileFirst)
    {
        var h = Fresh(); var old = Claims(); Establish(h, old); var vm = new OverlayViewModel();
        Current(h, vm, 5); var objects = vm.Rows.ToDictionary(r => r.DisplayName);
        h.Frame(Hit(99, old[1].Id)); h.Tick(); Reconnect(h, 201, 6000);
        var waiting = Current(h, vm, 1); Assert.Equal(4, waiting.StablePartyIdentities!.Count);
        var next = old.Select(c => c with { Id = c.Slot == 1 ? 201U : c.Id + 10000 }).ToArray();
        h.Frame(PlayerProfileTests.Profile(8, 64001, next[1].Name)); h.Frame(Hit(9999, 64001));
        if (profileFirst) Profiles(h, next);
        h.Frame(Hit(9999, next[1].Id)); h.Frame(Roster(next));
        if (!profileFirst) Profiles(h, next);
        foreach (var c in next.Skip(1)) h.Frame(Hit(11, c.Id));
        h.Frame(Hit(9999, old[1].Id)); var bound = Current(h, vm, 5);
        Assert.Equal(44m, bound.GroupTotalDamage); Assert.Equal(PartyAuthorityState.KnownRoster, bound.CurrentPartyAuthority);
        Assert.All(objects, item => Assert.Same(item.Value, vm.Rows.Single(r => r.DisplayName == item.Key)));
        Assert.All(bound.CurrentMembers!.Where(m => !m.IsSelf), m => Assert.Equal(PlayerClass.Gladiator, m.Class));
        Assert.All(bound.PartyRoster!.ActiveMembers, a => Assert.DoesNotContain(a.EntityId, old.Skip(1).Select(c => (ulong)c.Id)));
    }

    [Theory]
    [InlineData((byte)1, 591)] [InlineData((byte)9, 574)]
    public void RejectedObservedMaskShapesCannotChangeFourTrustedMembers(byte mask, int length)
    {
        var h = Fresh(); var cs = Claims(); Establish(h, cs); var before = h.Tick().PartyRoster!;
        // Only diagnostic-supported shape is known; the unseen body is not claimed to reproduce physical bytes.
        var body = new byte[length]; body[0] = mask; h.Frame(Frame([0, 0x92, .. body]));
        var after = h.Tick().PartyRoster!;
        Assert.Equal(before.Memberships, after.Memberships); Assert.Equal(before.ActiveMembers, after.ActiveMembers);
        Assert.Equal(before.RecentIntervals, after.RecentIntervals);
        Assert.Equal(before.AuthoritativeReplacementRevision, after.AuthoritativeReplacementRevision);
        Assert.Equal(PartyAuthorityState.KnownRoster, after.Authority);
        Assert.Equal("Rejected", after.LastLayout!.AuthorityDecision); Assert.Equal("NoMutation", after.LastLayout.MutationEffect);
        Assert.Equal(mask, after.LastLayout.RosterMask); Assert.Equal(length, after.LastLayout.BodyLength);
        Assert.Equal(25, after.LastLayout.ConsumedBodyBytes); Assert.Equal(length - 25, after.LastLayout.RemainingBodyBytes);
        foreach (var c in cs.Skip(1)) h.Frame(Hit(10, c.Id)); Assert.Equal(40m, h.Tick().GroupTotalDamage);
        h.Frame(Roster(cs.Take(2).ToArray())); Assert.Single(h.Tick().PartyRoster!.Memberships!);
    }

    [Fact]
    public void MalformedEndIsNoMutationWhileCompleteEndIsAuthoritative()
    {
        var h = Fresh(); Establish(h, Claims()); var before = h.Tick().PartyRoster!;
        h.Frame(Frame([0x13, 0x92, 0, 0, 9])); var rejected = h.Tick().PartyRoster!;
        Assert.Equal(before.Memberships, rejected.Memberships); Assert.Equal(before.ActiveMembers, rejected.ActiveMembers);
        Assert.Equal("NoMutation", rejected.LastLayout!.MutationEffect);
        h.Frame(PartyMeterTests.Disband()); var empty = h.Tick();
        Assert.Equal(PartyAuthorityState.KnownEmpty, empty.CurrentPartyAuthority); Assert.Single(empty.CurrentMembers!);
        Assert.Empty(empty.StablePartyIdentities!);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void RemoteCodeEightUpdatesOnlyValidatedMemberMetadata(bool before)
    {
        var h = Fresh(); var c = Claims()[1]; var vm = new OverlayViewModel();
        h.Frame(PlayerProfileTests.Profile(8, 65001, "Unrelated-New-Name")); h.Frame(Hit(9999, 65001));
        Current(h, vm, 1);
        if (before) h.Frame(PlayerProfileTests.Profile(8, c.Id, c.Name));
        h.Frame(Roster(Claims()[0], c)); Current(h, vm, 2);
        var row = vm.Rows.Single(r => !r.IsSelf);
        if (!before) h.Frame(PlayerProfileTests.Profile(8, c.Id, c.Name));
        h.Frame(Hit(21, c.Id)); var s = Current(h, vm, 2);
        Assert.Same(row, vm.Rows.Single(r => !r.IsSelf)); Assert.Equal(PlayerClass.Gladiator, row.Class);
        var proof = s.CurrentMembers!.Single(m => !m.IsSelf).ClassEvidence!;
        Assert.Equal(8U, proof.RawCode); Assert.Equal((byte)0, proof.Variant); Assert.Equal(PlayerClassSource.Profile4536, proof.Source);
        Assert.Equal(21m, s.GroupTotalDamage);
    }

    [Theory]
    [InlineData((byte)0x37)] [InlineData((byte)0x3F)]
    public void RemoteMappingDoesNotExtendLocalIdentityAuthority(byte marker)
    {
        var bytes = PlayerProfileTests.Profile(8, 70001, "Unseen-Local", true, marker);
        var p = PlayerProfileDecoder.Decode(PlayerProfileTests.Record(bytes, "3336"));
        Assert.True(p is null || p.Class == PlayerClass.Unknown);
        var h = new Harness(); h.Initialize(); h.Handshake(); h.Frame(bytes);
        Assert.NotEqual(CurrentPlayerBindingStatus.Resolved, h.Tick().BindingStatus);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FormerParticipantStaysInAccountingButNeverCurrentOrResetPreview(bool selfOnly)
    {
        var h = Fresh(); Establish(h, Claims()); h.Frame(Hit(10)); h.Frame(Hit(20, 42001));
        var old = h.Tick();
        h.Frame(selfOnly ? Roster(Claims()[0]) : PartyMeterTests.Disband()); var vm = new OverlayViewModel();
        var current = Current(h, vm, 1); Assert.Equal(5, old.CurrentMembers!.Count);
        Assert.Equal(20m, current.Members!.Single(m => m.EntityId == 42001).TotalDamage);
        Assert.Equal(100m, Assert.Single(current.CurrentMembers!).ContributionPercent);
        h.Meter.ResetCurrent(h.Now); Current(h, vm, 1);
        Establish(h, Claims()); Current(h, vm, 5);
    }

    [Fact]
    public void RepeatedLifecycleChurnHasBoundedStateAndNoUnchangedTickRowChurn()
    {
        var h = Fresh(); var vm = new OverlayViewModel();
        for (uint i = 0; i < 120; i++)
        {
            var cs = Claims(self: 200 + i, first: 42001 + i * 10, key: 100 + i * 10);
            if (i > 0) Reconnect(h, cs[0].Id, 100000 + i * 1000);
            Establish(h, cs); Current(h, vm, 5); foreach (var c in cs.Skip(1)) h.Frame(Hit(1, c.Id));
            Current(h, vm, 5); h.Frame(Roster(cs.Take(2).ToArray())); Current(h, vm, 2);
            h.Frame(PartyMeterTests.Disband()); var last = Current(h, vm, 1);
            h.Meter.ResetCurrent(h.Now); Current(h, vm, 1);
            Establish(h, cs); var final = Current(h, vm, 5);
            Assert.True(final.PartyRoster!.RecentIntervals.Count <= 16);
            Assert.True(final.StablePartyIdentities!.Count <= 5);
            Assert.True(h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Count <= LiveDiagnosticBuffer.Capacity);
            Assert.Equal(0, h.Pipeline.RetainedPayloadBytes); Assert.Equal(0, h.Feed.RetainedPartyRecordIdentities);
            Assert.Equal(PartyAuthorityState.KnownEmpty, last.CurrentPartyAuthority);
        }
        var changes = 0;
        ((INotifyCollectionChanged)vm.Rows).CollectionChanged += (_, _) => changes++;
        for (var i = 0; i < 100; i++) Current(h, vm, 5);
        Assert.Equal(0, changes);
        var field = typeof(OverlayViewModel).GetField("dormantRows", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(((IDictionary)field.GetValue(vm)!).Count <= 6);
        Assert.Equal(16, h.Meter.Diagnostics.Snapshot(h.Now).RecentRemoteProfiles!.Count);
        Assert.Equal(32, h.Meter.Diagnostics.Snapshot(h.Now).RecentCombatAttribution!.Count);
    }
}
