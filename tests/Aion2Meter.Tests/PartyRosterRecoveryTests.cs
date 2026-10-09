using System.Text;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;
using static Aion2Meter.Tests.PartyMeterTests;

namespace Aion2Meter.Tests;

public sealed class PartyRosterRecoveryTests
{
    // Independently constructed fixtures for the two observed, completely consumed roster forms.
    // No captured UUID, token, name, runtime ID or game bytes are embedded in these fixtures.
    private static byte[] Member(uint id, string name, byte slot, bool first)
    {
        var text = Encoding.UTF8.GetBytes(name);
        var uuid = Encoding.ASCII.GetBytes($"12345678-1234-5678-9012-{id:000000000000}");
        return [0, slot, .. BitConverter.GetBytes(id), 1, 0, 1, 0, 36, .. uuid,
            .. BitConverter.GetBytes((ulong)id), checked((byte)text.Length), .. text,
            .. new byte[56], .. first ? new byte[] { 0x3F } : Array.Empty<byte>(), .. new byte[21]];
    }
    private static byte[] Body(uint self = 200, uint remote = 300, string localName = "Local", string remoteName = "Remote", bool selfSecond = false) =>
        [0, 0x92, 8, .. new byte[24], 5, .. new byte[120], 2,
            .. Member(selfSecond ? remote : self, selfSecond ? remoteName : localName, 1, true),
            .. Member(selfSecond ? self : remote, selfSecond ? localName : remoteName, 2, false), 5];
    private static byte[] Roster(uint self = 200, uint remote = 300, string localName = "Local", string remoteName = "Remote", bool selfSecond = false) =>
        Frame(Body(self, remote, localName, remoteName, selfSecond));
    private static LiveMeterMemberSnapshot RemoteRow(LiveMeterSnapshot s) => Assert.Single(s.Members!, m => !m.IsSelf);
    private static void Restore(Harness h, uint self = 200, uint remote = 300, string local = "Local", string name = "Remote")
    { h.Frame(Identity(remote, name)); h.Frame(Roster(self, remote, local, name)); }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void FreshReconnectRestoresOnlyNewEpochIdentityAndAccounting(bool checkpoints)
    {
        var h = Fresh(checkpoints: checkpoints);
        h.Frame(Identity()); h.Frame(Invite()); h.Frame(Join()); h.Frame(Hit(400, 300)); var old = h.Tick();
        Assert.Equal(400m, RemoteRow(old).TotalDamage);
        h.Handshake(9000, 19000); h.Bind("Next", 201);
        Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
        Restore(h, 201, 301, "Next"); h.Frame(Hit(1296, 301)); h.Frame(Hit(624, 201)); h.Frame(Hit(326, 201));
        h.Frame(Hit(9999, 300)); h.Frame(Hit(9999, 777)); var current = h.Tick();
        Assert.NotEqual(old.EpochId, current.EpochId); Assert.Equal(201UL, current.EntityId);
        Assert.Equal(301UL, RemoteRow(current).EntityId); Assert.Equal(1296m, RemoteRow(current).TotalDamage);
        Assert.Equal(950m, current.TotalDamage); Assert.Equal(2246m, current.GroupTotalDamage);
        Assert.Equal(current.EpochId, Assert.Single(current.PartyRoster!.ActiveMembers).EpochId);
        Assert.DoesNotContain(current.Members!, m => m.EntityId == 300);
        Assert.Equal(3, current.OtherCount); Assert.Equal(2, current.Members!.Count);
    }

    [Fact]
    public void AlreadyFormedPartyNeedsFreshRosterAndIdentityButNoInvite()
    {
        var h = Fresh(); Restore(h); var m = h.Tick();
        Assert.Equal(2, m.Members!.Count); var member = Assert.Single(m.PartyRoster!.ActiveMembers);
        Assert.Equal("0092", member.Evidence.Tag); Assert.Equal("4536", member.IdentityEvidence!.Tag);
        Assert.Null(member.InviteEvidence); Assert.NotNull(member.MemberUuid); Assert.NotNull(member.OpaqueToken);
        Assert.Equal(0m, m.GroupTotalDamage);
    }

