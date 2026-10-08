namespace Aion2Meter.Presentation;

/// <summary>Deliberately small, portable hotkey grammar: modifiers and one letter/digit/F1..F11.</summary>
public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey, string Text)
{
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = null!;
        if (text is null || text.Length > 64) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            uint bit = part.ToUpperInvariant() switch { "ALT" => 1, "CTRL" => 2, "SHIFT" => 4, "WIN" => 8, _ => 0 };
            if (bit == 0 || (modifiers & bit) != 0) return false;
            modifiers |= bit;
        }
        // Shift alone would intercept normal typing.
        if ((modifiers & 11) == 0) return false;
        var key = parts[^1].ToUpperInvariant(); uint vk;
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') vk = key[0];
        else if (key.StartsWith('F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 11) vk = (uint)(111 + f);
        else return false;
        var names = new List<string>();
        if ((modifiers & 2) != 0) names.Add("Ctrl");
        if ((modifiers & 1) != 0) names.Add("Alt");
        if ((modifiers & 4) != 0) names.Add("Shift");
        if ((modifiers & 8) != 0) names.Add("Win");
        names.Add(key); gesture = new(modifiers, vk, string.Join('+', names)); return true;
    }
}
