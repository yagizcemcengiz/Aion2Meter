using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Aion2Meter.Core;

namespace Aion2Meter.Presentation;

public abstract class ObservableView : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new(name));
    }
}

public sealed class OverlayRowViewModel : ObservableView
{
    public string Key { get; }
    private string name = "", dps = "—", damage = "0", contribution = "100%";
    private double percent;
    private int rank;
    private bool self;
    private string accent = "#71E1C1";
    private PlayerClass playerClass;
    private string classHint = "Unknown";
    public PlayerClass Class { get => playerClass; private set => Set(ref playerClass, value); }
    public string ClassHint { get => classHint; private set => Set(ref classHint, value); }
    public string Accent { get => accent; private set => Set(ref accent, value); }
    public string DisplayName { get => name; private set => Set(ref name, value); }
    public string Dps { get => dps; private set => Set(ref dps, value); }
    public string Damage { get => damage; private set => Set(ref damage, value); }
    public string Contribution { get => contribution; private set => Set(ref contribution, value); }
    public double ContributionValue { get => percent; private set => Set(ref percent, value); }
    public int Rank { get => rank; private set => Set(ref rank, value); }
    public bool IsSelf { get => self; private set => Set(ref self, value); }
    public OverlayRowViewModel(OverlayRow row, PlayerClass? selfClassOverride = null) { Key = row.Key; Apply(row, selfClassOverride); }
    public void Apply(OverlayRow row, PlayerClass? selfClassOverride = null)
    {
        if (row.Key != Key) throw new ArgumentException("A row belongs to one stable identity scope.");
        var culture = CultureInfo.InvariantCulture;
        DisplayName = row.DisplayName; Rank = row.Rank; IsSelf = row.IsSelf;
        Accent = PlayerAccent.For(row.DisplayName, row.IsSelf);
        Class = Enum.IsDefined(row.Class) ? row.Class : PlayerClass.Unknown;
        var manual = Class == PlayerClass.Unknown && row.IsSelf && selfClassOverride is { } selected &&
            Enum.IsDefined(selected) && selected != PlayerClass.Unknown;
        if (manual) Class = selfClassOverride!.Value;
        ClassHint = manual ? $"{Class} (your display preference)" : Class.ToString();
        Dps = row.Dps is { } rate ? CompactNumber.Format(rate, rate: true) : "—"; Damage = CompactNumber.Format(row.TotalDamage);
        Contribution = row.ContributionPercent.ToString("0.#", culture) + "%";
        ContributionValue = (double)Math.Clamp(row.ContributionPercent, 0, 100);
    }
}

/// <summary>Owned by the UI thread; rows and visual controls survive ordinary ticks.</summary>
public sealed class OverlayViewModel : ObservableView
{
    private readonly ObservableCollection<OverlayRowViewModel> rows = [];
    private OverlaySnapshot? previous;
    private PlayerClass? selfClassOverride;
    private string status = "Waiting for character identity...", hint = "Will recover on the next fresh world connection.",
        elapsed = "00:00.0", coverage = "Coverage: Partial", details = "";
    private string ping = "— ms";
    public string Ping { get => ping; private set => Set(ref ping, value); }
    private string signal1 = LatencyQuality.Inactive, signal2 = LatencyQuality.Inactive, signal3 = LatencyQuality.Inactive, signal4 = LatencyQuality.Inactive;
    private int activeSignalBars;
    public int ActiveSignalBars { get => activeSignalBars; private set => Set(ref activeSignalBars, value); }
    public string Signal1 { get => signal1; private set => Set(ref signal1, value); }
    public string Signal2 { get => signal2; private set => Set(ref signal2, value); }
    public string Signal3 { get => signal3; private set => Set(ref signal3, value); }
    public string Signal4 { get => signal4; private set => Set(ref signal4, value); }
    public ReadOnlyObservableCollection<OverlayRowViewModel> Rows { get; }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Hint { get => hint; private set => Set(ref hint, value); }
    public string Elapsed { get => elapsed; private set => Set(ref elapsed, value); }
    public string Coverage { get => coverage; private set => Set(ref coverage, value); }
    public string CoverageDetails { get => details; private set => Set(ref details, value); }
    public OverlayViewModel() { Rows = new(rows); Apply(OverlaySnapshot.Waiting); }
    /// <summary>Reprojects the cached snapshot immediately, without changing identity, rows, or accounting.</summary>
    public void SetSelfClassOverride(PlayerClass? value)
    {
        var validated = value is { } selected && Enum.IsDefined(selected) && selected != PlayerClass.Unknown ? value : null;
        if (selfClassOverride == validated) return;
        selfClassOverride = validated;
        if (previous is null) return;
        foreach (var row in previous.Rows)
            rows.FirstOrDefault(r => r.Key == row.Key)?.Apply(row, selfClassOverride);
    }
    public void Apply(OverlaySnapshot snapshot)
    {
        if (ReferenceEquals(previous, snapshot)) return;
        previous = snapshot;
        Status = snapshot.Status; Hint = snapshot.Hint; Coverage = snapshot.Coverage; CoverageDetails = snapshot.CoverageDetails;
        Ping = snapshot.NetworkRttMilliseconds is { } ms && double.IsFinite(ms) && ms >= 0
            ? "≈ " + Math.Round(ms).ToString("0", CultureInfo.InvariantCulture) + " ms" : "— ms";
        var quality = LatencyQuality.From(snapshot.NetworkRttMilliseconds); ActiveSignalBars = quality.Bars;
        Signal1 = quality.Bars >= 1 ? quality.Color : LatencyQuality.Inactive;
        Signal2 = quality.Bars >= 2 ? quality.Color : LatencyQuality.Inactive;
        Signal3 = quality.Bars >= 3 ? quality.Color : LatencyQuality.Inactive;
        Signal4 = quality.Bars >= 4 ? quality.Color : LatencyQuality.Inactive;
        var seconds = double.IsFinite(snapshot.ElapsedSeconds) ? Math.Clamp(snapshot.ElapsedSeconds, 0, TimeSpan.MaxValue.TotalSeconds - 1) : 0;
        var time = TimeSpan.FromSeconds(seconds);
        Elapsed = FormattableString.Invariant($"{(long)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}");
        for (var index = rows.Count - 1; index >= 0; index--)
            if (!snapshot.Rows.Any(r => r.Key == rows[index].Key)) rows.RemoveAt(index);
        for (var i = 0; i < snapshot.Rows.Count; i++)
        {
            var row = snapshot.Rows[i]; var found = rows.FirstOrDefault(r => r.Key == row.Key);
            if (found is null) rows.Insert(i, new(row, selfClassOverride));
            else { found.Apply(row, selfClassOverride); var oldIndex = rows.IndexOf(found); if (oldIndex != i) rows.Move(oldIndex, i); }
        }
    }
}
