using System.Globalization;
using System.Text;

namespace Aion2Meter.Replay.Research;

/// <summary>Only owns terminal lines written after command startup. Redirected output is plain text.</summary>
public sealed class LiveMeterDashboard(TextWriter output, bool interactive, bool verbose = false,
    bool cursorInitiallyVisible = true) : IDisposable
{
    private int previousLines;
    private bool disposed;

    public static string[] Lines(LiveMeterSnapshot meter, string performance, bool verbose)
    {
        var c = CultureInfo.InvariantCulture;
        var elapsed = TimeSpan.FromSeconds(meter.EncounterElapsedSeconds);
        var clean = meter.Gaps == 0 && meter.Conflicts == 0 && !meter.Status.StartsWith("UNTRUSTED", StringComparison.Ordinal);
        var lines = new List<string>
        {
            "Aion2Meter LIVE | Ctrl+C stops",
            $"Character : {Clean(meter.CharacterName ?? "Unknown")}",
            $"Binding   : {meter.BindingStatus} | EntityId: {meter.EntityId?.ToString(c) ?? "Unknown"}",
            $"Status    : {meter.Status}",
            $"Encounter : {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100} (first to last Self hit)",
            $"Damage    : {meter.TotalDamage.ToString("N0", c)} | DPS: {meter.Dps?.ToString("N1", c) ?? "N/A"}",
            $"Self Hits : {meter.EncounterSelfHits} encounter / {meter.SelfCount} epoch",
            $"Other     : {meter.OtherCount} | Unknown: {meter.UnknownCount} (epoch)",
            $"Recent Self: {(meter.RecentSelfAmounts.Count == 0 ? "-" : string.Join(", ", meter.RecentSelfAmounts))}",
            $"Coverage  : {meter.Coverage}",
            $"Transport : {(clean ? "clean" : "pending/fault")} | Gap {meter.Gaps} | Conflict {meter.Conflicts} | Duplicate {meter.DuplicateSegments}",
            $"Overlap   : {meter.OverlapBytes} bytes | Unsupported combat: {meter.UnsupportedCandidates}"
        };
        if (verbose) { lines.Add(performance); lines.AddRange(meter.Warnings.Take(8).Select(w => "  " + Clean(w))); }
        return lines.ToArray();
    }

    public void Render(LiveMeterSnapshot meter, string performance)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var lines = Lines(meter, performance, verbose);
        if (!interactive)
        {
            output.WriteLine(string.Join(" | ", lines)); output.Flush(); return;
        }
        var update = new StringBuilder();
        if (previousLines == 0) update.Append("\u001b[?25l");
        else update.Append($"\u001b[{previousLines}A");
        var count = Math.Max(previousLines, lines.Length);
        for (var i = 0; i < count; i++)
        {
            update.Append("\r\u001b[2K");
            // One physical line per row, even when the terminal is resized to a narrow width.
            var width = 100;
            try { if (!Console.IsOutputRedirected) width = Math.Max(1, Console.WindowWidth - 1); }
            catch (IOException) { }
            var line = i < lines.Length ? lines[i] : "";
            update.AppendLine(Fit(line, width));
        }
        output.Write(update); previousLines = count; output.Flush();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (interactive && previousLines != 0) { output.Write(cursorInitiallyVisible ? "\u001b[?25h" : "\u001b[?25l"); output.Flush(); }
    }

    private static string Fit(string text, int width)
    {
        var result = new StringBuilder(); var occupied = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            // Conservative bound for non-Latin glyphs: avoid wide names wrapping into owned rows.
            var cells = rune.Value <= 0xff ? 1 : 2;
            if (occupied + cells > width) break;
            result.Append(rune); occupied += cells;
        }
        return result.ToString();
    }

    private static string Clean(string text) => string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
}
