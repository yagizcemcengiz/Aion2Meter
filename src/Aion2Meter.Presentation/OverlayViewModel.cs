using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

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
    public string DisplayName { get => name; private set => Set(ref name, value); }
    public string Dps { get => dps; private set => Set(ref dps, value); }
    public string Damage { get => damage; private set => Set(ref damage, value); }
    public string Contribution { get => contribution; private set => Set(ref contribution, value); }
    public double ContributionValue { get => percent; private set => Set(ref percent, value); }
    public int Rank { get => rank; private set => Set(ref rank, value); }
    public bool IsSelf { get => self; private set => Set(ref self, value); }
    public OverlayRowViewModel(OverlayRow row) { Key = row.Key; Apply(row); }
    public void Apply(OverlayRow row)
    {
        if (row.Key != Key) throw new ArgumentException("A row belongs to one stable identity scope.");
        var culture = CultureInfo.InvariantCulture;
        DisplayName = row.DisplayName; Rank = row.Rank; IsSelf = row.IsSelf;
        Dps = row.Dps?.ToString("N1", culture) ?? "—"; Damage = row.TotalDamage.ToString("N0", culture);
        Contribution = row.ContributionPercent.ToString("0.#", culture) + "%";
        ContributionValue = (double)row.ContributionPercent;
    }
}

/// <summary>Owned by the UI thread; rows and visual controls survive ordinary ticks.</summary>
public sealed class OverlayViewModel : ObservableView
{
    private readonly ObservableCollection<OverlayRowViewModel> rows = [];
    private OverlaySnapshot? previous;
    private string status = "Waiting for character identity...", hint = "Will recover on the next fresh world connection.",
        elapsed = "00:00.0", coverage = "Coverage: Partial", details = "";
    public ReadOnlyObservableCollection<OverlayRowViewModel> Rows { get; }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Hint { get => hint; private set => Set(ref hint, value); }
    public string Elapsed { get => elapsed; private set => Set(ref elapsed, value); }
    public string Coverage { get => coverage; private set => Set(ref coverage, value); }
    public string CoverageDetails { get => details; private set => Set(ref details, value); }
    public OverlayViewModel() { Rows = new(rows); Apply(OverlaySnapshot.Waiting); }
    public void Apply(OverlaySnapshot snapshot)
    {
        if (ReferenceEquals(previous, snapshot)) return;
        previous = snapshot;
        Status = snapshot.Status; Hint = snapshot.Hint; Coverage = snapshot.Coverage; CoverageDetails = snapshot.CoverageDetails;
        var seconds = double.IsFinite(snapshot.ElapsedSeconds) ? Math.Clamp(snapshot.ElapsedSeconds, 0, TimeSpan.MaxValue.TotalSeconds - 1) : 0;
        var time = TimeSpan.FromSeconds(seconds);
        Elapsed = FormattableString.Invariant($"{(long)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}");
        for (var index = rows.Count - 1; index >= 0; index--)
            if (!snapshot.Rows.Any(r => r.Key == rows[index].Key)) rows.RemoveAt(index);
        for (var i = 0; i < snapshot.Rows.Count; i++)
        {
            var row = snapshot.Rows[i]; var found = rows.FirstOrDefault(r => r.Key == row.Key);
            if (found is null) rows.Insert(i, new(row));
            else { found.Apply(row); var oldIndex = rows.IndexOf(found); if (oldIndex != i) rows.Move(oldIndex, i); }
        }
    }
}