    [Fact]
    public void LaterFullRosterStrengthensStatusIdentityAndKeepsTheSameRowAndInterval()
    {
        var h = Fresh(); h.Frame(LivePartyStatusTests.Status()); h.Frame(Identity()); h.Frame(Hit(100, 300));
        var before = h.Tick(); var row = RemoteRow(before); var from = before.PartyRoster!.ActiveMembers[0].ValidFrom;
        h.Frame(Roster()); h.Frame(Hit(200, 300)); var after = h.Tick();
        Assert.Equal(row.MembershipKey, RemoteRow(after).MembershipKey); Assert.Equal(300m, RemoteRow(after).TotalDamage);
        Assert.Equal(2, after.Members!.Count); var member = Assert.Single(after.PartyRoster!.ActiveMembers);
        Assert.Equal(from, member.ValidFrom); Assert.Equal("0092", member.Evidence.Tag); Assert.Equal("1B92", member.StatusEvidence!.Tag);
        Assert.NotNull(member.MemberUuid); Assert.NotNull(member.OpaqueToken);
        h.Frame(Roster()); var repeated = h.Tick(); Assert.Equal(row.MembershipKey, RemoteRow(repeated).MembershipKey);
        Assert.Equal("party-status/300", Assert.Single(repeated.PartyRoster!.Memberships!).Key);
    }

    [Fact]
    public void InitializationRosterWaitsAcrossCheckpointsForSelfAndRemoteIdentity()
    {
        var h = new Harness(); h.Initialize(); h.Handshake(); h.Frame(Roster());
        Assert.Empty(h.Tick().Members!); h.Bind(); Assert.Equal(2, h.Tick().Members!.Count);
        Assert.Null(RemoteRow(h.Tick()).EntityId);
        h.Frame(Hit(999, 300)); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        h.Frame(Identity()); var identityAt = h.Now.AddMilliseconds(-10); h.Frame(Hit(100, 300)); var m = h.Tick();
        Assert.Equal(100m, RemoteRow(m).TotalDamage);
        Assert.Equal(identityAt, Assert.Single(m.PartyRoster!.ActiveMembers).ValidFrom);
        Assert.True(h.Pipeline.CheckpointCount > 0); Assert.Equal(0, h.Feed.RetainedPartyRecordIdentities);
    }

    [Fact]
    public void BindingCanBeLastProofWithoutAnotherRosterOrIdentityMessage()
    {
        var h = new Harness(); h.Initialize(); h.Handshake(); h.Frame(Roster()); h.Frame(Identity());
        Assert.Empty(h.Tick().Members!); h.Bind(); var m = h.Tick();
        Assert.Equal(2, m.Members!.Count); Assert.Equal(m.IdentityValidFrom, Assert.Single(m.PartyRoster!.ActiveMembers).ValidFrom);
        h.Frame(Hit(100, 300)); Assert.Equal(100m, RemoteRow(h.Tick()).TotalDamage);
    }

    [Fact]
    public void WholeBatchDoesNotApplyLaterIdentityToPastCombat()
    {
        var h = Fresh(checkpoints: false); h.Frame(Roster()); h.Frame(Hit(999, 300));
        h.Frame(Identity()); h.Frame(Hit(100, 300)); var m = h.Tick();
        Assert.Equal(100m, RemoteRow(m).TotalDamage); Assert.Equal(1, RemoteRow(m).Hits);
        Assert.Equal(2, m.OtherCount);
    }

    [Fact]
    public void NamesAndCombatWithoutRosterNeverRestoreParty()
    {
        var h = Fresh(); h.Frame(Identity()); h.Frame(Hit(999, 300)); var m = h.Tick();
        Assert.Single(m.Members!); Assert.Empty(m.PartyRoster!.ActiveMembers); Assert.Equal(0m, m.GroupTotalDamage);
    }

