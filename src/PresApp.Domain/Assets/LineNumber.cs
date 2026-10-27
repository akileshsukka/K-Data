using System.Globalization;
using System.Text.RegularExpressions;

namespace PresApp.Domain.Assets;

/// <summary>Parts of a line number such as <c>39-0218-LS-16"-61055-P</c> (unit - sequence - service - size - spec [- insulation, not stored]).</summary>
public sealed record LineNumberParts(int Unit, string Sequence, string Service, decimal SizeIn, string SizeText, string Spec);

/// <summary>
/// Reads size and pipe spec from the line number, as in the Simpro line list (184 lines of Unit 39).
/// The size is written in inches with a closing quote: 16", 3/4", 1-1/2", 1 1/2" or 2.5".
/// The insulation code at the end ("-P", "-S/FG" or empty) is accepted but not stored.
/// </summary>
public static partial class LineNumber
{
    public static LineNumberParts? Parse(string? lineNumber)
    {
        if (string.IsNullOrWhiteSpace(lineNumber)) return null;
        var m = Pattern().Match(lineNumber.Trim().Replace('”', '"').Replace('″', '"'));
        if (!m.Success || !TryParseSize(m.Groups["size"].Value, out var size)) return null;
        return new LineNumberParts(
            int.Parse(m.Groups["unit"].Value, CultureInfo.InvariantCulture),
            m.Groups["seq"].Value.ToUpperInvariant(),
            m.Groups["service"].Value.ToUpperInvariant(),
            size,
            m.Groups["size"].Value.Trim() + "\"",
            m.Groups["spec"].Value.ToUpperInvariant());
    }

    /// <summary>
    /// Identity of a line regardless of how its number is written: unit, sequence, service, size and spec.
    /// The insulation code at the end and the way the size is written (1-1/2" or 1.5") do not count.
    /// </summary>
    public static string IdentityKey(LineNumberParts p) =>
        string.Join('|', p.Unit, p.Sequence.TrimStart('0'), p.Service, p.SizeIn.ToString("0.###", CultureInfo.InvariantCulture), p.Spec);

    /// <summary>"3/4" → 0.75, "1-1/2" or "1 1/2" → 1.5, "16" → 16, "2.5" → 2.5.</summary>
    public static bool TryParseSize(string text, out decimal inches)
    {
        inches = 0;
        var t = text.Trim();
        var mixed = MixedFraction().Match(t);
        if (mixed.Success)
        {
            var whole = mixed.Groups["whole"].Success ? decimal.Parse(mixed.Groups["whole"].Value, CultureInfo.InvariantCulture) : 0;
            var den = decimal.Parse(mixed.Groups["den"].Value, CultureInfo.InvariantCulture);
            if (den == 0) return false;
            inches = whole + decimal.Parse(mixed.Groups["num"].Value, CultureInfo.InvariantCulture) / den;
            return inches > 0;
        }
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out inches) && inches > 0;
    }

    [GeneratedRegex("""^(?<unit>\d{1,4})-(?<seq>[0-9A-Za-z]+)-(?<service>[0-9A-Za-z]+)-(?<size>\d+(?:[.]\d+)?(?:[- ]\d+/\d+)?|\d+/\d+)\s*"\s*-(?<spec>[0-9A-Za-z]+)(?:-(?<ins>[0-9A-Za-z]*(?:/[0-9A-Za-z]+)?))?$""")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^(?:(?<whole>\d+)[- ])?(?<num>\d+)/(?<den>\d+)$")]
    private static partial Regex MixedFraction();
}
