using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class LiveDiagnosticJsonTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-10-08T00:00:00Z");

    [Fact]
    public void OriginalIpv4ObjectSerializationInvokesUnsupportedScopeId()
    {
        var address = IPAddress.Parse("192.0.2.10");
        var getter = Assert.Throws<SocketException>(() => address.ScopeId);
        Assert.Equal(SocketError.OperationNotSupported, getter.SocketErrorCode);
        if (OperatingSystem.IsWindows()) Assert.Equal(10045, getter.NativeErrorCode);
        var reflected = Assert.Throws<SocketException>(() => JsonSerializer.Serialize(new { Epochs = new[] { Epoch(address, IPAddress.Parse("198.51.100.20")) } }));
        Assert.Equal(getter.SocketErrorCode, reflected.SocketErrorCode);
        Assert.Contains("ScopeId", reflected.StackTrace);
    }

    [Theory]
    [InlineData("192.0.2.10", "198.51.100.20")]
    [InlineData("203.0.113.30", "203.0.113.40")]
    [InlineData("2001:db8::10", "2001:db8::20")]
    [InlineData("fe80::10%23", "fe80::20%42")]
    public void PopulatedJsonPreservesEndpointsAndDiagnosticsWithoutNetworkingReflection(string localText, string remoteText)
    {
        var local = IPAddress.Parse(localText); var remote = IPAddress.Parse(remoteText);
        var pipeline = new LivePacketPipeline([local]); var epoch = Epoch(local, remote);
        // Exercise the exact serializer used by the CLI, without Npcap or a real interface.
        var json = LiveSmokeCli.SnapshotJson(pipeline, [epoch], Timestamp);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement; Assert.Equal("snapshot", root.GetProperty("Kind").GetString());
        Assert.Equal(Timestamp, root.GetProperty("TimestampUtc").GetDateTimeOffset());
        var value = Assert.Single(root.GetProperty("Epochs").EnumerateArray());
        var connection = value.GetProperty("Connection");
        Assert.Equal(local.ToString(), connection.GetProperty("LocalIp").GetString());
        Assert.Equal(remote.ToString(), connection.GetProperty("RemoteIp").GetString());
        Assert.Equal(24001, connection.GetProperty("LocalPort").GetInt32());
        Assert.Equal(13328, connection.GetProperty("RemotePort").GetInt32());
        Assert.Equal(new IPEndPoint(local, 24001).ToString(), connection.GetProperty("LocalEndpoint").GetString());
        Assert.Equal(new IPEndPoint(remote, 13328).ToString(), connection.GetProperty("RemoteEndpoint").GetString());
        if (local.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Assert.Equal(local.ScopeId, connection.GetProperty("LocalScopeId").GetInt64());
            Assert.Equal(remote.ScopeId, connection.GetProperty("RemoteScopeId").GetInt64());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, connection.GetProperty("LocalScopeId").ValueKind);
            Assert.Equal(JsonValueKind.Null, connection.GetProperty("RemoteScopeId").ValueKind);
        }
        Assert.Equal("Resolved", value.GetProperty("BindingStatus").GetString());
        Assert.Equal("Local", value.GetProperty("CharacterName").GetString());
        Assert.Equal(100u, value.GetProperty("ClientIsn").GetUInt32()); Assert.Equal(900u, value.GetProperty("ServerIsn").GetUInt32());
        Assert.Equal(3, value.GetProperty("AcceptedEvents").GetInt32()); Assert.Equal(1, value.GetProperty("SelfCount").GetInt32());
        Assert.Equal(1, value.GetProperty("OtherCount").GetInt32()); Assert.Equal(1, value.GetProperty("UnknownCount").GetInt32());
        Assert.Equal(400ul, value.GetProperty("RecentSelfAmounts")[0].GetUInt64());
        Assert.Equal(2, value.GetProperty("UnsupportedCandidates").GetInt32());
        Assert.Equal("diagnostic", value.GetProperty("Warnings")[0].GetString());
    }

    [Fact]
    public void EmptyEpochSnapshotSerializesAsEmptyArray()
    {
        var pipeline = new LivePacketPipeline([IPAddress.Parse("192.0.2.10")]);
        using var document = JsonDocument.Parse(LiveSmokeCli.SnapshotJson(pipeline, [], Timestamp));
        Assert.Equal(0, document.RootElement.GetProperty("Epochs").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("MalformedPackets").GetInt64());
        Assert.Equal(0, document.RootElement.GetProperty("UnsupportedPackets").GetInt64());
        Assert.Equal(0, document.RootElement.GetProperty("IgnoredPackets").GetInt64());
        Assert.Equal(0, document.RootElement.GetProperty("RejectedFlows").GetInt64());
    }

    [Fact]
    public void StartJsonRetainsTheExistingStringOnlyContract()
    {
        using var document = JsonDocument.Parse(LiveDiagnosticJson.SerializeStart("npcap:synthetic", "synthetic-interface", 13328));
        var root = document.RootElement;
        Assert.Equal("start", root.GetProperty("Kind").GetString());
        Assert.Equal("npcap:synthetic", root.GetProperty("SourceId").GetString());
        Assert.Equal("synthetic-interface", root.GetProperty("Interface").GetString());
        Assert.Equal(13328, root.GetProperty("Port").GetInt32());
    }

    [Fact]
    public void AllLiveJsonContractsRecursivelyContainOnlyExplicitSafeTypes()
    {
        var visited = new HashSet<Type>();
        Visit(typeof(LiveDiagnosticStart)); Visit(typeof(LiveDiagnosticSnapshot));
        void Visit(Type type)
        {
            if (!visited.Add(type)) return;
            if (Nullable.GetUnderlyingType(type) is { } inner) { Visit(inner); return; }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            { Visit(type.GetGenericArguments()[0]); return; }
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(DateTimeOffset)) return;
            Assert.False(type.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true,
                "Framework networking object reached the JSON wire contract: " + type);
            Assert.StartsWith("Aion2Meter.", type.Namespace);
            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                Visit(property.PropertyType);
        }
    }

    private static LiveEpochSnapshot Epoch(IPAddress local, IPAddress remote) => new("synthetic/epoch-1",
        new(local, 24001, remote, 13328), 100, 900, "Active", true, CurrentPlayerBindingStatus.Resolved,
        200, "Local", 3, 1, 1, 1, [400], 2, 0, 0, 4, 10, ["diagnostic"]);
}