    [Theory]
    [InlineData("count-zero")] [InlineData("count-three")] [InlineData("count-noncanonical")]
    [InlineData("truncated")] [InlineData("trailing")]
    [InlineData("utf8")] [InlineData("mask")] [InlineData("lanes")]
    [InlineData("layout")] [InlineData("slot")] [InlineData("two-without-optional")]
    public void UnrecognizedOrIncompleteSnapshotDoesNotMutateTrustedParty(string mutation)
    {
        var h = Fresh(); Restore(h); var before = h.Tick().PartyRoster!; Assert.Equal(2, h.Tick().Members!.Count);
        var body = Body(); const int count = 148, first = 149;
        var second = first + 56 + Encoding.UTF8.GetByteCount("Local") + 78;
        switch (mutation)
        {
            case "count-zero": body[count] = 0; break;
            case "count-three": body[count] = 3; break;
            case "count-noncanonical": body = [.. body[..count], 0x82, 0, .. body[(count + 1)..]]; break;
            case "truncated": body = body[..^12]; break;
            case "trailing": body = [.. body, 9]; break;
            case "utf8": body[second + 56] = 0xFF; break;
            case "mask": body[2] = 9; break;
            case "lanes": body[28] = 1; break;
            case "layout": body[first + 56 + 5 + 56] = 0x07; break;
            case "slot": body[second + 1] = 1; break;
            case "two-without-optional": body = [.. body[..2], 0, .. body[3..27], .. body[148..]]; break;
        }
        h.Frame(Frame(body)); h.Frame(Hit(999, 300)); var m = h.Tick();
        Assert.Equal(before.Memberships, m.PartyRoster!.Memberships); Assert.Equal(before.ActiveMembers, m.PartyRoster.ActiveMembers);
        Assert.Equal(2, m.Members!.Count); Assert.Equal(999m, m.GroupTotalDamage);
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, m.BindingStatus); Assert.NotNull(m.PartyRoster.Diagnostic);
        Assert.Equal("NoMutation", m.PartyRoster.LastLayout!.MutationEffect);
    }

    [Theory]
    [InlineData("runtime")] [InlineData("uuid")] [InlineData("token")]
    public void DuplicateMemberFactsCannotCreateAnAuthoritativeList(string duplicate)
    {
        var h = Fresh(); h.Frame(Identity()); var body = Body(); const int first = 149;
        var second = first + 56 + 5 + 78;
        var (offset, length) = duplicate switch { "runtime" => (2, 4), "uuid" => (11, 36), _ => (47, 8) };
        body.AsSpan(first + offset, length).CopyTo(body.AsSpan(second + offset, length));
        h.Frame(Frame(body)); Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
    }

    [Theory]
    [InlineData("Self")] [InlineData("Remote")]
    public void ConflictingIndependentIdentityDoesNotPromoteOrRetainParty(string conflict)
    {
        var h = Fresh(); Restore(h); h.Tick();
        h.Frame(conflict == "Self" ? Roster(localName: "Wrong") : Roster(remoteName: "Wrong"));
        h.Frame(Hit(999, 300)); Assert.Empty(h.Tick().PartyRoster!.ActiveMembers); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        if (conflict == "Remote")
        {
            h.Frame(Identity(300, "Another")); h.Frame(Roster()); h.Frame(Identity());
            Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
        }
    }

    [Fact]
    public void DuplicateRefreshKeepsMembershipStartAndReusableRows()
    {
        var h = Fresh(); Restore(h); h.Frame(Hit(100, 300)); var initial = h.Tick();
        var from = Assert.Single(initial.PartyRoster!.ActiveMembers).ValidFrom;
        var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(initial)); var rows = vm.Rows.ToArray();
        for (var i = 0; i < 5; i++) { h.Frame(Roster()); vm.Apply(OverlaySnapshot.FromMeter(h.Tick())); }
        Assert.Equal(from, Assert.Single(h.Tick().PartyRoster!.ActiveMembers).ValidFrom);
        Assert.Empty(h.Tick().PartyRoster!.RecentIntervals); Assert.Equal(100m, RemoteRow(h.Tick()).TotalDamage);
        Assert.All(rows, r => Assert.Contains(vm.Rows, now => ReferenceEquals(r, now)));
    }

    [Theory]
    [InlineData(11)] [InlineData(47)]
    public void ChangedUuidOrTokenForActiveRuntimeIdWithdrawsEligibility(int field)
    {
        var h = Fresh(); Restore(h); h.Tick(); var body = Body();
        var second = 149 + 56 + 5 + 78;
        // UUID remains syntactically valid; this is an identity conflict, not malformed framing.
        body[second + field] = field == 11 ? (byte)'9' : (byte)99;
        h.Frame(Frame(body)); h.Frame(Hit(999, 300));
        Assert.Empty(h.Tick().PartyRoster!.ActiveMembers); Assert.Equal(0m, h.Tick().GroupTotalDamage);
    }

    [Fact]
    public void LaterReplacementSupersedesPendingRosterBeforeIdentityArrives()
    {
        var h = Fresh(); h.Frame(Roster()); h.Tick();
        h.Frame(Roster(remote: 301, remoteName: "Next")); h.Frame(Identity());
        Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
        h.Frame(Identity(301, "Next")); h.Frame(Hit(999, 300)); h.Frame(Hit(100, 301));
        Assert.Equal(301UL, RemoteRow(h.Tick()).EntityId); Assert.Equal(100m, h.Tick().GroupTotalDamage);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnprovedReplacementCannotResurrectMembershipThroughAnOldInvite(bool contradiction)
    {
        var h = Fresh(); h.Frame(Identity()); h.Frame(Invite()); h.Frame(Join()); h.Tick();
        h.Frame(contradiction ? Roster(localName: "Wrong") : Roster(remote: 301, remoteName: "Next"));
        h.Tick(); h.Frame(Join()); h.Frame(Hit(999, 300)); var m = h.Tick();
        Assert.Empty(m.PartyRoster!.ActiveMembers); Assert.DoesNotContain(m.Members!, row => row.EntityId == 300);
        Assert.Equal(contradiction ? 1 : 2, m.Members!.Count); Assert.Equal(0m, m.GroupTotalDamage);
    }

    [Fact]
    public void AuthoritativeReplacementTerminatesAbsentMemberAndFreezesCurrentRow()
    {
        var h = Fresh(); Restore(h); h.Frame(Hit(100, 300)); h.Tick();
        Restore(h, remote: 301, name: "Next"); h.Frame(Hit(999, 300)); h.Frame(Hit(200, 301)); var m = h.Tick();
        Assert.Equal(301UL, Assert.Single(m.PartyRoster!.ActiveMembers).EntityId);
        Assert.Equal("0092", Assert.Single(m.PartyRoster.RecentIntervals).TerminationEvidence!.Tag);
        Assert.Equal(300m, m.GroupTotalDamage); Assert.Equal(3, m.Members!.Count);
        Assert.False(Assert.Single(m.Members, p => p.EntityId == 300).ActivePartyMember);
        h.Now = h.Now.AddSeconds(31); h.Frame(Hit(10)); m = h.Tick();
        Assert.DoesNotContain(m.Members!, p => p.EntityId == 300); Assert.Equal(10m, m.GroupTotalDamage);
        h.Frame(Leave()); Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DisbandRemovesStableRowsButFreshEpochWithholdsOnlyRuntime(bool reconnect)
    {
        var h = Fresh(); h.Frame(Roster()); h.Tick();
        if (reconnect) { h.Handshake(9000, 19000); h.Bind(); }
        else h.Frame(Disband());
        h.Frame(Identity()); h.Frame(Hit(999, 300)); var m = h.Tick();
        Assert.Empty(m.PartyRoster!.ActiveMembers); Assert.Equal(reconnect ? 2 : 1, m.Members!.Count); Assert.Equal(0m, m.GroupTotalDamage);
        if (reconnect) Assert.Null(RemoteRow(m).EntityId);
    }

    [Fact]
    public void TwoMemberFormSupportsSelfSecondAndStrictUnicodeByteLengths()
    {
        var h = Fresh(); h.Frame(Identity(50000, "Étranger"));
        h.Frame(Roster(remote: 50000, remoteName: "Étranger", selfSecond: true)); h.Frame(Hit(100, 50000));
        Assert.Equal("Étranger", RemoteRow(h.Tick()).CharacterName); Assert.Equal(100m, RemoteRow(h.Tick()).TotalDamage);
    }

    [Fact]
    public void PendingReplacementIsCompactBoundedAndReleasedOnUnknownTransition()
    {
        var resolver = new PartyRosterResolver("synthetic");
        for (var i = 0; i < 100; i++) resolver.Observe(Record(Roster(), i), null, null, null);
        Assert.Equal(2, resolver.RetainedReplacementMemberCount); Assert.Equal(0, resolver.RetainedIdentityCount);
        resolver.Observe(Record(Frame([0x21, 0x92, 0]), 101), null, null, null);
        Assert.Equal(0, resolver.RetainedReplacementMemberCount);
        resolver.Observe(Record(Roster(), 102), null, null, null); resolver.EndEpoch();
        Assert.Equal(0, resolver.RetainedReplacementMemberCount); Assert.Empty(resolver.Snapshot().ActiveMembers);
    }

    [Fact]
    public void ProofDiagnosticsDistinguishRosterClaimFromEligibleActorAndStayBounded()
    {
        var h = Fresh(); h.Frame(Roster()); var pending = h.Tick();
        var proof = pending.PartyRoster!.Proofs!;
        Assert.Equal(1, proof.RosterRecords); Assert.Equal(2, proof.PendingReplacement.Count);
        Assert.Contains(proof.PendingReplacement, p => p.RosterEntityId == 300 && !p.IndependentIdentityMatches);
        Assert.Empty(pending.PartyRoster.ActiveMembers); Assert.Equal(2, pending.Members!.Count);
        Assert.Null(RemoteRow(pending).EntityId); Assert.Null(RemoteRow(pending).Dps);
        h.Frame(Hit(999, 300)); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        h.Frame(Identity()); var bound = h.Tick();
        Assert.Empty(bound.PartyRoster!.Proofs!.PendingReplacement);
        Assert.Equal(0m, RemoteRow(bound).TotalDamage); // No retroactive attribution.
        h.Frame(Hit(100, 300)); Assert.Equal(100m, RemoteRow(h.Tick()).TotalDamage);
        for (var i = 0; i < 50; i++) { h.Frame(Roster()); h.Tick(); }
        var compact = h.Tick().PartyRoster!.Proofs!;
        Assert.Equal(51, compact.RosterRecords); Assert.Equal(8, compact.RecentTransitions.Count);
        Assert.Empty(compact.PendingReplacement); Assert.All(compact.RecentTransitions, t => Assert.Equal("0092", t.Tag));
        h.Handshake(9000, 19000); h.Bind(); var fresh = h.Tick();
        Assert.Empty(fresh.PartyRoster!.Proofs!.RecentTransitions); Assert.Equal(0, fresh.PartyRoster.Proofs.RosterRecords);
        Assert.Equal(2, fresh.Members!.Count); Assert.Null(RemoteRow(fresh).EntityId); Assert.Equal(0m, fresh.GroupTotalDamage);
    }

    [Fact]
    public void SameEpochRefreshRetainsPartyAndAbsenceNeverExpiresMembershipOrAdmitsOther()
    {
        var h = Fresh(); Restore(h); var initial = h.Tick(); var member = Assert.Single(initial.PartyRoster!.ActiveMembers);
        h.Now = h.Now.AddMinutes(10); h.Frame(Frame([0x33, 0x36, .. Varint(200), 5, .. Encoding.UTF8.GetBytes("Local")]));
        h.Frame(Hit(99999, 999)); var absent = h.Tick();
        Assert.Empty(absent.PartyRoster!.ActiveMembers);
        Assert.Equal(member.MembershipKey, Assert.Single(absent.PartyRoster.Memberships!).Key);
        Assert.Null(RemoteRow(absent).EntityId);
        Assert.Equal(0m, RemoteRow(absent).TotalDamage); Assert.Equal(0m, absent.GroupTotalDamage);
        h.Frame(Hit(999, 300)); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        h.Frame(Identity()); h.Frame(Hit(200, 300)); Assert.Equal(200m, RemoteRow(h.Tick()).TotalDamage);
        Assert.Equal(2, h.Tick().Members!.Count);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void AuthoritativeFarMemberHasNoRuntimeUntilExactLateIdentityAndKeepsItsRow(bool checkpoints)
    {
        var h = Fresh(checkpoints: checkpoints); h.Frame(Roster()); var far = h.Tick();
        var membership = Assert.Single(far.PartyRoster!.Memberships!);
        Assert.Equal("Remote", membership.CharacterName); Assert.Null(membership.CurrentRuntimeEntityId);
        Assert.Equal(300UL, membership.RosterEntityIdCandidate); Assert.Empty(far.PartyRoster.ActiveMembers);
        var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(far));
        var row = Assert.Single(vm.Rows, r => !r.IsSelf);
        h.Now = h.Now.AddMinutes(3);
        if (checkpoints) h.Frame(Frame([0x33, 0x36, .. Varint(200), 5, .. Encoding.UTF8.GetBytes("Local")]));
        h.Frame(Identity(999, "Remote")); h.Frame(Hit(9999, 999)); h.Frame(Hit(9999, 300));
        var stillFar = h.Tick(); Assert.Equal(0m, stillFar.GroupTotalDamage);
        Assert.Null(RemoteRow(stillFar).EntityId); Assert.Equal(2, stillFar.Members!.Count);
        h.Meter.ResetCurrent(h.Now); Assert.Single(h.Tick().PartyRoster!.Memberships!);
        h.Frame(Identity()); var bound = h.Tick();
        Assert.Equal(300UL, Assert.Single(bound.PartyRoster!.Memberships!).CurrentRuntimeEntityId);
        Assert.Equal(0m, RemoteRow(bound).TotalDamage); // Never backfill earlier Other.
        vm.Apply(OverlaySnapshot.FromMeter(bound)); Assert.Same(row, Assert.Single(vm.Rows, r => !r.IsSelf));
        h.Frame(Hit(200, 300)); h.Frame(Hit(9999, 999)); var hit = h.Tick();
        Assert.Equal(200m, hit.GroupTotalDamage); Assert.Equal(1, RemoteRow(hit).Hits);
        vm.Apply(OverlaySnapshot.FromMeter(hit)); Assert.Same(row, Assert.Single(vm.Rows, r => !r.IsSelf));
        h.Frame(Leave()); h.Tick(); vm.Apply(OverlaySnapshot.FromMeter(h.Tick()));
        Assert.DoesNotContain(vm.Rows, r => !r.IsSelf); // CURRENT uses current membership; accounting keeps history.
        Assert.Equal(200m, RemoteRow(h.Tick()).TotalDamage);
        Assert.Empty(h.Tick().PartyRoster!.Memberships!);
        h.Now = h.Now.AddSeconds(31); h.Frame(Hit(10)); Assert.Single(h.Tick().Members!);
    }

    [Theory]
    [InlineData("name")] [InlineData("disband")] [InlineData("epoch")]
    public void ConflictTerminationRemoveStableMembershipAndFreshEpochOnlyWithdrawsRuntime(string transition)
    {
        var h = Fresh(); h.Frame(Roster()); Assert.Null(RemoteRow(h.Tick()).EntityId);
        switch (transition)
        {
            case "name": h.Frame(Identity(300, "Wrong")); break;
            case "unknown-transition": h.Frame(Frame([0x21, 0x92, 0])); break;
            case "disband": h.Frame(Disband()); break;
            case "epoch": h.Handshake(9000, 19000); h.Bind(); break;
        }
        h.Frame(Identity()); h.Frame(Hit(999, 300)); var m = h.Tick();
        Assert.Empty(m.PartyRoster!.Memberships!); Assert.Empty(m.PartyRoster.ActiveMembers);
        Assert.Equal(transition == "epoch" ? 2 : 1, m.Members!.Count); Assert.Equal(0m, m.GroupTotalDamage);
        if (transition == "epoch") Assert.Null(RemoteRow(m).EntityId);
    }

    [Fact]
    public void RestoredMembershipSurvivesBoundedCheckpointsWithoutDuplicateDamage()
    {
        var h = Fresh(); Restore(h); h.Tick();
        for (var i = 0; i < 50; i++) { h.Frame(Roster()); h.Frame(Hit(10, 300)); h.Tick(); }
        Assert.Equal(500m, RemoteRow(h.Tick()).TotalDamage); Assert.Equal(50, RemoteRow(h.Tick()).Hits);
        Assert.Equal(0, h.Pipeline.RetainedPayloadBytes); Assert.Equal(0, h.Feed.RetainedPartyRecordIdentities);
        Assert.True(h.Pipeline.CheckpointCount > 50); Assert.Single(h.Tick().PartyRoster!.ActiveMembers);
    }

    [Fact]
    public void RandomOtherDoesNotChangeRestoredGroupClockOrContribution()
    {
        var h = Fresh(); Restore(h); h.Frame(Hit(100)); h.Frame(Hit(100, 300)); var before = h.Tick();
        h.Now = h.Now.AddSeconds(20); h.Frame(Hit(99999, 999)); var after = h.Tick();
        Assert.Equal(before.EncounterElapsedSeconds, after.EncounterElapsedSeconds);
        Assert.Equal(200m, after.GroupTotalDamage); Assert.All(after.Members!, m => Assert.Equal(50m, m.ContributionPercent));
        h.Frame(Hit(1000, 300, category: 0x36)); Assert.Equal(200m, h.Tick().GroupTotalDamage);
        Assert.Contains("0x36 pending", h.Tick().Coverage);
    }

    private static RawProtocolRecord Record(byte[] bytes, int index) => new(index + 1, "synthetic", TrafficDirection.ServerToClient,
        index * 1000L, index + 1, index * 1000L, DateTimeOffset.UnixEpoch.AddSeconds(index), index + 1, index + 1,
        DateTimeOffset.UnixEpoch.AddSeconds(index), ApplicationFraming.Read(bytes).PrefixLength, bytes.Length, bytes,
        Convert.ToHexString(bytes.AsSpan(ApplicationFraming.Read(bytes).PrefixLength, 2)), [], "Unknown", []);
}
