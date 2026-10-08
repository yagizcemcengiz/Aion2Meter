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

public sealed class PlayerProfileTests
{
    internal static byte[] Profile(uint code, ulong id = 300, string name = "Remote", bool local = false,
        byte? marker = null, byte faction = 1) => Frame([local ? (byte)0x33 : (byte)0x45, 0x36, .. Varint(id),
            2, 3, 4, 5, marker ?? (local ? (byte)0x37 : (byte)7), (byte)Encoding.UTF8.GetByteCount(name),
            .. Encoding.UTF8.GetBytes(name), .. local ? new byte[] { 1, 0 } : Array.Empty<byte>(),
            .. BitConverter.GetBytes(code), faction, .. new byte[12]]);
    internal static RawProtocolRecord Record(byte[] bytes, string tag = "4536", string epoch = "test", int ordinal = 1,
        TrafficDirection direction = TrafficDirection.ServerToClient) =>
        new(ordinal, epoch, direction, ordinal, ordinal, ordinal, DateTimeOffset.UnixEpoch.AddSeconds(ordinal), ordinal,
            ordinal, DateTimeOffset.UnixEpoch.AddSeconds(ordinal), ApplicationFraming.Read(bytes).PrefixLength,
            bytes.Length, bytes, tag, [], "Unknown", []);

    private static Harness FreshProfile()
    {
        var h = new Harness(); h.Initialize(); h.Handshake();
        h.Frame(Frame([0x15, 0x36, .. BitConverter.GetBytes(200U), 5, .. Encoding.UTF8.GetBytes("Local")]));
        h.Frame(Profile(29, 200, "Local", true)); h.Tick(); return h;
    }

    [Theory]
    [InlineData(5, PlayerClass.Gladiator)] [InlineData(6, PlayerClass.Gladiator)]
    [InlineData(9, PlayerClass.Templar)] [InlineData(10, PlayerClass.Templar)]
    [InlineData(13, PlayerClass.Ranger)] [InlineData(14, PlayerClass.Ranger)]
    [InlineData(17, PlayerClass.Assassin)] [InlineData(18, PlayerClass.Assassin)]
    [InlineData(21, PlayerClass.Spiritmaster)] [InlineData(22, PlayerClass.Spiritmaster)]
    [InlineData(25, PlayerClass.Sorcerer)] [InlineData(26, PlayerClass.Sorcerer)]
    [InlineData(29, PlayerClass.Cleric)] [InlineData(30, PlayerClass.Cleric)]
    [InlineData(33, PlayerClass.Chanter)] [InlineData(34, PlayerClass.Chanter)]
    public void RemoteFixedProfileUsesAllEightIndependentClassBands(uint code, PlayerClass expected)
    {
        var r = Record(Profile(code, 50000, "Étranger")); var p = Assert.IsType<PlayerClassEvidence>(PlayerProfileDecoder.Decode(r));
        Assert.Equal(expected, p.Class); Assert.Equal(50000UL, p.EntityId); Assert.Equal("Étranger", p.CharacterName);
        Assert.Equal(PlayerClassSource.Profile4536, p.Source); Assert.Equal(code, p.RawCode); Assert.Equal(code % 4, p.Variant);
        Assert.Equal(r.CompletionUtc, p.ValidFrom); Assert.Equal(RecordProvenance.From(r), p.Provenance); Assert.Equal(64, p.RawSha256.Length);
    }

