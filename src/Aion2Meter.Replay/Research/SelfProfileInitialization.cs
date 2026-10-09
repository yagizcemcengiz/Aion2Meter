using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public sealed partial class ReplayCurrentPlayerBindingResolver
{
    internal static (StableCharacterIdentity Identity, CurrentPlayerBindingEvidence Evidence)? SelfProfile(RawProtocolRecord record)
    {
        var inspected = PlayerProfileDecoder.Inspect(record);
        if (inspected.Profile is not { Source: PlayerClassSource.Character3336, Class: not PlayerClass.Unknown } profile)
            return null;
        var candidates = LocalInitializationExtractor.Extract(record);
        var leading = UnsignedVarint.Read(record.RawBytes.AsSpan(record.PrefixLength + 2), requireCanonical: true);
        var nameLengthAt = record.PrefixLength + 2 + leading.BytesConsumed + 5;
        var exact = candidates.Where(e => e.EntityId == profile.EntityId && e.CharacterName == profile.CharacterName &&
            e.NumericRange.Offset == record.PrefixLength + 2 && e.NameLengthOffset == nameLengthAt).ToArray();
        if (exact.Length != 1) return null;
        var evidence = exact[0];
        if (inspected.ServerField is not { } server || server == 0) return null; // No newly invented sentinel semantics.
        return (new(profile.CharacterName, profile.Class, server, profile.FactionCode,
            inspected.LayoutType + ":canonical-id,opaque-u32,name-u8,server-u16,class-u32,faction-u8"), evidence);
    }

    private static CurrentPlayerBinding ResolveSelfProfiles(ResearchCapture capture, ReplayConnectionScope scope,
        IReadOnlyList<RawProtocolRecord> records, DateTimeOffset? until, CurrentPlayerBinding? prior = null)
    {
        DateTimeOffset? coverage = capture.Packets.Count == 0 ? prior?.EvidenceCoverageEnd : capture.Packets.Max(p => p.Segment.TimestampUtc);
        var local = records.OrderBy(r => r, ResearchRecordArrivalComparer.Instance).ToArray();
        var profiles = local.Select(SelfProfile).ToArray();
        var known = profiles.Where(p => p is not null).Select(p => p!.Value).ToArray();
        // Branch fingerprint is profile provenance, not a stable character fact.
        var identities = known.Select(p => p.Identity).Concat(prior?.StableIdentity is { } existing ? [existing] : Array.Empty<StableCharacterIdentity>())
            .GroupBy(i => (i.CharacterName, i.Class, i.ServerId, i.FactionCode)).Select(g => g.First()).ToArray();
        var stable = identities.Length == 1 ? prior?.StableIdentity ?? identities[0] : null;
        CurrentPlayerBinding Withheld(CurrentPlayerBindingStatus status, string reason) => new(status, scope,
            null, null, null, null, null, coverage, known.Select(p => p.Evidence), [reason], stable);
        if (identities.Length == 0) return Withheld(CurrentPlayerBindingStatus.Unknown,
            "No complete validated local profile; cross-server hypotheses cannot establish Self.");
        if (identities.Length != 1 || prior is not null && prior.CharacterName != stable!.CharacterName)
            return Withheld(CurrentPlayerBindingStatus.Conflict, "Conflicting complete local Self profile identities; no guessed winner.");
        var declared = records.Where(r => r.OpcodeCandidate == "1536").Select(LocalInitializationExtractor.Extract)
            .Where(c => c.Count == 1).Select(c => c[0]);
        if (declared.Any(c => c.CharacterName != stable!.CharacterName || known.Length > 0 &&
            !known.Any(p => p.Evidence.EntityId == c.EntityId) && prior?.EntityId != c.EntityId))
            return Withheld(CurrentPlayerBindingStatus.Conflict, "Unambiguous 1536 declaration contradicts independently fixed local profile assignments.");
        if (local.Zip(local.Skip(1)).Any(pair => !Precedes(pair.First, pair.Second)))
            return Withheld(CurrentPlayerBindingStatus.Unknown, "Stable Self observed; ambiguous profile ordering withholds current actor.");
        if (local.Any(r => LocalInitializationExtractor.Extract(r).Count == 0))
            return Withheld(CurrentPlayerBindingStatus.Unknown, "Malformed initialization cannot retain runtime authority.");
        var history = new List<CurrentPlayerBinding>();
        CurrentPlayerBinding? current = prior?.CurrentOnly();
        void Retire(CurrentPlayerBindingEvidence e)
        {
            if (current is null) return;
            history.Add(new(current.Status, scope, current.EntityId, current.CharacterName, current.CandidateObservedFrom,
                current.ValidFrom, e.Timestamp, coverage, current.Evidence, current.Diagnostics, stable,
                retirementEvidence: e));
            current = null;
        }
        foreach (var r in local)
        {
            var profile = SelfProfile(r);
            if (profile is null)
            {
                var fields = LocalInitializationExtractor.Extract(r);
                // A corroborating declaration does not move an already confirmed assignment.
                if (r.OpcodeCandidate == "1536" && fields.Count == 1 && current?.EntityId == fields[0].EntityId &&
                    current.CharacterName == fields[0].CharacterName) continue;
                if (fields.Count > 0) Retire(fields[0]); // Boundary only; these hypotheses grant no actor authority.
                if (history.Count > 16) return Withheld(CurrentPlayerBindingStatus.Unknown, "Runtime initialization interval bound exceeded.");
                continue;
            }
            var p = profile.Value;
            var e = p.Evidence;
            if (current?.EntityId == e.EntityId)
            {
                // A complete same-assignment refresh cannot move the original ValidFrom.
                if (current.StableIdentity is null)
                    current = new(current.Status, scope, current.EntityId, current.CharacterName, current.CandidateObservedFrom,
                        current.ValidFrom, current.ValidUntil, coverage, current.Evidence, current.Diagnostics, stable);
                continue;
            }
            Retire(e);
            if (history.Count > 16) return Withheld(CurrentPlayerBindingStatus.Unknown, "Runtime initialization interval bound exceeded.");
            current = new(CurrentPlayerBindingStatus.Resolved, scope, e.EntityId, e.CharacterName, e.Timestamp,
                e.CompletionTimestamp, until, coverage, [e], [$"Complete fixed-boundary {p.Identity.LayoutFingerprint.Split(':')[0]} local Self profile in a proven fresh epoch; runtime valid only from this confirmation."], stable);
        }
        if (current is null)
            return new(CurrentPlayerBindingStatus.Unknown, scope, null, null, null, null, null, coverage,
                known.Select(p => p.Evidence), ["Stable local Self retained; unsupported initialization boundary retired runtime. Await a complete fixed local profile; uncertain combat is not eligible."],
                stable, history, awaitingActor: true);
        if (until is { } stop && stop < current.ValidFrom)
            return Withheld(CurrentPlayerBindingStatus.Unknown, "Connection ended before local profile confirmation.");
        return new(current.Status, scope, current.EntityId, current.CharacterName, current.CandidateObservedFrom,
            current.ValidFrom, until, coverage, current.Evidence, current.Diagnostics, stable, history);
    }
}
