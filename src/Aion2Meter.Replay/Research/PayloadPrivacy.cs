using System.Text;
using System.Text.RegularExpressions;

namespace Aion2Meter.Replay.Research;

public static partial class PayloadPrivacy
{
    // Conservative heuristic, not a guarantee that unknown binary formats contain no secrets.
    [GeneratedRegex(@"authorization|bearer|password|passwd|cookie|credential|api[_-]?key|access[_-]?token|refresh[_-]?token|secret|eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Sensitive();
    public static bool IsSensitive(byte[] payload) => Sensitive().IsMatch(Encoding.ASCII.GetString(payload));

    public static bool IsSensitive(ReassembledStream stream)
    {
        var tail = "";
        long end = -1;
        foreach (var chunk in stream.Chunks)
        {
            if (chunk.Offset != end) tail = "";
            var text = tail + Encoding.ASCII.GetString(chunk.Bytes);
            if (Sensitive().IsMatch(text)) return true;
            tail = text.Length > 1024 ? text[^1024..] : text;
            end = chunk.End;
        }
        return false;
    }
    public static string Ascii(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = bytes[i] is >= 32 and <= 126 ? (char)bytes[i] : '.';
        return new(chars);
    }
}
