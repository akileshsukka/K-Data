using System.Globalization;
using System.Text.RegularExpressions;

namespace PresApp.Domain.Assets;

/// <summary>Parts of a line number such as <c>39-0218-LS-16"-61055-P</c> (unit - sequence - service - size - spec [- insulation, not stored]).</summary>
public sealed record LineNumberParts(int Unit, string Sequence, string Service, decimal SizeIn, string SizeText, string Spec);

/// <summary>
/// What could be read from a line number, standard or not (<see cref="LineNumber.Read"/>).
/// <see cref="Parts"/> is set only for a standard number; <see cref="Problem"/> says why a number is not standard.
/// </summary>
public sealed record LineNumberReading(
    int? Unit, string? Sequence, string? Service, decimal? SizeIn, string? SizeText, string? Spec,
    LineNumberParts? Parts, string? Problem, IReadOnlyList<string> Missing, IReadOnlyList<string> Ignored)
{
    public static readonly LineNumberReading Empty = new(null, null, null, null, null, null, null, "Enter the line number.", ["unit", "sequence", "service", "size", "pipe spec"], []);

    public bool IsStandard => Parts is not null;

    /// <summary>Short note for a non-standard number: what could not be read and what was ignored. Null when standard.</summary>
    public string? Note
    {
        get
        {
            if (IsStandard) return null;
            var text = string.Join(" · ", new[]
            {
                Missing.Count > 0 ? "Not read: " + string.Join(", ", Missing) : null,
                Ignored.Count > 0 ? "Ignored: " + string.Join(", ", Ignored) : null
            }.Where(t => t is not null));
            if (text.Length == 0) text = "Not in the standard format";
            return text.Length > PipelineDetail.NoteLength ? text[..PipelineDetail.NoteLength] : text;
        }
    }
}

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
        // One line written with two numbers in the same cell (a size change, e.g. 16" and 12"): the first one gives size and spec.
        if (lineNumber.Contains('\n')) lineNumber = First(lineNumber);
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

    /// <summary>
    /// Key of the size group of a line: unit, sequence, service, spec and insulation, without the size. Sections of one line
    /// that change size (541-2089-RF-2"-31376-C, 541-2089-RF-4"-31376-C…) share it, so the grouped view and the grouped line
    /// list show them as one row. Only a single standard number has a key; any other line stays on its own (null).
    /// Nothing is stored: the groups are worked out each time from the line numbers.
    /// </summary>
    public static string? GroupKey(string? lineNumber)
    {
        if (string.IsNullOrWhiteSpace(lineNumber) || lineNumber.Contains('\n')) return null;
        var m = Pattern().Match(lineNumber.Trim().Replace('”', '"').Replace('″', '"'));
        if (!m.Success) return null;
        return string.Join('|', int.Parse(m.Groups["unit"].Value, CultureInfo.InvariantCulture), m.Groups["seq"].Value.TrimStart('0').ToUpperInvariant(),
            m.Groups["service"].Value.ToUpperInvariant(), m.Groups["spec"].Value.ToUpperInvariant(), m.Groups["ins"].Value.ToUpperInvariant());
    }

    /// <summary>First non-empty number of a cell that holds several ("39-0061-RSX-16"-13030-H⏎39-0061-RSX-12"-13030-H").</summary>
    public static string First(string lineNumber) =>
        lineNumber.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

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

    /// <summary>
    /// Reads a line number that may not follow the standard (lines of demolished plants, trimmed numbers).
    /// A standard number (<see cref="Parse"/> succeeds and the part after the size is not an insulation code) is read as always.
    /// Otherwise only what can be told apart for sure is kept, never guessed:
    /// <list type="bullet">
    /// <item>unit and sequence: first and second part;</item>
    /// <item>size: the part with the inch mark (");</item>
    /// <item>service: third part when nothing else stands between it and the size (or no size), or when it is a known service code;</item>
    /// <item>pipe spec: a part after the size that is in the pipe spec list. Insulation codes are dropped; anything else is ignored and reported.</item>
    /// </list>
    /// </summary>
    public static LineNumberReading Read(string? lineNumber, Func<string, bool> isSpec, Func<string, bool> isInsulation, Func<string, bool> isService)
    {
        if (string.IsNullOrWhiteSpace(lineNumber)) return LineNumberReading.Empty;
        var text = (lineNumber.Contains('\n') ? First(lineNumber) : lineNumber).Trim().Replace('”', '"').Replace('″', '"').Replace('“', '"');

        var strict = Parse(text);
        if (strict is not null && !isInsulation(strict.Spec))
            return new LineNumberReading(strict.Unit, strict.Sequence, strict.Service, strict.SizeIn, strict.SizeText, strict.Spec, strict, null, [], []);

        string[] head, tail;
        decimal? size = null;
        string? sizeText = null;
        var anchor = SizeAnchor().Match(text);
        if (anchor.Success && TryParseSize(anchor.Groups["size"].Value, out var inches))
        {
            size = inches;
            sizeText = anchor.Groups["size"].Value.Trim() + "\"";
            head = Tokens(anchor.Groups["head"].Value);
            tail = Tokens(anchor.Groups["tail"].Value);
        }
        else
        {
            var all = Tokens(text);
            head = all.Take(3).ToArray();
            tail = all.Skip(3).ToArray();
        }

        int? unit = head.Length > 0 && UnitPart().IsMatch(head[0]) ? int.Parse(head[0], CultureInfo.InvariantCulture) : null;
        var sequence = head.Length > 1 && CodePart().IsMatch(head[1]) ? head[1].ToUpperInvariant() : null;
        string? service = null;
        var ignored = new List<string>();
        if (head.Length > 2 && CodePart().IsMatch(head[2]) && (head.Length == 3 || isService(head[2].ToUpperInvariant())))
            service = head[2].ToUpperInvariant();
        foreach (var extra in head.Skip(service is null ? 2 : 3)) ignored.Add(extra);
        if (unit is null && head.Length > 0) ignored.Insert(0, head[0]);

        string? spec = null;
        foreach (var part in tail)
        {
            var code = part.ToUpperInvariant();
            if (spec is null && isSpec(code)) spec = code;
            else if (!isInsulation(code)) ignored.Add(part);
        }

        var missing = new List<string>();
        if (unit is null) missing.Add("unit");
        if (sequence is null) missing.Add("sequence");
        if (service is null) missing.Add("service");
        if (size is null) missing.Add("size");
        if (spec is null) missing.Add("pipe spec");
        var problem = strict is not null
            ? $"After the size comes {strict.Spec}, an insulation code: this line number has no pipe spec."
            : "Not a line number like 39-0218-LS-16\"-61055-P (size and spec cannot be read).";
        return new LineNumberReading(unit, sequence, service, size, sizeText, spec, null, problem, missing, ignored);
    }

    /// <summary>
    /// Two numbers may be the same line when unit and sequence are equal and nothing else read from both contradicts
    /// (service, size and spec are compared only when both numbers have them). Used when one of them is not standard.
    /// </summary>
    public static bool MayBeSameLine(LineNumberReading a, LineNumberReading b)
    {
        if (a.Unit is null || b.Unit is null || a.Sequence is null || b.Sequence is null) return false;
        if (a.Unit != b.Unit || !string.Equals(a.Sequence.TrimStart('0'), b.Sequence.TrimStart('0'), StringComparison.OrdinalIgnoreCase)) return false;
        static bool Agree<T>(T? x, T? y) => x is null || y is null || EqualityComparer<T>.Default.Equals(x, y);
        return Agree(a.Service, b.Service) && Agree(a.SizeIn, b.SizeIn) && Agree(a.Spec, b.Spec);
    }

    private static string[] Tokens(string text) =>
        text.Split('-').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();

    [GeneratedRegex("""^(?<unit>\d{1,4})-(?<seq>[0-9A-Za-z]+)-(?<service>[0-9A-Za-z]+)-(?<size>\d+(?:[.]\d+)?(?:[- ]\d+/\d+)?|\d+/\d+)\s*"\s*-(?<spec>[0-9A-Za-z]+)(?:-(?<ins>[0-9A-Za-z]*(?:/[0-9A-Za-z]+)?))?$""")]
    private static partial Regex Pattern();

    /// <summary>The size is the first part written with an inch mark; what comes before and after it is split on "-".</summary>
    [GeneratedRegex("""^(?<head>.*?)-\s*(?<size>\d+(?:[.]\d+)?(?:[- ]\d+/\d+)?|\d+/\d+)\s*"(?<tail>.*)$""")]
    private static partial Regex SizeAnchor();

    [GeneratedRegex(@"^\d{1,4}$")]
    private static partial Regex UnitPart();

    [GeneratedRegex("^[0-9A-Za-z]+$")]
    private static partial Regex CodePart();

    [GeneratedRegex(@"^(?:(?<whole>\d+)[- ])?(?<num>\d+)/(?<den>\d+)$")]
    private static partial Regex MixedFraction();
}
