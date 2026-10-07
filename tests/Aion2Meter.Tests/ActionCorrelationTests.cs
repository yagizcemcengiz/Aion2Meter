using System.Buffers.Binary;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class ActionCorrelationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public void OneAnchorCanOwnZeroOneOrManyUnmergedRecords(int count)
    {
        var raw = Enumerable.Range(1, count).Select(i => Combat(i, i * 200, (uint)(100 + i), amount: (ulong)i * 10)).ToArray();
        var result = Analyze(raw);
        var window = Assert.Single(result.Windows);
        Assert.Equal(count, window.Records.Count); Assert.Equal(count, result.AllRecords.Count);
        if (count == 0) Assert.Empty(window.RecordGroups);
        else
        {
            var group = Assert.Single(window.RecordGroups);
            Assert.Equal(raw.Select(r => r.RecordId), group.OrderedRecords.Select(r => r.RawRecord.RecordId));
            Assert.Equal(Enumerable.Range(1, count).Select(i => (ulong)i * 10), group.AggregateAmounts);
            Assert.Equal(count, group.GroupPattern.RecordCount);
        }
    }

    [Fact]
    public void DefaultFourSecondsKeepsLateCompanionAndRetainsOutsideRecords()
    {
        var result = Analyze([Combat(1, 3068), Combat(2, 3999), Combat(3, 4000)]);
        Assert.Equal(4000, result.Policy.AfterMilliseconds);
        Assert.Equal(new[] { 1, 2 }, result.Windows[0].Records.Select(r => r.CombatRecord.RawRecord.RecordId));
        Assert.Equal(3, result.AllRecords.Count);
        Assert.Equal(RecordContextClassification.ControlledTupleOutsideWindow, result.AllRecords[2].Classification);
    }

    [Fact]
    public void PresentationWindowCanBeShorterWithoutDeletingTheLateRecord()
    {
        var result = Analyze([Combat(1, 3068)], policy: new() { AfterMilliseconds = 3000 });
        Assert.Empty(result.Windows[0].Records);
        Assert.Equal(3068, (Assert.Single(result.AllRecords).CombatRecord.RawRecord.TimestampUtc - Start).TotalMilliseconds);
    }

    [Fact]
    public void NextAnchorCapsOwnershipAndBoundaryRecordBelongsToNextWindow()
    {
        var result = Analyze([Combat(1, 1999), Combat(2, 2000)], [Anchor("a"), Anchor("b", 2000)]);
        Assert.Equal(Start.AddSeconds(2), result.Windows[0].WindowEnd);
        Assert.Equal(1, Assert.Single(result.Windows[0].Records).CombatRecord.RawRecord.RecordId);
        Assert.Equal(2, Assert.Single(result.Windows[1].Records).CombatRecord.RawRecord.RecordId);
        Assert.Equal("b", result.Windows[0].NextAnchor!.AnchorId); Assert.Equal("a", result.Windows[1].PreviousAnchor!.AnchorId);
    }

    [Fact]
    public void LookbackCapUsesNextWindowStartToPreventDuplicateOwnership()
    {
        var result = Analyze([Combat(1, 1499), Combat(2, 1500)], [Anchor("a"), Anchor("b", 2000)], new() { BeforeMilliseconds = 500 });
        Assert.Equal(Start.AddMilliseconds(-500), result.Windows[0].WindowStart);
        Assert.Equal(Start.AddMilliseconds(1500), result.Windows[0].WindowEnd);
        Assert.Single(result.Windows[0].Records); Assert.Single(result.Windows[1].Records);
        Assert.All(result.AllRecords, r => Assert.Single(r.WindowAssociations));
    }

    [Fact]
    public void ExplicitOverlapRetainsMultipleAssociationsAndDoesNotDeduplicateDamage()
    {
        var result = Analyze([Combat(1, 2500)], [Anchor("a"), Anchor("b", 2000)], new() { CapAtNextAnchor = false });
        Assert.All(result.Windows, w => Assert.Single(w.Records));
        Assert.Equal(2, Assert.Single(result.AllRecords).WindowAssociations.Count);
        Assert.All(result.Windows, w => Assert.Contains(w.Warnings, s => s.Contains("overlap")));
    }

    [Fact]
    public void CoincidentAnchorsAreNotSilentlyMerged()
    {
        var result = Analyze([Combat(1, 0)], [Anchor("a"), Anchor("b")]);
        Assert.Equal(2, result.Windows.Count); Assert.Empty(result.Windows[0].Records); Assert.Single(result.Windows[1].Records);
        Assert.Contains(result.Windows[0].Warnings, s => s.Contains("Coincident"));
    }

    [Theory]
    [InlineData(-1, 4000, 3000)] [InlineData(0, 0, 3000)] [InlineData(0, 4000, -1)]
    [InlineData(double.NaN, 4000, 3000)] [InlineData(0, double.PositiveInfinity, 3000)]
    public void InvalidWindowPolicyFailsExplicitly(double before, double after, double auxiliary)
    {
        Assert.Throws<ArgumentException>(() => Analyze([], policy: new() { BeforeMilliseconds = before, AfterMilliseconds = after, AuxiliaryProximityMilliseconds = auxiliary }));
    }

    [Fact]
    public void TimestampTieUsesPacketThenOuterThenNestedOffsetsNotCodeTokenOrAmount()
    {
        var a = Combat(40, 500, 900, token: 255, amount: 900) with { PacketIndex = 2, OuterFrameOffset = 10, ContainerPath = [new(80, 9)] };
        var b = Combat(30, 500, 100, token: 1, amount: 100) with { PacketIndex = 2, OuterFrameOffset = 10, ContainerPath = [new(80, 12)] };
        var c = Combat(20, 500, 800) with { PacketIndex = 1, OuterFrameOffset = 1000 };
        var d = Combat(10, 500, 200) with { PacketIndex = 2, OuterFrameOffset = 20 };
        var result = Analyze([d,b,a,c]);
        Assert.Equal(new[] { 20,40,30,10 }, result.Windows[0].Records.Select(x => x.CombatRecord.RawRecord.RecordId));
        Assert.Equal(new uint[] { 800,900,100,200 }, result.Windows[0].RecordGroups[0].OrderedRawSkillCodes);
    }

    [Fact]
    public void ExactProvenanceTieFallsBackToStableRecordId()
    {
        var a = Combat(2, 500) with { PacketIndex = 1, OuterFrameOffset = 0 };
        var b = Combat(1, 500) with { PacketIndex = 1, OuterFrameOffset = 0 };
        Assert.Equal(new[] { 1,2 }, Analyze([a,b]).Windows[0].Records.Select(r => r.CombatRecord.RawRecord.RecordId));
    }

    [Fact]
    public void RepeatedCodesPreserveOrderMultisetSetAndSeparateAmounts()
    {
        var group = Assert.Single(Analyze([Combat(1,100,300), Combat(2,200,100), Combat(3,300,300)]).Windows[0].RecordGroups);
        Assert.Equal(new uint[] { 300,100,300 }, group.GroupPattern.ExactOrderedCodes);
        Assert.Equal(new uint[] { 100,300 }, group.GroupPattern.UniqueCodeSet);
        Assert.Equal(2, group.GroupPattern.CodeMultiset.Single(c => c.RawSkillCode == 300).Count);
        Assert.Equal(new[] { 0,2 }, Assert.Single(group.GroupPattern.RepeatedCodePositions).Positions);
        Assert.Equal(200, group.GroupPattern.ArrivalSpanMilliseconds);
        Assert.Equal(3, group.AggregateAmounts.Count); Assert.Equal(3, group.ComponentLists.Count);
    }

    [Fact]
    public void UnconstrainedWindowGroupsDifferentActualTuplesButDoesNotGuessControlledIdentity()
    {
        var decoded = Decode([Combat(1,100,source:7,target:8),Combat(2,200,source:9,target:8),Combat(3,300,source:7,target:10)]);
        var result = new ReplayActionCorrelation().Analyze("synthetic", decoded, [Anchor("a")]);
        Assert.Equal(3, result.Windows[0].RecordGroups.Count);
        Assert.All(result.AllRecords, r => Assert.Equal(RecordContextClassification.Unresolved, r.Classification));
    }

    [Theory]
    [InlineData(7ul,8ul,500,RecordContextClassification.ControlledTupleInsideWindow)]
    [InlineData(7ul,8ul,5000,RecordContextClassification.ControlledTupleOutsideWindow)]
    [InlineData(9ul,8ul,500,RecordContextClassification.OtherTupleInsideWindow)]
    [InlineData(7ul,10ul,500,RecordContextClassification.OtherTupleInsideWindow)]
    [InlineData(9ul,8ul,5000,RecordContextClassification.OtherTupleOutsideWindow)]
    public void BackgroundClassificationNeverDeletesARecord(ulong source, ulong target, double ms, RecordContextClassification expected)
    {
        var result = Analyze([Combat(1,ms,source:source,target:target)]);
        Assert.Equal(expected, Assert.Single(result.AllRecords).Classification);
        if (expected == RecordContextClassification.OtherTupleInsideWindow) Assert.Single(result.Windows[0].BackgroundRecords);
    }

    [Fact]
    public void PerAnchorContextOverridesGlobalContextWithoutCrossCaptureIdentity()
    {
        var result = Analyze([Combat(1,500,source:9)], [Anchor("a") with { SourceEntityId = 9 }]);
        Assert.Equal(RecordContextClassification.ControlledTupleInsideWindow, result.AllRecords[0].Classification);
        Assert.Equal(9ul, result.Windows[0].SourceEntityId);
    }

    [Fact]
    public void MixedCaptureAndDuplicateAnchorsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Analyze([Combat(1,500) with { SourceCapture = "other" }]));
        Assert.Throws<ArgumentException>(() => Analyze([], [Anchor("a") with { CaptureId = "other" }]));
        Assert.Throws<ArgumentException>(() => Analyze([], [Anchor("a"),Anchor("a")]));
    }

    [Fact]
    public void UnresolvedCombatCandidateKeepsPartialFieldsRawBytesAndWarnings()
    {
        var raw = Raw([4,0x38,8,5,0],1,100);
        var result = Analyze([raw]);
        var unresolved = Assert.Single(result.UnresolvedCombatCandidates);
        Assert.Same(raw, unresolved.RawRecord); Assert.NotEqual("Supported", unresolved.Status);
        Assert.NotEmpty(unresolved.Warnings); Assert.Empty(result.Windows[0].Records);
    }

    [Theory]
    [InlineData("0238")] [InlineData("0338")] [InlineData("0638")]
    public void AuxiliaryTupleTokenAndTimeEdgesPreserveNeutralTagAndRawRemainder(string tag)
    {
        var result = Analyze([Combat(1,500,101,token:5),Aux(tag,2,400,101,5)]);
        var edge = Assert.Single(result.AuxiliaryEdges);
        Assert.Equal(tag,edge.Tag); Assert.Equal(-100,edge.DeltaMs); Assert.Equal(1,edge.CombatRecordId);
        Assert.Equal(2,edge.AuxiliaryRecordId); Assert.Equal(7ul,edge.SourceEntityId);
        Assert.Equal(tag == "0638" ? null : 8ul,edge.TargetEntityId);
        Assert.Equal(new byte[] { 9,8,7 }, Assert.Single(result.AuxiliaryObservations).UnresolvedRemainder);
    }

    [Theory]
    [InlineData(9ul,8ul,101u,5,400)] [InlineData(7ul,9ul,101u,5,400)]
    [InlineData(7ul,8ul,102u,5,400)] [InlineData(7ul,8ul,101u,6,400)]
    [InlineData(7ul,8ul,101u,5,4000)]
    public void AuxiliaryWrongContextOrTimeNeverCollidesOnToken(ulong source,ulong target,uint code,int token,double ms)
    {
        var result = Analyze([Combat(1,500,101,token:5),Aux("0238",2,ms,code,(byte)token,source,target)]);
        Assert.Empty(result.AuxiliaryEdges); Assert.Single(result.AuxiliaryObservations);
    }

    [Fact]
    public void SameTokenAcrossCapturesIsNotAnEdge()
    {
        var combat = SupportedCombatRecord.From(CombatCandidateDecoder.Decode(Combat(1,500,101,token:5)));
        var aux = Assert.Single(AuxiliaryRecordDecoder.Decode([Aux("0638",2,400,101,5) with { SourceCapture = "other" }]).Observations);
        Assert.Empty(AuxiliaryRecordCorrelation.Match([combat],[aux],3000));
    }

    [Fact]
    public void AuxiliaryBeforeWindowIsRetainedWhenItMatchesInsideRecord()
    {
        var result = Analyze([Combat(1,100,101,token:5),Aux("0238",2,-100,101,5)]);
        Assert.Single(result.Windows[0].AuxiliaryObservations); Assert.Single(result.Windows[0].RecordGroups[0].AuxiliaryEdges);
    }

    [Fact]
    public void UnknownAuxiliaryPrefixAndTruncationRemainIssuesNotGuessedFields()
    {
        var unknown = Aux("0238",2,100,101,5, prefix:1);
        var truncated = Raw([6,0x38,7,1],3,100);
        var result = Analyze([unknown,truncated]);
        Assert.Empty(result.AuxiliaryObservations); Assert.Equal(2,result.AuxiliaryIssues.Count);
        Assert.Equal(unknown.RawBytes,result.AuxiliaryIssues[0].RawRecord.RawBytes);
    }

    [Fact]
    public void OutboundAuxiliaryTagIsNotPromoted()
    {
        var result = AuxiliaryRecordDecoder.Decode([Aux("0238",2,400,101,5) with { Direction = TrafficDirection.ClientToServer }]);
        Assert.Empty(result.Observations); Assert.Empty(result.Issues);
    }

    [Fact]
    public void ExternalAnnotationsDoNotNameOrModifyNetworkRecords()
    {
        var annotation = new ManualActionObservation { ManualSkillName = "External label", ManualPhaseLabel = "Phase X", ProcObserved = false,
            VisualDamageGroups = [new() { ObservationId = "visible", GroupRecordPosition = 0 }] };
        var raw = Combat(1,100,999,amount:100);
        var result = Analyze([raw],[Anchor("a") with { ExternalAnnotation = annotation }]);
        Assert.Same(annotation,result.Windows[0].ActionAnchor.ExternalAnnotation);
        Assert.Equal(ResearchMatchStatus.StructuralOnly,Assert.Single(result.Windows[0].VisualComparisons).Status);
        Assert.Null(annotation.VisualDamageGroups[0].BaseAmount); Assert.Null(annotation.VisualDamageGroups[0].ComponentValues);
        Assert.Equal(999u,result.Windows[0].Records[0].CombatRecord.RawSkillCode);
        Assert.Same(raw,result.Windows[0].Records[0].CombatRecord.RawRecord);
    }

    [Theory]
    [InlineData(100ul,ResearchMatchStatus.Exact)] [InlineData(101ul,ResearchMatchStatus.Contradicted)]
    public void ExplicitVisualSelectionCanSupportOrContradictWithoutNearestMatch(ulong amount,ResearchMatchStatus expected)
    {
        var observation = new VisualDamageObservation { ObservationId = "v",GroupRecordPosition = 0,FinalAmount = amount };
        var record = SupportedCombatRecord.From(CombatCandidateDecoder.Decode(Combat(1,100,amount:100)));
        Assert.Equal(expected,VisualNetworkMatcher.Match(observation,[record]).Status);
        Assert.Equal(100ul,record.AggregateAmount);
    }

    [Fact]
    public void EqualAmountsWithoutUniqueContextStayAmbiguousAndUnmatchedValuesStayVisible()
    {
        var records = new[] { Combat(1,100,amount:100),Combat(2,200,amount:100) }.Select(c => SupportedCombatRecord.From(CombatCandidateDecoder.Decode(c))).ToArray();
        var observation = new VisualDamageObservation { ObservationId = "v",FinalAmount = 100 };
        var ambiguous = VisualNetworkMatcher.Match(observation,records);
        Assert.Equal(ResearchMatchStatus.Ambiguous,ambiguous.Status); Assert.Equal(new[] { 1,2 },ambiguous.CandidateRecordIds);
        var unmatched = VisualNetworkMatcher.Match(observation with { FinalAmount = 99 },records);
        Assert.Equal(ResearchMatchStatus.Unmatched,unmatched.Status); Assert.Equal(99ul,unmatched.Observation.FinalAmount);
    }

    [Fact]
    public void ExactComponentsAreCheckedOnlyWhenExternallyKnown()
    {
        var record = SupportedCombatRecord.From(CombatCandidateDecoder.Decode(Combat(1,100,amount:100,components:[3,4])));
        var observed = new VisualDamageObservation { ObservationId = "v",RecordId = 1,FinalAmount = 100,BaseAmount = 93,ComponentValues = [3,4] };
        Assert.Equal(ResearchMatchStatus.Exact,VisualNetworkMatcher.Match(observed,[record]).Status);
        Assert.Equal(ResearchMatchStatus.Contradicted,VisualNetworkMatcher.Match(observed with { ComponentValues = [4,3] },[record]).Status);
    }

    [Fact]
    public void ExplicitVideoCorrectionPreservesOld361While363MatchesUnchangedNetwork()
    {
        var record = SupportedCombatRecord.From(CombatCandidateDecoder.Decode(Combat(1,100,amount:363)));
        var previous = new VisualDamageObservation { ObservationId = "v",RecordId = 1,FinalAmount = 361 };
        Assert.Equal(ResearchMatchStatus.Contradicted,VisualNetworkMatcher.Match(previous,[record]).Status);
        var corrected = previous with { FinalAmount = 363,CorrectionHistory = [new("FinalAmount","361","363","Explicit user re-watch of original video")] };
        var result = VisualNetworkMatcher.Match(corrected,[record]);
        Assert.Equal(ResearchMatchStatus.Exact,result.Status);
        Assert.Equal("361",Assert.Single(result.Observation.CorrectionHistory).PreviousValue);
        Assert.Equal(363ul,record.AggregateAmount);
        Assert.Null(corrected.BaseAmount); Assert.Null(corrected.DoubleObserved);
    }

    [Fact]
    public void PositiveOnlyAndExtraReusableCodeMultiplicityAreDerivedWithoutSemanticNames()
    {
        var result = Compare([Trial("p1",[10,20,30,20]),Trial("p2",[10,20,30,20])],[Trial("n1",[10,20]),Trial("n2",[10,20])]);
        Assert.Equal(new uint[] { 30 },result.PositiveOnlyCodes);
        Assert.Equal(new uint[] { 10,20 },result.CodesPresentInBothGroups);
        var companion = result.MultiplicityDifferences.Single(m => m.RawSkillCode == 20);
        Assert.Equal(2,companion.PositiveMinimum); Assert.Equal(1,companion.NegativeMaximum); Assert.True(companion.ConsistentPositiveIncrease);
        var difference = Assert.Single(result.OrderedSubsequenceDifferences);
        Assert.True(difference.NegativeIsSubsequence); Assert.Equal(new uint[] { 30,20 },difference.AdditionalOrderedCodes);
    }

    [Fact]
    public void CodeInEveryPositiveAndEveryControlCannotBePromotedAsExclusive()
    {
        var result = Compare([Trial("p",[10,20,40])],[Trial("n",[10,20,40])]);
        Assert.Empty(result.PositiveOnlyCodes); Assert.Empty(result.AmbiguousCodes);
        Assert.All(result.MultiplicityDifferences,m => Assert.False(m.ConsistentPositiveIncrease));
        Assert.Contains(40u,result.CodesPresentInBothGroups);
    }

    [Fact]
    public void MixedPositivePresenceOrAnyNegativePresencePreventsPositiveOnlyClaim()
    {
        var result = Compare([Trial("p1",[10,30,40]),Trial("p2",[10,40])],[Trial("n1",[10,40]),Trial("n2",[10])]);
        Assert.Empty(result.PositiveOnlyCodes); Assert.Equal(new uint[] { 30,40 },result.AmbiguousCodes);
    }

    [Fact]
    public void IncomparableOrderedPatternsRemainUnresolvedSubsequenceRatherThanReordered()
    {
        var result = Compare([Trial("p",[20,10])],[Trial("n",[10,20])]);
        var difference = Assert.Single(result.OrderedSubsequenceDifferences);
        Assert.False(difference.NegativeIsSubsequence); Assert.Empty(difference.AdditionalOrderedCodes);
        Assert.Empty(result.PositiveOnlyCodes);
    }

    [Fact]
    public void EmptyResponseTrialsRemainInPresenceDenominator()
    {
        var result = Compare([Trial("p1",[10]),Trial("p2",[])],[Trial("n",[])]);
        Assert.Equal(2,result.PositiveTrials); Assert.Empty(result.CodesPresentInEveryPositive); Assert.Contains(10u,result.AmbiguousCodes);
        Assert.Equal(1,result.PositiveDistributions[0].PresenceTrials);
    }

    [Fact]
    public void AmountRawFlagsAuxiliaryFootprintsAndTimingRemainPerRecordDistributions()
    {
        var a = SupportedCombatRecord.From(CombatCandidateDecoder.Decode(Combat(1,500,10,amount:100)));
        var b = SupportedCombatRecord.From(CombatCandidateDecoder.Decode(Combat(2,900,10,amount:200,type:3,modifier:8)));
        var edges = new[] { new AuxiliaryRecordEdge("synthetic",1,9,"0238",7,8,10,1,-100,ResearchConfidence.High) };
        var trial = new ResearchTrial("synthetic","p",[a,b],edges,Start);
        var result = Compare([trial],[Trial("n",[])]);
        var distribution = Assert.Single(result.PositiveDistributions);
        Assert.Equal(new ulong[] { 100,200 },distribution.AggregateAmounts);
        Assert.Equal(2,distribution.RawFlagCombinations.Count); Assert.Equal(500,distribution.DeltaFromAnchorMilliseconds!.Minimum);
        Assert.Equal(900,distribution.DeltaFromAnchorMilliseconds.Maximum); Assert.Equal("0238",Assert.Single(distribution.AuxiliaryFootprint).Tag);
    }

    [Fact]
    public void DuplicateOrOverlappingSelectionsAndEmptyLabeledGroupsFailInsteadOfBiasingCounts()
    {
        var trial = Trial("same",[10]);
        Assert.Throws<ArgumentException>(() => Compare([trial],[trial]));
        Assert.Throws<ArgumentException>(() => Compare([trial,trial],[Trial("n",[])]));
        Assert.Throws<ArgumentException>(() => Compare([],[trial]));
    }

    [Theory]
    [InlineData("action-windows")] [InlineData("record-groups")] [InlineData("compare-groups")]
    public void ResearchCliDispatchesNewHelpWithoutCaptureOrUi(string mode)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0,ResearchCli.Run([mode,"--help"],output,error)); Assert.Empty(error.ToString());
        Assert.Contains("4000",output.ToString());
    }

    [Theory]
    [InlineData("action-windows")] [InlineData("record-groups")]
    public void JsonCliUsesGenericDefinitionAndCompressedSyntheticPcap(string mode)
    {
        using var files = new TestFiles();
        var path = files.WritePcap([Packet(ReplayProtocolDecoderTests.Pack(Combat(1,0,300).RawBytes.Concat(Combat(2,0,100).RawBytes).ToArray()))]);
        var definition = new ActionResearchDefinition { Capture = path, SemanticLabel = "External corrected alias",Local = "198.51.100.2:443",Remote = "192.0.2.1:12345",SourceEntityId = 7,TargetEntityId = 8,
            Anchors = [Anchor("trial") with { CaptureId = "",AnchorSource = ActionAnchorSource.ManualAnnotation }] };
        var definitionPath = Path.ChangeExtension(path,"definition.json");
        try
        {
            File.WriteAllText(definitionPath,JsonSerializer.Serialize(definition,ActionResearchCli.JsonOptions));
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(0,ResearchCli.Run([mode,path,"--definition",definitionPath,"--json"],output,error)); Assert.Empty(error.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            var window = json.RootElement.GetProperty("Windows")[0];
            Assert.Equal("External corrected alias",json.RootElement.GetProperty("SemanticLabel").GetString());
            Assert.Equal(2,window.GetProperty("Records").GetArrayLength());
            Assert.Equal(new uint[] { 300,100 },window.GetProperty("RecordGroups")[0].GetProperty("OrderedRawSkillCodes").EnumerateArray().Select(c => c.GetUInt32()));
            Assert.Equal(1,window.GetProperty("Records")[0].GetProperty("CombatRecord").GetProperty("RawRecord").GetProperty("ContainerPath").GetArrayLength());
        }
        finally { File.Delete(definitionPath); }
    }

    [Fact]
    public void NoAnchorDoesNotLoseSuppliedContextOrOutsideRecord()
    {
        var result = Analyze([Combat(1,100)], []);
        Assert.Empty(result.Windows);
        Assert.Equal(RecordContextClassification.ControlledTupleOutsideWindow, Assert.Single(result.AllRecords).Classification);
    }

    [Fact]
    public void CompareCliResolvesRelativeDefinitionsAndKeepsZeroResponseControl()
    {
        using var files = new TestFiles();
        var path = files.WritePcap([Packet(Combat(1,0,300).RawBytes)]);
        var definitionPath = Path.ChangeExtension(path,"definition.json");
        var comparisonPath = Path.ChangeExtension(path,"comparison.json");
        var definition = new ActionResearchDefinition { Capture = Path.GetFileName(path),Local = "198.51.100.2:443",Remote = "192.0.2.1:12345",
            SourceEntityId = 7,TargetEntityId = 8,Anchors = [Anchor("positive") with { CaptureId = "" },Anchor("negative",5000) with { CaptureId = "" }] };
        var comparison = new GroupComparisonDefinition([
            new("external positive",[new(Path.GetFileName(definitionPath),["positive"])]),
            new("external negative",[new(Path.GetFileName(definitionPath),["negative"])])]);
        try
        {
            File.WriteAllText(definitionPath,JsonSerializer.Serialize(definition,ActionResearchCli.JsonOptions));
            File.WriteAllText(comparisonPath,JsonSerializer.Serialize(comparison,ActionResearchCli.JsonOptions));
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(0,ResearchCli.Run(["compare-groups",comparisonPath,"--json"],output,error)); Assert.Empty(error.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(300u,json.RootElement.GetProperty("PositiveOnlyCodes")[0].GetUInt32());
            Assert.Equal(1,json.RootElement.GetProperty("NegativeControlTrials").GetInt32());
            Assert.Equal("external positive",json.RootElement.GetProperty("PositiveLabel").GetString());
        }
        finally { File.Delete(definitionPath); File.Delete(comparisonPath); }
    }

    [Theory]
    [InlineData("--source","-1")] [InlineData("--after-ms","NaN")] [InlineData("--unknown","x")]
    public void CliRejectsInvalidOptionsBeforeOpeningCapture(string option,string value)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2,ResearchCli.Run(["action-windows","missing.pcap",option,value],output,error));
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public void ResearchJsonRoundTripPreservesUnknownFlagsAndCorrectionHistory()
    {
        var observed = new VisualDamageObservation { ObservationId = "v",FinalAmount = 363,FrontObserved = true,
            CorrectionHistory = [new("FinalAmount","361","363","Explicit external video correction")] };
        var roundTrip = JsonSerializer.Deserialize<VisualDamageObservation>(JsonSerializer.Serialize(observed,ActionResearchCli.JsonOptions),ActionResearchCli.JsonOptions)!;
        Assert.Null(roundTrip.BaseAmount); Assert.Null(roundTrip.ComponentValues); Assert.Null(roundTrip.CriticalObserved);
        Assert.True(roundTrip.FrontObserved); Assert.Equal("361",Assert.Single(roundTrip.CorrectionHistory).PreviousValue);
    }

    [Fact]
    public void MetadataCueUsesObservedTimestampAndDoesNotInventAKeypress()
    {
        using var files = new TestFiles(); var path = files.WritePcap([Packet(Combat(1,0).RawBytes)]);
        var metadataPath = Path.ChangeExtension(path,".json");
        var marker = TestMarker.UserActionCue(Start,Start.AddSeconds(5),Start.AddMilliseconds(5500),500);
        var metadata = new SessionMetadata(Guid.NewGuid(),"synthetic",Start,Start.AddSeconds(10),TimeSpan.FromSeconds(10),
            new("id","name","description",[]),CaptureMode.AllTraffic,null,null,[],"",1,1,1,0,0,null) { TestMarkers = [marker] };
        try
        {
            File.WriteAllText(metadataPath,SessionMetadataStore.Serialize(metadata));
            var result = ActionResearchCli.Analyze(new() { Capture = path,Local = "198.51.100.2:443",Remote = "192.0.2.1:12345",IncludeMetadataCues = true });
            var anchor = Assert.Single(result.ActionAnchors);
            Assert.Equal(ActionAnchorSource.UserActionCue,anchor.AnchorSource); Assert.Equal(marker.TimestampUtc,anchor.Timestamp);
            Assert.NotEqual(marker.ScheduledUtc,anchor.Timestamp); Assert.Null(anchor.PacketIndex); Assert.Null(anchor.ExternalAnnotation);
            Assert.Equal(RecordContextClassification.Unresolved,Assert.Single(result.AllRecords).Classification);
        }
        finally { File.Delete(metadataPath); }
    }

    [Fact]
    public void CallerSuppliedOutboundSequenceDerivesAnchorWithOriginalPacketIndices()
    {
        using var files = new TestFiles();
        var a = ReplayProtocolDecoderTests.Frame([0xaa,0xbb]); var b = ReplayProtocolDecoderTests.Frame([0xaa,0xbb,0xcc]); var c = ReplayProtocolDecoderTests.Frame([0xaa,0xbb,0xcc,0xdd]);
        var packets = new[] { Packet(a,100),Packet(b,100+(uint)a.Length,5),Packet(c,100+(uint)(a.Length+b.Length),10) };
        var path = files.WritePcap(packets);
        var result = ActionResearchCli.Analyze(new() { Capture = path,Local = "192.0.2.1:12345",Remote = "198.51.100.2:443",
            OutboundSequence = packets.Select(p => p.Data.Length).ToArray() });
        var anchor = Assert.Single(result.ActionAnchors);
        Assert.Equal(ActionAnchorSource.OutboundSequence,anchor.AnchorSource); Assert.Equal(Start,anchor.Timestamp);
        Assert.Equal(new long[] { 1,2,3 },anchor.SequencePacketIndices); Assert.Equal(1,anchor.PacketIndex);
        Assert.Empty(result.Windows[0].Records);
    }

    [Fact]
    public void MissingComparisonDocumentReturnsControlledFailureWithoutUnhandledException()
    {
        using var files = new TestFiles(); var path = files.WritePcap([]);
        var definitionPath = Path.ChangeExtension(path,"comparison.json");
        try
        {
            File.WriteAllText(definitionPath,"null");
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(1,ResearchCli.Run(["compare-groups",definitionPath,"--json"],output,error));
            Assert.Contains("Missing research definition",error.ToString());
        }
        finally { File.Delete(definitionPath); }
    }

    private static ResearchActionAnchor Anchor(string id,double ms = 0) => new() { AnchorId = id,CaptureId = "synthetic",Timestamp = Start.AddMilliseconds(ms) };
    private static ActionCorrelationResult Analyze(RawProtocolRecord[] raw,ResearchActionAnchor[]? anchors = null,ActionWindowPolicy? policy = null) =>
        new ReplayActionCorrelation().Analyze("synthetic",Decode(raw),anchors ?? [Anchor("a")],policy,7,8);
    private static ProtocolDecodeResult Decode(RawProtocolRecord[] raw) => new(raw,raw.Where(r => r.OpcodeCandidate == "0438").Select(CombatCandidateDecoder.Decode).ToArray(),[]);
    private static DifferentialAssociation Compare(ResearchTrial[] positive,ResearchTrial[] negative) => ResearchGroupComparison.Compare("positive",positive,"negative",negative);
    private static ResearchTrial Trial(string id,uint[] codes) => new("synthetic",id,codes.Select((code,i) => SupportedCombatRecord.From(CombatCandidateDecoder.Decode(Combat(i+1,(i+1)*100,code)))).ToArray(),[],Start);
    private static byte[] V(ulong value) => ReplayProtocolDecoderTests.Varint(value);
    private static RawProtocolRecord Raw(byte[] body,int id,double ms)
    {
        var bytes = ReplayProtocolDecoderTests.Frame(body); var frame = ApplicationFraming.Read(bytes); var time = Start.AddMilliseconds(ms);
        return new(id,"synthetic",TrafficDirection.ServerToClient,id*100,id,id*100,time,id,id,time,frame.PrefixLength,bytes.Length,bytes,
            Convert.ToHexString(body.AsSpan(0,2)),[],"Unknown",[]);
    }
    private static RawProtocolRecord Combat(int id,double ms,uint code = 101,ulong source = 7,ulong target = 8,byte token = 1,
        ulong amount = 100,ulong[]? components = null,byte type = 2,byte modifier = 0)
    {
        components ??= []; var rawCode = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(rawCode,code);
        return Raw([4,0x38,..V(target),components.Length > 0 ? (byte)0x26 : (byte)6,0,..V(source),..rawCode,token,type,modifier,0,2,
            9,8,7,6,1,0,0,0,1,..V(amount),..(components.Length > 0 ? V((ulong)components.Length).Concat(components.SelectMany(V)) : []),1,0],id,ms);
    }
    private static RawProtocolRecord Aux(string tag,int id,double ms,uint code,byte token,ulong source = 7,ulong target = 8,ulong prefix = 0)
    {
        var rawCode = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(rawCode,code);
        return Raw([..Convert.FromHexString(tag),..V(source),..(tag == "0638" ? [] : V(prefix)),..(tag == "0338" ? V(target) : []),
            ..rawCode,token,..(tag == "0238" ? V(1).Concat(V(target)) : []),9,8,7],id,ms);
    }
    private static CapturedPacket Packet(byte[] payload,uint sequence = 100,double ms = 0)
    {
        var template = TestFiles.Packet(6,Start.AddMilliseconds(ms)); var data = new byte[54+payload.Length]; template.Data.CopyTo(data,0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16),(ushort)(40+payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(38),sequence); data[47] = (byte)(TcpFlags.Psh | TcpFlags.Ack); payload.CopyTo(data,54);
        return template with { Data = data,OriginalLength = data.Length };
    }
}
