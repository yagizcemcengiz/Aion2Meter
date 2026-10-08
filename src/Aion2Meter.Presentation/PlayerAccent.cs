namespace Aion2Meter.Presentation;

public static class PlayerAccent
{
    private static readonly string[] Palette = ["#A694F4", "#69B9F1", "#F1BA70", "#EF8EAF", "#F19B72", "#CBA9E9", "#D1CD82"];
    // A stable UI color, not class or gameplay identity. Avoid process-randomized string.GetHashCode.
    public static string For(string name, bool self)
    {
        if (self) return "#71E1C1";
        uint hash = 2166136261;
        foreach (var c in name) hash = unchecked((hash ^ c) * 16777619);
        return Palette[hash % (uint)Palette.Length];
    }
}
