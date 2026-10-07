using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public static class ProtocolResearchCli
{
    private const string Usage = "research decode|containers|identities|id-graph|skills <capture.pcap> [--local IP:port --remote IP:port] [--json] [--summary]\n" +
        "With no endpoints, requires exactly one TCP port-13328 connection in companion session metadata.\n" +
        "Replay only: exact observed names, separate context labels; no DPS, gameplay flags or ownership. JSON raw bytes use base64.\n" +
        "Safety: --max-output-bytes N --max-depth N --max-total-bytes N --max-inner-frames N";

    public static int Run(string[] args, TextWriter? output = null, TextWriter? error = null, string mode = "decode")
    {
        output ??= Console.Out; error ??= Console.Error;
        if (args.Length == 1 && args[0] is "--help" or "-h") { output.WriteLine(Usage); return 0; }
        try
        {
            if (args.Length == 0 || args[0].StartsWith('-')) throw new ArgumentException("Specify one capture path.");
            var path = Path.GetFullPath(args[0]);
            var json = false; var summaryOnly = false;
            IPEndPoint? local = null, remote = null;
            var limits = new ProtocolDecodeLimits();
            var seen = new HashSet<string>();
            for (var i = 1; i < args.Length; i++)
            {
                var option = args[i];
                if (!seen.Add(option)) throw new ArgumentException("Duplicate option: " + option);
                switch (option)
                {
                    case "--json": json = true; break;
                    case "--summary": summaryOnly = true; break;
                    case "--local": local = Endpoint(Value()); break;
                    case "--remote": remote = Endpoint(Value()); break;
                    case "--max-output-bytes": limits = limits with { MaximumDecompressedSize = Positive() }; break;
                    case "--max-depth": limits = limits with { MaximumContainerDepth = Positive() }; break;
                    case "--max-total-bytes": limits = limits with { MaximumDecompressedBytesPerOuter = Positive() }; break;
                    case "--max-inner-frames": limits = limits with { MaximumFramesPerContainer = Positive() }; break;
                    default: throw new ArgumentException("Unknown option: " + option);
                }
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                int Positive() => int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new ArgumentException("Expected positive integer.");
            }
            limits.Validate();
            if ((local is null) != (remote is null)) throw new ArgumentException("Specify both endpoints.");
            SessionMetadata? metadata = null;
            var metadataPath = Path.ChangeExtension(path, ".json");
            if (File.Exists(metadataPath)) metadata = SessionMetadataStore.Deserialize(File.ReadAllText(metadataPath));
            if (local is null)
            {
                var connections = metadata?.ConnectionsAtStart.Where(c => c.Protocol == TransportProtocol.Tcp && c.RemotePort == 13328).ToArray() ?? [];
                if (connections.Length != 1) throw new ArgumentException("Metadata does not identify exactly one game connection; specify endpoints explicitly.");
                if (connections[0].LocalPort == 0 || connections[0].RemoteIp is null || connections[0].RemotePort is null or 0)
                    throw new ArgumentException("Metadata game connection has no concrete endpoint pair.");
                local = new(IPAddress.Parse(connections[0].LocalIp), connections[0].LocalPort);
                remote = new(IPAddress.Parse(connections[0].RemoteIp!), connections[0].RemotePort!.Value);
            }
            var capture = new ResearchAnalyzer().Read(path, new(local.Address, (ushort)local.Port, remote!.Address, (ushort)remote.Port), metadata?.StartedUtc);
            var streams = Enum.GetValues<TrafficDirection>().Select(d => TcpStreamReassembler.Assemble(capture.Packets, d)).ToArray();
            var result = new ReplayProtocolDecoder(limits).Decode(path, streams, capture.OriginUtc);
            if (mode is "identities" or "id-graph" or "skills")
            {
                var identities = new ReplayIdentityAnalyzer().Analyze(path, result);
                var identityOptions = new JsonSerializerOptions { WriteIndented = true };
                identityOptions.Converters.Add(new JsonStringEnumConverter());
                object view = mode switch
                {
                    "id-graph" => new { identities.CaptureId, SessionLabel = metadata?.SessionLabel, identities.Summary,
                        identities.Graph, identities.IdentityObservations, identities.RelationshipObservations, identities.TextContextObservations, identities.DecodeIssues },
                    "skills" => new { identities.CaptureId, SessionLabel = metadata?.SessionLabel, identities.Summary,
                        identities.Skills, identities.SupportedCombatRecords, identities.CombatCorrelations },
                    _ => new { identities.CaptureId, SessionLabel = metadata?.SessionLabel, identities.Summary,
                        identities.IdentityObservations, identities.RelationshipObservations, identities.TextContextObservations,
                        identities.SupportedCombatRecords, identities.CombatCorrelations, identities.DecodeIssues }
                };
                if (summaryOnly) output.WriteLine(JsonSerializer.Serialize(new { identities.CaptureId, SessionLabel = metadata?.SessionLabel, identities.Summary }, identityOptions));
                else if (json) output.WriteLine(JsonSerializer.Serialize(view, identityOptions));
                else WriteIdentityText(output, mode, identities);
                return 0;
            }
            var supported = result.CombatCandidates.Where(c => c.Status == "Supported").ToArray();
            var summary = new
            {
                OuterFrameCount = result.Records.Count(r => r.ContainerDepth == 0 && r.PrefixLength > 0),
                SuspectedContainers = result.Containers.Count,
                ValidDecompressions = result.Containers.Count(c => c.ValidDecompression),
                FailedDecompressions = result.Containers.Count(c => !c.ValidDecompression),
                DecompressedByteTotal = result.Containers.Sum(c => (long)c.DecompressedBytes),
                InnerFramedRecordCount = result.Containers.Sum(c => (long)c.InnerFrameCount),
                FullyConsumedInnerBytes = result.Containers.Sum(c => (long)c.FullyConsumedBytes),
                TrailingUnparsedBytes = result.Containers.Sum(c => (long)c.TrailingUnparsedBytes),
                NestedContainerCount = result.Containers.Count(c => c.Depth > 0),
                MalformedInnerRecords = result.Containers.Sum(c => c.MalformedInnerRecords),
                InnerOpcodeCandidates = result.Records.Where(r => r.ContainerDepth > 0 && r.PrefixLength > 0).Select(r => r.OpcodeCandidate).Distinct().Order().ToArray(),
                OpcodeFrequencies = result.Records.Where(r => r.PrefixLength > 0).GroupBy(r => r.OpcodeCandidate).ToDictionary(g => g.Key, g => g.Count()),
                RawCombatCandidateCount = result.CombatCandidates.Count,
                InnerCombatCandidateCount = result.CombatCandidates.Count(c => c.RawRecord.ContainerDepth > 0),
                SupportedCategory6Count = supported.Length,
                UnresolvedCombatCandidates = result.CombatCandidates.Count - supported.Length,
                AggregateSum = supported.Sum(c => (decimal)c.AggregateAmount!.Value),
                DerivedBaseSum = supported.Sum(c => (decimal)c.DerivedBaseAmount!.Value),
                ComponentCount = supported.Sum(c => c.OptionalComponents.Count),
                MalformedCount = result.Records.Count(r => r.DecodeStatus is "MalformedFraming" or "MalformedInnerFraming" or "FailedContainer") + result.CombatCandidates.Count(c => c.Status == "Malformed"),
                SuppressedRecords = result.Records.Count(r => r.DecodeStatus == "Suppressed"),
                GapCount = streams.Sum(s => s.Gaps.Count), ConflictCount = streams.Sum(s => s.Conflicts.Count),
                capture.HeaderErrors, capture.UnsupportedPackets
            };
            var document = new { Capture = path, SessionLabel = metadata?.SessionLabel, capture.OriginUtc, capture.OriginSource,
                BoundaryAssumption = "Starts of contiguous TCP runs are candidate boundaries; no padding/resync or optional-marker guesses.",
                Streams = streams.Select(s => new { s.Direction, s.BaseSequence, s.DeclaredSpan, s.SynObserved,
                    s.Gaps, s.Conflicts, s.DuplicateSegments, s.OverlapBytes, AvailableBytes = s.Chunks.Sum(c => (long)c.Bytes.Length) }).ToArray(),
                Limits = limits, Summary = summary, Containers = result.Containers,
                Records = summaryOnly ? [] : result.Records, CombatCandidates = summaryOnly ? [] : result.CombatCandidates };
            var options = new JsonSerializerOptions { WriteIndented = true }; options.Converters.Add(new JsonStringEnumConverter());
            output.WriteLine(JsonSerializer.Serialize(json ? (object)document : summary, options));
            return 0;
        }
        catch (ArgumentException ex) { error.WriteLine(ex.Message); error.WriteLine(Usage); return 2; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException or OverflowException)
        { error.WriteLine($"Protocol research failed: {ex.GetType().Name}: {ex.Message}"); return 1; }
    }

    private static IPEndPoint Endpoint(string value) => IPEndPoint.TryParse(value, out var endpoint) && endpoint.Port is > 0 and <= 65535
        ? endpoint : throw new ArgumentException("Invalid IP:port endpoint.");

    private static void WriteIdentityText(TextWriter output, string mode, IdentityResearchResult result)
    {
        output.WriteLine($"Capture scope: {result.CaptureId}. Replay research only; names may be unknown or conflicting.");
        output.WriteLine(JsonSerializer.Serialize(result.Summary, new JsonSerializerOptions { WriteIndented = true }));
        if (mode == "skills")
        {
            foreach (var skill in result.Skills)
            {
                output.WriteLine($"RawSkillCode={skill.RawSkillCode} count={skill.OccurrenceCount} sources=[{string.Join(',', skill.SourceEntityIds)}] targets=[{string.Join(',', skill.TargetEntityIds)}] aggregate={skill.AggregateMinimum}..{skill.AggregateMaximum} componentTailRows={skill.ComponentTailRows}");
                output.WriteLine($"  rawFlagCombinations={JsonSerializer.Serialize(skill.RawFlagCombinations)} sourceRetrospectiveNames={JsonSerializer.Serialize(skill.SourceNames)} targetRetrospectiveNames={JsonSerializer.Serialize(skill.TargetNames)}");
            }
        }
        else
        {
            foreach (var o in result.IdentityObservations)
                output.WriteLine($"record={o.ObservationId} entity={o.EntityId} name={Quoted(o.Name)} evidence={o.EvidenceType} UTC={o.Timestamp:O}");
            foreach (var o in result.RelationshipObservations)
                output.WriteLine($"{o.ObservationId} header={o.HeaderEntityId} relatedCandidate={o.RelatedEntityIdCandidate} self={o.IsSelfId} kind={o.EvidenceType} context={Quoted(o.ContextLabel)}");
            foreach (var o in result.TextContextObservations)
                output.WriteLine($"context-record={o.ObservationId} entity={o.EntityId} ContextLabel={Quoted(o.ContextLabel)} (not a name)");
            if (mode == "id-graph")
            {
                output.WriteLine($"nodes=[{string.Join(',', result.Graph.EntityNodes)}]");
                foreach (var d in result.Graph.Duplicates) output.WriteLine($"duplicate entity={d.EntityId} name={Quoted(d.Name)} records=[{string.Join(',', d.ObservationIds)}]");
                foreach (var c in result.Graph.Conflicts) output.WriteLine($"conflict entity={c.EntityId} names={JsonSerializer.Serialize(c.Resolution.Names)}");
            }
        }
        foreach (var c in result.CombatCorrelations)
            output.WriteLine($"combat={c.CombatRecordId} code={c.RawSkillCode} source={c.SourceEntityId} preceding={Resolution(c.SourcePrecedingName)} retrospective={Resolution(c.SourceRetrospectiveName)} target={c.TargetEntityId} preceding={Resolution(c.TargetPrecedingName)} retrospective={Resolution(c.TargetRetrospectiveName)}");
        output.WriteLine("Use --json for full raw bytes, field values, observation links and provenance.");
        static string Quoted(string? value) => value is null ? "unknown" : JsonSerializer.Serialize(value);
        static string Resolution(NameResolution r) => $"{r.State}:{JsonSerializer.Serialize(r.Names)}";
    }
}
