using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class CurrentPlayerBindingTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2025-03-04T00:00:00Z");
    private static readonly TcpConnectionSelection Connection = new(IPAddress.Parse("192.0.2.5"), 24001, IPAddress.Parse("198.51.100.7"), 13328);
    private readonly ReplayCurrentPlayerBindingResolver resolver = new();

    [Theory]
    [InlineData(0u, "Z", 0, 0)]
    [InlineData(17u, "Alice", 3, 8)]
    [InlineData(517u, "Different", 30, 1)]
    [InlineData(65537u, "名字é", 10, 13)]
    [InlineData(16777217u, "A punctuation-name", 91, 42)]
    [InlineData(uint.MaxValue, "Longer name", 25, 31)]
    public void FreshPairResolvesIndependentPositionsWidthsAndUtf8(uint id, string name, int aPadding, int bPadding)
    {
        var capture = Capture(Init1536(id, name, aPadding), Init3336(id, name, bPadding), split: true);
        var binding = resolver.Analyze(capture, Connection, "session-test");
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, binding.Status);
        Assert.Equal((ulong)id, binding.EntityId); Assert.Equal(name, binding.CharacterName);
        Assert.Equal(Start.AddSeconds(4), binding.CandidateObservedFrom);
        Assert.Equal(Start.AddSeconds(10), binding.ValidFrom); Assert.Null(binding.ValidUntil);
        Assert.Equal(Start.AddSeconds(10), binding.EvidenceCoverageEnd);
        Assert.Equal("session-test", binding.Scope!.SessionId); Assert.Equal(100u, binding.Scope.ClientIsn);
        Assert.Equal(900u, binding.Scope.ServerIsn); Assert.Equal(capture.Path, binding.Scope.SourceCapture);
        var a = Assert.Single(binding.QualifyingEvidence, e => e.RecordTag == "1536");
        var b = Assert.Single(binding.QualifyingEvidence, e => e.RecordTag == "3336");
        Assert.Equal("u32LE", a.NumericRepresentation); Assert.Equal(4, a.NumericBytes.Count);
        Assert.Equal(Encoding.UTF8.GetBytes(name), b.NameBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(name), b.NameRange.Length);
        Assert.Equal(Start.AddSeconds(8), b.Timestamp); Assert.Equal(Start.AddSeconds(10), b.CompletionTimestamp);
        Assert.NotEqual(b.PacketIndex, b.CompletionPacketIndex);
        Assert.Equal(capture.Path, b.SourceCapture); Assert.NotEmpty(b.RawRecordSha256);
        Assert.Equal(Encoding.UTF8.GetBytes(name), Convert.FromBase64String(b.RawRecordBase64).AsSpan(b.NameRange.Offset, b.NameRange.Length).ToArray());
    }

    [Fact]
    public void CompressedConfirmationUsesFullContainerCompletionAndKeepsPath()
    {
        var compressed = ReplayProtocolDecoderTests.Pack(Init3336(71, "Nested"));
        var binding = resolver.Analyze(Capture(Init1536(71, "Nested"), compressed, split: true), Connection);
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, binding.Status);
        var b = Assert.Single(binding.QualifyingEvidence, e => e.RecordTag == "3336");
        Assert.Single(b.ContainerPath); Assert.Equal(Start.AddSeconds(10), binding.ValidFrom);
    }

    [Fact]
    public void SameNumericAndNameInDifferentEpochsHaveSeparateScopeAndNoStateLeak()
    {
        var a = Capture(Init1536(88, "Same"), Init3336(88, "Same"));
        var b = Capture(Init1536(88, "Same"), Init3336(88, "Same"), clientIsn: 600, serverIsn: 1100);
        Assert.NotEqual(resolver.Analyze(a, Connection).Scope, resolver.Analyze(b, Connection).Scope);
        Assert.Equal(CurrentPlayerBindingStatus.Unknown, resolver.Analyze(Capture([], Init3336(88, "Same")), Connection).Status);
    }

    [Fact]
    public void DifferentFreshSynTimestampSeparatesEvenRepeatedIsnsAndEndpoints()
    {
        var a = Capture(Init1536(2, "Same"), Init3336(2, "Same"));
        var b = a with { Packets = a.Packets.Select(p => p with { Segment = p.Segment with { TimestampUtc = p.Segment.TimestampUtc.AddHours(1) } }).ToArray() };
        Assert.NotEqual(resolver.Analyze(a, Connection).Scope, resolver.Analyze(b, Connection).Scope);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void MissingEitherRequiredTagIsUnknown(bool missingFirst)
    {
        var c = Capture(missingFirst ? [] : Init1536(5, "Name"), missingFirst ? Init3336(5, "Name") : []);
        Unknown(resolver.Analyze(c, Connection));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void EveryMissingHandshakeStepFailsClosed(int step)
    {
        var c = Capture(Init1536(5, "Name"), Init3336(5, "Name"));
        Unknown(resolver.Analyze(c with { Packets = c.Packets.Where((p, i) => step == 2
            ? p.Direction != TrafficDirection.ClientToServer || p.Segment.Flags == TcpFlags.Syn : i != step).ToArray() }, Connection));
    }

    [Fact]
    public void MidstreamCaptureCannotBeRescuedByACompleteNamePair()
    {
        var c = Capture(Init1536(8, "Name"), Init3336(8, "Name"));
        Unknown(resolver.Analyze(c with { Packets = c.Packets.Skip(3).ToArray() }, Connection));
    }

    [Theory]
    [InlineData("1536")] [InlineData("3336")]
    public void TruncatedRequiredFrameIsUnknown(string tag)
    {
        var a = Init1536(8, "Name"); var b = Init3336(8, "Name");
        Unknown(resolver.Analyze(Capture(tag == "1536" ? a[..^1] : a, tag == "3336" ? b[..^1] : b), Connection));
    }

    [Theory]
    [InlineData("C3")] [InlineData("C328")] [InlineData("EDA080")] [InlineData("F4908080")]
    public void InvalidUtf8CannotBePromoted(string hex)
    {
        var malformed = Frame([0x15, 0x36, 8, 0, 0, 0, (byte)(hex.Length / 2), .. Convert.FromHexString(hex)]);
        Unknown(resolver.Analyze(Capture(malformed, Init3336(8, "Name")), Connection));
    }

    [Fact]
    public void CanonicalVarintRequiredAndUnknownBytesAreNotInterpreted()
    {
        var noncanonical = Frame([0x33, 0x36, 0x88, 0, 4, .. Encoding.UTF8.GetBytes("Name")]);
        Unknown(resolver.Analyze(Capture(Init1536(8, "Name"), noncanonical), Connection));
    }

    [Fact]
    public void CompleteFrameWithTruncatedLengthPrefixedNameIsUnknown()
    {
        var malformed = Frame([0x15, 0x36, 8, 0, 0, 0, 100, (byte)'X']);
        Unknown(resolver.Analyze(Capture(malformed, Init3336(8, "Name")), Connection));
    }

    [Fact]
    public void OutboundInitializationCannotCreateSelfBinding()
    {
        var c = Capture([], Init3336(8, "Name")); var p = c.Packets[3]; var payload = Init1536(8, "Name");
        c = c with { Packets = c.Packets.Select(x => x == p ? x with { Segment = x.Segment with { Payload = payload, DeclaredPayloadLength = payload.Length } } : x).ToArray() };
        Unknown(resolver.Analyze(c, Connection));
    }

    [Fact]
    public void TenByteCanonicalVarintIsRetainedWithoutAssumingTwoByteId()
    {
        var b = Init3336(ulong.MaxValue, "Wide"); var c = Capture(Init1536(1, "Wide"), b);
        var record = Decode(c).Single(r => r.OpcodeCandidate == "3336");
        var e = Assert.Single(LocalInitializationExtractor.Extract(record));
        Assert.Equal(ulong.MaxValue, e.EntityId); Assert.Equal(10, e.NumericRange.Length);
    }

    [Fact]
    public void UnsupportedTagsCannotSubstituteForInitialization()
    {
        Unknown(resolver.Analyze(Capture(Frame([0x45, 0x36, 1, 4, .. Encoding.UTF8.GetBytes("Name")]), Frame([0x41, 0x36, 1])), Connection));
    }

    [Fact]
    public void NoSelectedFlowIsUnknown()
    {
        Unknown(resolver.Analyze(new("empty", Start, "synthetic", [], 0, 0), Connection));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void GapOrConflictingRetransmissionIsUnknown(bool gap)
    {
        var c = Capture(Init1536(8, "Name"), Init3336(8, "Name"));
        if (gap)
        {
            var last = c.Packets[^1]; c = c with { Packets = [.. c.Packets.Take(c.Packets.Count - 1), last with { Segment = last.Segment with { SequenceNumber = last.Segment.SequenceNumber + 1 } }] };
        }
        else
        {
            var first = c.Packets[4]; var bytes = first.Segment.Payload.ToArray(); bytes[^1] ^= 1;
            c = c with { Packets = [.. c.Packets, first with { Segment = first.Segment with { PacketIndex = 99, TimestampUtc = Start.AddSeconds(12), Payload = bytes } }] };
        }
        Unknown(resolver.Analyze(c, Connection));
    }

    [Fact]
    public void IdenticalTcpRetransmissionCreatesOneApplicationEvidencePair()
    {
        var c = Capture(Init1536(8, "Name"), Init3336(8, "Name")); var first = c.Packets[4];
        c = c with { Packets = [.. c.Packets, first with { Segment = first.Segment with { PacketIndex = 99, TimestampUtc = Start.AddSeconds(12) } }] };
        var stream = TcpStreamReassembler.Assemble(c.Packets, TrafficDirection.ServerToClient);
        Assert.Equal(1, stream.DuplicateSegments);
        var binding = resolver.Analyze(c, Connection);
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, binding.Status); Assert.Equal(2, binding.QualifyingEvidence.Count);
    }

    [Theory]
    [InlineData("completion")] [InlineData("source")] [InlineData("packet")] [InlineData("container")]
    public void MissingOrForeignProvenanceCannotResolve(string field)
    {
        var c = Capture(Init1536(8, "Name"), Init3336(8, "Name")); var rs = Decode(c);
        var b = rs.Single(r => r.OpcodeCandidate == "3336");
        var bad = field switch { "completion" => b with { CompletionUtc = default }, "source" => b with { SourceCapture = "other" },
            "packet" => b with { CompletionPacketIndex = 999 }, _ => b with { ContainerPath = [new(999, 0)] } };
        Unknown(resolver.Resolve(c, Connection, rs.Select(r => r == b ? bad : r).ToArray()));
    }

    [Fact]
    public void SameProvenanceIsDeduplicatedButIndependentSameValuesAreNot()
    {
        var c = Capture(Init1536(8, "Name"), Init3336(8, "Name")); var rs = Decode(c);
        var binding = resolver.Resolve(c, Connection, [.. rs, .. rs]);
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, binding.Status); Assert.Equal(2, binding.QualifyingEvidence.Count);
        var repeated = Capture([.. Init1536(8, "Name"), .. Init1536(8, "Name")], Init3336(8, "Name"));
        Unknown(resolver.Analyze(repeated, Connection));
    }

    [Theory]
    [InlineData(12u, "Alice", 13u, "Alice")]
    [InlineData(12u, "Alice", 12u, "Bob")]
    public void UniqueCandidatesWithContradictoryIdentityAreConflict(uint a, string an, uint b, string bn)
    {
        var binding = resolver.Analyze(Capture(Init1536(a, an), Init3336(b, bn)), Connection);
        Assert.Equal(CurrentPlayerBindingStatus.Conflict, binding.Status); Assert.Null(binding.EntityId); Assert.Null(binding.CharacterName);
        Assert.Equal(2, binding.Evidence.Count);
    }

    [Fact]
    public void MultipleIncompatibleQualifyingPairsConflictWithoutVoting()
    {
        var bytes = new[] { Init1536(9, "Alice"), Init3336(9, "Alice"), Init1536(10, "Bob"), Init3336(10, "Bob") }.SelectMany(b => b).ToArray();
        Assert.Equal(CurrentPlayerBindingStatus.Conflict, resolver.Analyze(Capture(bytes, []), Connection).Status);
    }

    [Fact]
    public void SameScopeSameProvenanceWithIncompatibleSupportedBytesConflicts()
    {
        var c = Capture(Init1536(9, "Alice"), Init3336(9, "Alice")); var rs = Decode(c); var a = rs.Single(r => r.OpcodeCandidate == "1536");
        var bad = a with { RawBytes = Init1536(10, "Alice") };
        Assert.Equal(CurrentPlayerBindingStatus.Conflict, resolver.Resolve(c, Connection, [.. rs, bad]).Status);
    }

    [Theory]
    [InlineData(0)] [InlineData(9)]
    public void MultiplePossibleRawNameFieldsAreUnknownEvenWithSharedValue(byte id)
    {
        var a = Frame([0x15, 0x36, id, 0, 0, 0, 1, (byte)'A', id, 0, 0, 0, 1, (byte)'B']);
        var b = Frame([0x33, 0x36, id, 1, (byte)'A', 1, (byte)'B']);
        Unknown(resolver.Analyze(Capture(a, b), Connection));
    }

    [Fact]
    public void ConfirmationBeforeCandidateIsUnknown()
    {
        Unknown(resolver.Analyze(Capture(Init3336(9, "Alice"), Init1536(9, "Alice")), Connection));
    }

    [Fact]
    public void NoNormalizationAndNoReplacementCharacterName()
    {
        var c = Capture(Init1536(9, "é"), Init3336(9, "e\u0301"));
        Assert.Equal(CurrentPlayerBindingStatus.Conflict, resolver.Analyze(c, Connection).Status);
        Unknown(resolver.Analyze(Capture(Init1536(9, "\uFFFD"), Init3336(9, "\uFFFD")), Connection));
    }

    [Fact]
    public void ResultAndEvidenceOwnTheirImmutableCollections()
    {
        var c = Capture(Init1536(9, "Alice"), Init3336(9, "Alice")); var rs = Decode(c);
        var binding = resolver.Resolve(c, Connection, rs); var e = binding.QualifyingEvidence[0]; var original = e.NameBytes.ToArray();
        foreach (var r in rs) Array.Fill(r.RawBytes, (byte)0);
        Assert.Equal(original, e.NameBytes);
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)e.NameBytes)[0] = 0);
        Assert.Throws<NotSupportedException>(() => ((IList<CurrentPlayerBindingEvidence>)binding.Evidence).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)binding.Diagnostics).Clear());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CaptureEndAndOneSidedFinDoNotInventActorLifetime(bool fin)
    {
        var c = Capture(Init1536(9, "Alice"), Init3336(9, "Alice"));
        if (fin) c = Append(c, TrafficDirection.ServerToClient, TcpFlags.Fin | TcpFlags.Ack, 950, 102);
        var binding = resolver.Analyze(c, Connection);
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, binding.Status); Assert.Null(binding.ValidUntil);
    }

    [Fact]
    public void ResetBoundsConnectionValidityAndDoesNotBecomeDespawn()
    {
        var c = Append(Capture(Init1536(9, "Alice"), Init3336(9, "Alice")), TrafficDirection.ServerToClient, TcpFlags.Rst, 999, 0);
        var b = resolver.Analyze(c, Connection); Assert.Equal(CurrentPlayerBindingStatus.Resolved, b.Status);
        Assert.Equal(Start.AddSeconds(20), b.ValidUntil); Assert.Equal(Start.AddSeconds(20), b.EvidenceCoverageEnd);
    }

    [Fact]
    public void DataBearingAckCanCompleteHandshake()
    {
        var c = Capture(Init1536(9, "Alice"), Init3336(9, "Alice"));
        var result = resolver.Analyze(c with { Packets = c.Packets.Where((_, i) => i != 2).ToArray() }, Connection);
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, result.Status);
    }

    [Fact]
    public void BothFinsWithTheirAcknowledgmentsBoundConnectionOnly()
    {
        var c = Capture(Init1536(9, "Alice"), Init3336(9, "Alice"));
        var serverSequence = c.Packets[^1].Segment.SequenceNumber + (uint)c.Packets[^1].Segment.Payload.Length;
        var clientSequence = c.Packets[3].Segment.SequenceNumber + (uint)c.Packets[3].Segment.Payload.Length;
        var additions = new List<ResearchPacket>();
        void Add(TrafficDirection d, TcpFlags flags, uint seq, uint ack, int seconds)
        {
            var p = c.Packets.First(p => p.Direction == d);
            additions.Add(p with { Segment = p.Segment with { PacketIndex = 100 + additions.Count, TimestampUtc = Start.AddSeconds(seconds),
                SequenceNumber = seq, AcknowledgmentNumber = ack, Flags = flags, DeclaredPayloadLength = 0, Payload = [] } });
        }
        Add(TrafficDirection.ServerToClient, TcpFlags.Fin | TcpFlags.Ack, serverSequence, clientSequence, 20);
        Add(TrafficDirection.ClientToServer, TcpFlags.Fin | TcpFlags.Ack, clientSequence, serverSequence + 1, 21);
        Add(TrafficDirection.ServerToClient, TcpFlags.Ack, serverSequence + 1, clientSequence + 1, 22);
        var binding = resolver.Analyze(c with { Packets = [.. c.Packets, .. additions] }, Connection);
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, binding.Status);
        Assert.Equal(Start.AddSeconds(22), binding.ValidUntil);
    }

    [Fact]
    public void UnrelatedRemotePartyAndContextRecordsAreNotBindingPrerequisites()
    {
        var noise = new[] { "4536", "4136", "338A", "0092", "0792", "0892", "0992", "0D92", "0E92" }
            .SelectMany(tag => Frame([.. Convert.FromHexString(tag), 0])).ToArray();
        var b = resolver.Analyze(Capture([.. Init1536(9, "Alice"), .. noise], Init3336(9, "Alice")), Connection);
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, b.Status);
        Assert.All(b.Evidence, e => Assert.Contains(e.RecordTag, new[] { "1536", "3336" }));
    }

    [Fact]
    public void JsonCliNoConnectionReturnsUnknownWithoutAllTrafficNameScanning()
    {
        using var files = new TestFiles(); var path = files.WritePcap([TestFiles.Packet(6, Start)]);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, ResearchCli.Run(["self-binding", path, "--json"], output, error));
        using var j = JsonDocument.Parse(output.ToString());
        Assert.Equal("Unknown", j.RootElement.GetProperty("Status").GetString()); Assert.Equal(JsonValueKind.Null, j.RootElement.GetProperty("EntityId").ValueKind);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void JsonAndTextCliExposeCompleteReplayBinding()
    {
        var c = Capture(Init1536(9, "Alice"), Init3336(9, "Alice"), split: true);
        using var files = new TestFiles(); var path = files.WritePcap(c.Packets.Select(Wire).ToArray());
        foreach (var json in new[] { true, false })
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            var args = new List<string> { "self-binding", path, "--local", "192.0.2.5:24001", "--remote", "198.51.100.7:13328" };
            if (json) args.Add("--json");
            Assert.Equal(0, ResearchCli.Run(args.ToArray(), output, error)); Assert.Empty(error.ToString());
            if (json) { using var j = JsonDocument.Parse(output.ToString()); Assert.Equal("Resolved", j.RootElement.GetProperty("Status").GetString()); Assert.Equal(2, j.RootElement.GetProperty("QualifyingEvidence").GetArrayLength()); }
            else { Assert.Contains("Status=Resolved", output.ToString()); Assert.Contains("complete=", output.ToString()); }
        }
    }

    private static void Unknown(CurrentPlayerBinding b)
    {
        Assert.Equal(CurrentPlayerBindingStatus.Unknown, b.Status); Assert.Null(b.EntityId); Assert.Null(b.CharacterName); Assert.Null(b.ValidFrom); Assert.NotEmpty(b.Diagnostics);
    }
    private static byte[] Frame(byte[] b) => ReplayProtocolDecoderTests.Frame(b);
    private static byte[] Init1536(uint id, string name, int padding = 0)
    {
        var numeric = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(numeric, id); var text = Encoding.UTF8.GetBytes(name);
        return Frame([0x15, 0x36, .. Enumerable.Repeat((byte)0xff, padding), .. numeric, (byte)text.Length, .. text]);
    }
    private static byte[] Init3336(ulong id, string name, int padding = 0)
    {
        var text = Encoding.UTF8.GetBytes(name);
        return Frame([0x33, 0x36, .. ReplayProtocolDecoderTests.Varint(id), .. Enumerable.Repeat((byte)0xff, padding), (byte)text.Length, .. text]);
    }
    private static ResearchCapture Capture(byte[] first, byte[] second, bool split = false, uint clientIsn = 100, uint serverIsn = 900)
    {
        var packets = new List<ResearchPacket>();
        void Add(TrafficDirection d, uint sequence, uint ack, TcpFlags flags, byte[] payload, int seconds)
        {
            var inbound = d == TrafficDirection.ServerToClient;
            var s = new TcpSegment(packets.Count + 1, Start.AddSeconds(seconds), inbound ? Connection.RemoteIp : Connection.LocalIp,
                inbound ? Connection.RemotePort : Connection.LocalPort, inbound ? Connection.LocalIp : Connection.RemoteIp,
                inbound ? Connection.LocalPort : Connection.RemotePort, sequence, ack, flags, 54 + payload.Length, payload.Length, payload, false);
            packets.Add(new(s, d, seconds));
        }
        Add(TrafficDirection.ClientToServer, clientIsn, 0, TcpFlags.Syn, [], 0);
        Add(TrafficDirection.ServerToClient, serverIsn, clientIsn + 1, TcpFlags.Syn | TcpFlags.Ack, [], 1);
        Add(TrafficDirection.ClientToServer, clientIsn + 1, serverIsn + 1, TcpFlags.Ack, [], 2);
        Add(TrafficDirection.ClientToServer, clientIsn + 1, serverIsn + 1, TcpFlags.Ack, Frame([0x99, 0x77]), 3);
        Add(TrafficDirection.ServerToClient, serverIsn + 1, clientIsn + 1, TcpFlags.Psh | TcpFlags.Ack, first, 4);
        if (split && second.Length > 4)
        {
            Add(TrafficDirection.ServerToClient, serverIsn + 1 + (uint)first.Length, clientIsn + 1, TcpFlags.Psh | TcpFlags.Ack, second[..4], 8);
            Add(TrafficDirection.ServerToClient, serverIsn + 5 + (uint)first.Length, clientIsn + 1, TcpFlags.Psh | TcpFlags.Ack, second[4..], 10);
        }
        else Add(TrafficDirection.ServerToClient, serverIsn + 1 + (uint)first.Length, clientIsn + 1, TcpFlags.Psh | TcpFlags.Ack, second, 8);
        return new("synthetic-entry", Start, "synthetic", packets.ToArray(), 0, 0);
    }
    private static IReadOnlyList<RawProtocolRecord> Decode(ResearchCapture c) => new ReplayProtocolDecoder().Decode(c.Path,
        Enum.GetValues<TrafficDirection>().Select(d => TcpStreamReassembler.Assemble(c.Packets, d)).ToArray(), c.OriginUtc).Records;
    private static ResearchCapture Append(ResearchCapture c, TrafficDirection d, TcpFlags flags, uint sequence, uint ack)
    {
        var template = c.Packets.First(p => p.Direction == d); return c with { Packets = [.. c.Packets, template with { Segment = template.Segment with {
            PacketIndex = 99, TimestampUtc = Start.AddSeconds(20), Flags = flags, SequenceNumber = sequence, AcknowledgmentNumber = ack, Payload = [], DeclaredPayloadLength = 0 } }] };
    }
    private static CapturedPacket Wire(ResearchPacket p)
    {
        var s = p.Segment; var data = new byte[54 + s.Payload.Length]; TestFiles.Packet(6, s.TimestampUtc).Data.CopyTo(data, 0);
        s.SourceIp.GetAddressBytes().CopyTo(data, 26); s.DestinationIp.GetAddressBytes().CopyTo(data, 30);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)(40 + s.Payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(34), s.SourcePort); BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(36), s.DestinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(38), s.SequenceNumber); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(42), s.AcknowledgmentNumber);
        data[47] = (byte)s.Flags; s.Payload.CopyTo(data, 54); return new(s.TimestampUtc, data.Length, 1, data);
    }
}
