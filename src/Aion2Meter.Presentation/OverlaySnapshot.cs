using Aion2Meter.Core;
using Aion2Meter.Replay;

namespace Aion2Meter.Presentation;

public enum OverlayState { Waiting, Ready, InCombat, Idle, Unavailable, Stopped }
public sealed record OverlayRow(string Key, int Rank, string DisplayName, bool IsSelf,
    decimal TotalDamage, decimal? Dps, decimal ContributionPercent, long SelfEvents, PlayerClass Class = PlayerClass.Unknown);

/// <summary>Presentation only. No packets, transport objects, actor dictionaries or mutable backend state.</summary>
public sealed record OverlaySnapshot(OverlayState State, string Status, string Hint, double ElapsedSeconds,
    string Coverage, string CoverageDetails, IReadOnlyList<OverlayRow> Rows, double? NetworkRttMilliseconds = null)
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
        if (meter.StableIdentity is { } stable && meter.BindingStatus == CurrentPlayerBindingStatus.Unknown && meter.Status.StartsWith("AWAITING ACTOR", StringComparison.Ordinal))
            return new(OverlayState.Waiting, "Waiting for current actor...", "Character recognized; combat counting is paused.",
                0, coverage, meter.Coverage, Array.AsReadOnly((meter.Members ?? [new(null, stable.CharacterName, true, 0, null, 0, 0, false, Class: stable.Class)])
                    .Select((m, i) => new OverlayRow(m.IsSelf ? SelfRowKey(meter) : $"party/{m.MembershipKey}", i + 1,
                        Clean(m.CharacterName), m.IsSelf, 0, null, 0, 0, m.Class)).ToArray()));
        if (meter.Status.StartsWith("UNTRUSTED", StringComparison.Ordinal) || meter.Status.StartsWith("AMBIGUOUS", StringComparison.Ordinal) ||
            meter.BindingStatus == CurrentPlayerBindingStatus.Conflict)
            return EmptyWithCoverage(Unavailable);
        if (meter.Status == "STOPPED") return EmptyWithCoverage(Stopped);
        if (meter.BindingStatus != CurrentPlayerBindingStatus.Resolved || meter.EpochId is null ||
            meter.EntityId is null || string.IsNullOrWhiteSpace(meter.CharacterName)) return EmptyWithCoverage(Waiting);
        var state = meter.Status == "IN COMBAT" ? OverlayState.InCombat :
            meter.Status.StartsWith("IDLE", StringComparison.Ordinal) ? OverlayState.Idle : OverlayState.Ready;
        var members = meter.CurrentMembers ?? meter.Members ?? [new LiveMeterMemberSnapshot(meter.EntityId.Value, meter.CharacterName, true,
            meter.TotalDamage, meter.Dps, 100m, meter.EncounterSelfHits, false)];
        var rows = members.OrderByDescending(m => m.Dps ?? 0m).ThenBy(m => m.CharacterName, StringComparer.Ordinal)
            .ThenBy(m => m.EntityId).Select((m, index) => new OverlayRow(m.IsSelf ? SelfRowKey(meter) : m.MembershipKey is { } key ? $"party/{key}" : $"{meter.EpochId}/{m.EntityId?.ToString(System.Globalization.CultureInfo.InvariantCulture)}", index + 1,
                Clean(m.CharacterName), m.IsSelf, m.TotalDamage, m.Dps, m.ContributionPercent, m.Hits, m.Class)).ToArray();
        // Other/Unknown counts are diagnostics, never a source of display rows.
        return new(state, state == OverlayState.InCombat ? "In combat" : state == OverlayState.Idle ? "Last encounter" : "Ready",
            "", meter.EncounterElapsedSeconds, coverage, meter.Coverage, Array.AsReadOnly(rows));
    }

    private static string SelfRowKey(LiveMeterSnapshot meter)
    {
        // Presentation only. Runtime authority still belongs to the independently validated binding.
        var stable = meter.StableIdentity;
        var identity = $"{stable?.CharacterName ?? meter.CharacterName}\0{stable?.ServerId?.ToString(System.Globalization.CultureInfo.InvariantCulture)}\0{stable?.FactionCode}";
        return "self/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
    }

    private static string Clean(string value) => string.Concat(value.Where(c => !char.IsControl(c)).Take(128));
}
