using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public static class ActionResearchCli
{
    private const string Usage = "research action-windows|record-groups <capture.pcap> [--definition file.json] [--json]\n" +
        "  [--local IP:port --remote IP:port] [--source ID --target ID] [--sequence CSV --sequence-window-ms 20]\n" +
        "  [--include-cues] [--before-ms 0 --after-ms 4000 --auxiliary-ms 3000] [--no-next-cap]\n" +
        "research compare-groups <definition.json> [--json]\n" +
        "Definition paths are relative to their JSON file. Compare requires two labeled groups of definition + anchorIds selections.\n" +
        "Windows are [start,end); actual arrivals, outside/background records, overlaps and unmatched observations are retained. Replay research only.";
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Run(string mode, string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        if (args.Length == 1 && args[0] is "--help" or "-h") { output.WriteLine(Usage); return 0; }
        try
        {
            if (args.Length == 0 || args[0].StartsWith('-')) throw new ArgumentException("Specify a capture or comparison definition.");
            var json = false;
            if (mode == "compare-groups")
            {
                if (args.Length > 2 || args.Length == 2 && args[1] != "--json") throw new ArgumentException("compare-groups accepts only a definition and optional --json.");
                json = args.Length == 2;
                var comparisonPath = Path.GetFullPath(args[0]);
                var definition = Read<GroupComparisonDefinition>(comparisonPath);
                if (definition.Groups is null || definition.Groups.Count != 2 || definition.Groups.Any(g => g is null)) throw new ArgumentException("Exactly two labeled comparison groups are required: positive first, negative second.");
                var cache = new Dictionary<string, ActionCorrelationResult>(StringComparer.Ordinal);
                var selected = definition.Groups.Select(g =>
                {
                    if (g.Trials is null) throw new ArgumentException("Missing trial selections.");
                    var result = new List<ResearchTrial>();
                    foreach (var selection in g.Trials)
                    {
                        if (selection is null) throw new ArgumentException("Trial selections cannot be null.");
                        var path = Resolve(comparisonPath, selection.Definition);
                        if (!cache.TryGetValue(path, out var analyzed))
                        {
                            var input = Read<ActionResearchDefinition>(path);
                            input = input with { Capture = Resolve(path, input.Capture) };
                            analyzed = Analyze(input); cache.Add(path, analyzed);
                        }
                        if (selection.AnchorIds is null || selection.AnchorIds.Count == 0) throw new ArgumentException("Select at least one explicit anchor ID per trial selection.");
                        foreach (var id in selection.AnchorIds)
                        {
                            var window = analyzed.Windows.SingleOrDefault(w => w.ActionAnchor.AnchorId == id)
                                ?? throw new ArgumentException("Unknown anchor ID: " + id);
                            result.Add(new(analyzed.CaptureId, id, window.Records.Select(r => r.CombatRecord).ToArray(),
                                window.RecordGroups.SelectMany(r => r.AuxiliaryEdges).ToArray(), window.ActionAnchor.Timestamp));
                        }
                    }
                    return result.ToArray();
                }).ToArray();
                var comparison = ResearchGroupComparison.Compare(definition.Groups[0].Label, selected[0], definition.Groups[1].Label, selected[1]);
                if (json) output.WriteLine(JsonSerializer.Serialize(comparison, JsonOptions));
                else
                {
                    output.WriteLine($"{comparison.PositiveLabel} ({comparison.PositiveTrials}) vs {comparison.NegativeLabel} ({comparison.NegativeControlTrials}); correlation does not assign gameplay semantics.");
                    output.WriteLine("Positive-only exact codes: " + string.Join(',', comparison.PositiveOnlyCodes));
                    foreach (var m in comparison.MultiplicityDifferences)
                        output.WriteLine($"code={m.RawSkillCode} positive count={m.PositiveMinimum}..{m.PositiveMaximum} negative count={m.NegativeMinimum}..{m.NegativeMaximum} consistentIncrease={m.ConsistentPositiveIncrease}");
                    output.WriteLine("Use --json for full order, distribution, timing and auxiliary footprint evidence.");
                }
                return 0;
            }
            var capturePath = Path.GetFullPath(args[0]);
            var definitionPathIndex = Array.IndexOf(args, "--definition");
            var inputDefinition = definitionPathIndex < 0 ? new ActionResearchDefinition { Capture = capturePath } :
                Read<ActionResearchDefinition>(definitionPathIndex + 1 < args.Length ? args[definitionPathIndex + 1] : throw new ArgumentException("Missing --definition value."));
            if (definitionPathIndex >= 0)
            {
                var declaredCapture = Resolve(Path.GetFullPath(args[definitionPathIndex + 1]), inputDefinition.Capture);
                if (!string.Equals(declaredCapture, capturePath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Definition capture does not match the command capture.");
            }
            inputDefinition = inputDefinition with { Capture = capturePath };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index++)
            {
                var option = args[index];
                if (!seen.Add(option)) throw new ArgumentException("Duplicate option: " + option);
                switch (option)
                {
                    case "--definition": Value(); break;
                    case "--json": json = true; break;
                    case "--local": inputDefinition = inputDefinition with { Local = Value() }; break;
                    case "--remote": inputDefinition = inputDefinition with { Remote = Value() }; break;
                    case "--source": inputDefinition = inputDefinition with { SourceEntityId = Entity() }; break;
                    case "--target": inputDefinition = inputDefinition with { TargetEntityId = Entity() }; break;
                    case "--sequence": inputDefinition = inputDefinition with { OutboundSequence = Value().Split(',').Select(s => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray() }; break;
                    case "--sequence-window-ms": inputDefinition = inputDefinition with { SequenceWindowMilliseconds = Number() }; break;
                    case "--include-cues": inputDefinition = inputDefinition with { IncludeMetadataCues = true }; break;
                    case "--before-ms": inputDefinition = inputDefinition with { Policy = inputDefinition.Policy with { BeforeMilliseconds = Number() } }; break;
                    case "--after-ms": inputDefinition = inputDefinition with { Policy = inputDefinition.Policy with { AfterMilliseconds = Number() } }; break;
                    case "--auxiliary-ms": inputDefinition = inputDefinition with { Policy = inputDefinition.Policy with { AuxiliaryProximityMilliseconds = Number() } }; break;
                    case "--no-next-cap": inputDefinition = inputDefinition with { Policy = inputDefinition.Policy with { CapAtNextAnchor = false } }; break;
                    default: throw new ArgumentException("Unknown option: " + option);
                }
                string Value() => ++index < args.Length ? args[index] : throw new ArgumentException("Missing option value: " + option);
                double Number() => double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : throw new ArgumentException("Expected finite numeric value.");
                ulong Entity() => ulong.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException("Expected unsigned entity ID.");
            }
            var correlated = Analyze(inputDefinition);
            if (json) output.WriteLine(JsonSerializer.Serialize(correlated, JsonOptions));
            else
            {
                output.WriteLine($"Capture={correlated.CaptureId}; anchors={correlated.ActionAnchors.Count}; supported={correlated.AllRecords.Count}; all outside/background records retained.");
                foreach (var w in correlated.Windows)
                {
                    output.WriteLine($"{w.WindowId} [{w.WindowStart:O},{w.WindowEnd:O}) records={w.Records.Count} background={w.BackgroundRecords.Count}");
                    foreach (var g in w.RecordGroups) output.WriteLine($"  source={g.SourceEntityId} target={g.TargetEntityId} orderedCodes=[{string.Join(',', g.OrderedRawSkillCodes)}] spanMs={g.GroupPattern.ArrivalSpanMilliseconds.ToString(CultureInfo.InvariantCulture)}");
                    foreach (var v in w.VisualComparisons) output.WriteLine($"  visual={v.ObservationId} status={v.Status} candidates=[{string.Join(',', v.CandidateRecordIds)}]");
                }
                foreach (var warning in correlated.Warnings) output.WriteLine(warning);
                output.WriteLine("Use --json for policy, classifications, raw records, auxiliary edges, warnings and external annotations.");
            }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { error.WriteLine(ex.Message); error.WriteLine(Usage); return 2; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException or OverflowException)
        { error.WriteLine($"Action research failed: {ex.Message}"); return 1; }
    }

    public static ActionCorrelationResult Analyze(ActionResearchDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Capture);
        if (definition.Policy is null || definition.Anchors is null || definition.Anchors.Any(a => a is null || a.CaptureId is null)) throw new ArgumentException("Policy and anchors cannot be null.");
        definition.Policy.Validate();
        var path = Path.GetFullPath(definition.Capture);
        var metadataPath = Path.ChangeExtension(path, ".json");
        var metadata = File.Exists(metadataPath) ? SessionMetadataStore.Deserialize(File.ReadAllText(metadataPath)) : null;
        if ((definition.Local is null) != (definition.Remote is null)) throw new ArgumentException("Specify both endpoints.");
        IPEndPoint local, remote;
        if (definition.Local is not null) { local = Endpoint(definition.Local); remote = Endpoint(definition.Remote!); }
        else
        {
            var connections = metadata?.ConnectionsAtStart.Where(c => c.Protocol == TransportProtocol.Tcp && c.RemotePort == 13328).ToArray() ?? [];
            if (connections.Length != 1) throw new ArgumentException("Metadata must identify exactly one game connection or endpoints must be explicit.");
            local = Endpoint(connections[0].LocalEndpoint); remote = Endpoint(connections[0].RemoteEndpoint ?? "");
        }
        var capture = new ResearchAnalyzer().Read(path, new(local.Address, (ushort)local.Port, remote.Address, (ushort)remote.Port), metadata?.StartedUtc);
        var streams = Enum.GetValues<TrafficDirection>().Select(d => TcpStreamReassembler.Assemble(capture.Packets, d)).ToArray();
        var decoded = new ReplayProtocolDecoder().Decode(path, streams, capture.OriginUtc);
        var anchors = definition.Anchors.Select(a =>
        {
            if (a.CaptureId.Length > 0 && !string.Equals(Path.GetFullPath(a.CaptureId), path, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Imported anchor belongs to a different capture.");
            return a with { CaptureId = path };
        }).ToList();
        if (definition.OutboundSequence is not null)
        {
            var matches = FrameSequenceSearch.Find(capture.Packets, definition.OutboundSequence, definition.SequenceWindowMilliseconds)
                .Where(m => m.Direction == TrafficDirection.ClientToServer).ToArray();
            for (var index = 0; index < matches.Length; index++)
            {
                var packets = matches[index].Packets;
                anchors.Add(new() { AnchorId = $"sequence-{index + 1}", CaptureId = path, Timestamp = packets[0].Segment.TimestampUtc,
                    AnchorSource = ActionAnchorSource.OutboundSequence, PacketIndex = packets[0].Segment.PacketIndex,
                    SequencePacketIndices = packets.Select(p => p.Segment.PacketIndex).ToArray() });
            }
        }
        if (definition.IncludeMetadataCues)
            anchors.AddRange((metadata?.TestMarkers ?? []).Where(m => m.MarkerType == "UserActionCue").Select(m =>
                new ResearchActionAnchor { AnchorId = "cue-" + m.MarkerId, CaptureId = path, Timestamp = m.TimestampUtc, AnchorSource = ActionAnchorSource.UserActionCue }));
        return new ReplayActionCorrelation().Analyze(path, decoded, anchors, definition.Policy, definition.SourceEntityId, definition.TargetEntityId)
            with { SemanticLabel = definition.SemanticLabel };
    }

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Missing research definition.");
    private static string Resolve(string definitionPath, string relative) => string.IsNullOrWhiteSpace(relative) ? throw new ArgumentException("Definition path is empty.") :
        Path.GetFullPath(relative, Path.GetDirectoryName(Path.GetFullPath(definitionPath))!);
    private static IPEndPoint Endpoint(string value) => IPEndPoint.TryParse(value, out var result) && result.Port > 0 ? result : throw new ArgumentException("Invalid concrete endpoint: " + value);
}