    [Theory]
    [InlineData(5)] [InlineData(9)] [InlineData(13)] [InlineData(17)]
    [InlineData(21)] [InlineData(25)] [InlineData(29)] [InlineData(33)]
    public void LocalBranchHasDistinctMarkerAndTwoBytesBeforeClass(uint code)
    {
        var local = PlayerProfileDecoder.Decode(Record(Profile(code, 200, "Local", true), "3336"))!;
        var remote = PlayerProfileDecoder.Decode(Record(Profile(code)))!;
        Assert.Equal(remote.Class, local.Class); Assert.Equal(PlayerClassSource.Character3336, local.Source);
        var directory = new PlayerProfileDirectory("test"); directory.Observe(Record(Profile(code, 200, "Local", true), "3336"));
        Assert.Null(directory.Get(200, "Local", false)); Assert.Null(directory.Get(200, "Local", true));
        Assert.NotNull(directory.Get(200, "Local", true, DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(0)] [InlineData(7)] [InlineData(8)] [InlineData(11)] [InlineData(36)] [InlineData(45)] [InlineData(uint.MaxValue)]
    public void UnsupportedCodesRemainUnknown(uint code) =>
        Assert.Equal(PlayerClass.Unknown, PlayerProfileDecoder.Decode(Record(Profile(code)))!.Class);

    [Theory]
    [InlineData("presence")] [InlineData("local-marker")] [InlineData("faction")] [InlineData("utf8")]
    [InlineData("framing")] [InlineData("tag")] [InlineData("direction")] [InlineData("suppressed")]
    [InlineData("warnings")] [InlineData("name-length")] [InlineData("truncated")]
    public void UnvalidatedProfilesDoNotScanForConvenientFields(string mutation)
    {
        var bytes = Profile(29); var r = Record(bytes);
        var bodyAt = r.PrefixLength + 2 + Varint(300).Length;
        switch (mutation)
        {
            case "presence": bytes[bodyAt + 4] = 0x17; break;
            case "local-marker": r = Record(Profile(29, local: true, marker: 0x3F), "3336"); break;
            case "faction": r = Record(Profile(29, faction: 3)); break;
            case "utf8": bytes[bodyAt + 6] = 0xFF; break;
            case "framing": r = r with { FrameLength = r.FrameLength + 1 }; break;
            case "tag": r = r with { OpcodeCandidate = "3336" }; break;
            case "direction": r = r with { Direction = TrafficDirection.ClientToServer }; break;
            case "suppressed": r = r with { DecodeStatus = "Suppressed" }; break;
            case "warnings": r = r with { DecodeWarnings = ["uncertain"] }; break;
            case "name-length": bytes[bodyAt + 5] = 0; break;
            case "truncated": r = Record(Frame([0x45, 0x36, .. Varint(300), 0, 0, 0, 0, 7, 1, 65, 29, 0])); break;
        }
        Assert.Null(PlayerProfileDecoder.Decode(r));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ConflictingDirectClassOrNameStaysUnknownUntilFreshScope(bool name)
    {
        var d = new PlayerProfileDirectory("test"); d.Observe(Record(Profile(29)));
        d.Observe(Record(Profile(name ? 29U : 5U, name: name ? "Wrong" : "Remote"), ordinal: 2));
        d.Observe(Record(Profile(29), ordinal: 3));
        Assert.True(d.Get(300, name ? "Wrong" : "Remote", false)!.Conflict);
        Assert.Equal(PlayerClass.Unknown, d.Get(300, name ? "Wrong" : "Remote", false)!.Class);
        d.Clear(); d.Observe(Record(Profile(5))); Assert.Equal(PlayerClass.Gladiator, d.Get(300, "Remote", false)!.Class);
    }

    [Fact]
    public void PairedVariantsDoNotChangeClassOrInventFactionMeaning()
    {
        var a = PlayerProfileDecoder.Decode(Record(Profile(25)))!; var b = PlayerProfileDecoder.Decode(Record(Profile(26)))!;
        Assert.Equal(a.Class, b.Class); Assert.NotEqual(a.Variant, b.Variant); Assert.Equal(a.FactionCode, b.FactionCode);
    }

    [Fact]
    public void SharedLiveProfileUpdatesIconsAndAutomaticOverridesManualOnlyForSelf()
    {
        var h = FreshProfile(); var vm = new OverlayViewModel(); vm.SetSelfClassOverride(PlayerClass.Templar);
        vm.Apply(OverlaySnapshot.FromMeter(h.Tick())); Assert.Equal(PlayerClass.Cleric, vm.Rows[0].Class); h.Frame(Invite()); h.Frame(Join()); h.Frame(Profile(5));
        var snapshot = h.Tick(); vm.Apply(OverlaySnapshot.FromMeter(snapshot));
        Assert.Equal(PlayerClass.Cleric, vm.Rows.Single(r => r.IsSelf).Class);
        Assert.Equal(PlayerClass.Gladiator, vm.Rows.Single(r => !r.IsSelf).Class);
        Assert.Equal(PlayerClassSource.Character3336, snapshot.Members!.Single(m => m.IsSelf).ClassEvidence!.Source);
        Assert.Equal(PlayerClassSource.Profile4536, snapshot.Members!.Single(m => !m.IsSelf).ClassEvidence!.Source);
    }

    [Fact]
    public void ProfilesDoNotGrantPartyAndGenericOrSummonCombatNeverAssignsClass()
    {
        var h = Fresh(); h.Frame(Profile(26)); h.Frame(Hit(999, 300)); h.Frame(Hit(999, 777)); h.Frame(Hit(100));
        var m = h.Tick(); Assert.Single(m.Members!); Assert.Equal(PlayerClass.Unknown, m.Members![0].Class);
        Assert.Equal(100m, m.GroupTotalDamage); Assert.Equal(2, m.OtherCount);
    }

    [Fact]
    public void MetadataIsBoundedAndWrongEpochOrSameNameNewRuntimeNeverInheritsClass()
    {
        var d = new PlayerProfileDirectory("test"); d.Observe(Record(Profile(29)));
        Assert.Null(d.Get(301, "Remote", false)); d.Observe(Record(Profile(5, 301), epoch: "other")); Assert.Null(d.Get(301, "Remote", false));
        for (uint i = 1000; i < 1000 + PlayerProfileDirectory.Capacity; i++) d.Observe(Record(Profile(5, i)));
        Assert.Equal(0, d.Count); Assert.Null(d.Get(300, "Remote", false)); d.Observe(Record(Profile(29))); Assert.Equal(0, d.Count);
    }

    [Fact]
    public void ProfileSurvivesCheckpointResetAndSameEpochRefreshButNeverReconnect()
    {
        var h = FreshProfile();
        for (var i = 0; i < 12; i++) { h.Frame(Hit(10)); h.Tick(); }
        h.Meter.ResetCurrent(h.Now); Assert.Equal(PlayerClass.Cleric, h.Tick().Members![0].Class);
        h.Frame(Profile(29, 200, "Local", true)); Assert.Equal(PlayerClass.Cleric, h.Tick().Members![0].Class);
        Assert.Equal(0, h.Feed.RetainedPartyRecordIdentities); h.Handshake(9000, 19000); h.Bind();
        Assert.Equal(PlayerClass.Unknown, h.Tick().Members![0].Class);
    }
}
