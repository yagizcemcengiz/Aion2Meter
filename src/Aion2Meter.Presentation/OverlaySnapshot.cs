using Aion2Meter.Core;
using Aion2Meter.Replay;

namespace Aion2Meter.Presentation;

public enum OverlayState { Waiting, Ready, InCombat, Idle, Unavailable, Stopped }
public sealed record OverlayRow(string Key, int Rank, string DisplayName, bool IsSelf,
    decimal TotalDamage, decimal? Dps, decimal ContributionPercent, long SelfEvents);

/// <summary>Presentation only. No packets, transport objects, actor dictionaries or mutable backend state.</summary>
public sealed record OverlaySnapshot(OverlayState State, string Status, string Hint, double ElapsedSeconds,
    string Coverage, string CoverageDetails, IReadOnlyList<OverlayRow> Rows)
{
    public static OverlaySnapshot Waiting { get; } = Empty(OverlayState.Waiting,
        "Waiting for character identity...", "Will recover on the next fresh world connection.");
    public static OverlaySnapshot Stopped { get; } = Empty(OverlayState.Stopped, "Meter stopped", "Start live overlay from Aion2Meter.");
    public static OverlaySnapshot Unavailable { get; } = Empty(OverlayState.Unavailable,
        "Data unavailable", "Check the capture adapter and reconnect when ready.");

    private static OverlaySnapshot Empty(OverlayState state, string status, string hint) =>
        new(state, status, hint, 0, "Coverage: Partial", LiveCombatMeter.Coverage, Array.AsReadOnly(Array.Empty<OverlayRow>()));

    public static OverlaySnapshot FromMeter(LiveMeterSnapshot meter)
    {
        var coverage = meter.Coverage.StartsWith("PARTIAL", StringComparison.OrdinalIgnoreCase) ? "Coverage: Partial" : "Coverage: " + Clean(meter.Coverage);
        OverlaySnapshot EmptyWithCoverage(OverlaySnapshot empty) => empty with { Coverage = coverage, CoverageDetails = meter.Coverage };
        if (meter.Status.StartsWith("UNTRUSTED", StringComparison.Ordinal) || meter.Status.StartsWith("AMBIGUOUS", StringComparison.Ordinal) ||
            meter.BindingStatus == CurrentPlayerBindingStatus.Conflict)
            return EmptyWithCoverage(Unavailable);
        if (meter.Status == "STOPPED") return EmptyWithCoverage(Stopped);
        if (meter.BindingStatus != CurrentPlayerBindingStatus.Resolved || meter.EpochId is null ||
            meter.EntityId is null || string.IsNullOrWhiteSpace(meter.CharacterName)) return EmptyWithCoverage(Waiting);
        var state = meter.Status == "IN COMBAT" ? OverlayState.InCombat :
            meter.Status.StartsWith("IDLE", StringComparison.Ordinal) ? OverlayState.Idle : OverlayState.Ready;
        var row = new OverlayRow($"{meter.EpochId}/{meter.EntityId}", 1, Clean(meter.CharacterName), true,
            meter.TotalDamage, meter.Dps, 100m, meter.EncounterSelfHits);
        // Other/Unknown counts are diagnostics, never a source of display rows.
        return new(state, state == OverlayState.InCombat ? "In combat" : state == OverlayState.Idle ? "Last encounter" : "Ready",
            "", meter.EncounterElapsedSeconds, coverage, meter.Coverage, Array.AsReadOnly(new[] { row }));
    }

    private static string Clean(string value) => string.Concat(value.Where(c => !char.IsControl(c)).Take(128));
}
