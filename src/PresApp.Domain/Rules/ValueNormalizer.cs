using System.Globalization;
using System.Text.RegularExpressions;

namespace PresApp.Domain.Rules;

/// <summary>
/// Normalisation rules shared by the app, the Excel import and the migration (handover 8.2,
/// plus the 24-Oct-2026 rule: N/A is written "Not Applicable").
/// </summary>
public static partial class ValueNormalizer
{
    public const string NotApplicable = "Not Applicable";

    private static readonly HashSet<string> NotApplicableVariants = new(StringComparer.OrdinalIgnoreCase)
    {
        "N/A", "NA", "N.A", "N.A.", "N / A", "Not Applicable", "Not Applicalbe", "Not Aplicable", "Not Appliable"
    };

    private static readonly Dictionary<string, string> GateStatusAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Complete"] = "Completed",
        ["DONE"] = "Completed",
        ["Completed - Documentation Pending"] = "Completed",
        ["HOLD"] = "Hold",
        ["On-going"] = "Ongoing",
        ["On going"] = "Ongoing",
        ["Not Started"] = "Ongoing",
        ["Not Verified"] = "Ongoing",
        ["Open"] = "Ongoing",
    };

    private static readonly Dictionary<string, string> CommonAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Complete"] = "Completed",
        ["DONE"] = "Completed",
        ["On-going"] = "Ongoing",
        ["On going"] = "Ongoing",
    };

    /// <summary>Trim, one space between words, line breaks kept, case kept. Empty → null.</summary>
    public static string? CleanText(string? value)
    {
        if (value is null) return null;
        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => SpacesRegex().Replace(l, " ").Trim());
        var text = string.Join("\n", lines).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Key used to compare list values: case and spaces ignored.</summary>
    public static string ListKey(string value) => SpacesRegex().Replace(value, "").ToLowerInvariant();

    public static bool IsNotApplicable(string value) => NotApplicableVariants.Contains(value.Trim());

    /// <summary>
    /// Maps a raw list value to the canonical option spelling. Returns the cleaned raw value and
    /// <c>matched = false</c> when no option fits (kept and flagged, never dropped).
    /// </summary>
    public static (string? Value, bool Matched) Canonical(string? raw, IEnumerable<string> options, bool isGateStatus = false)
    {
        var value = CleanText(raw);
        if (value is null) return (null, true);
        if (IsNotApplicable(value)) value = NotApplicable;
        if (isGateStatus && GateStatusAliases.TryGetValue(value, out var alias)) value = alias;

        var key = ListKey(value);
        var list = options as IReadOnlyCollection<string> ?? options.ToList();
        var match = list.FirstOrDefault(o => ListKey(o) == key);
        // Spelling variants valid in any list that has the target value (e.g. EM Walkdown "Complete").
        if (match is null && CommonAliases.TryGetValue(value, out var common)) match = list.FirstOrDefault(o => ListKey(o) == ListKey(common));
        return match is null ? (value, false) : (match, true);
    }

    public static bool TryParseDecimal(string? raw, out decimal value) =>
        decimal.TryParse(raw?.Trim().Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex SpacesRegex();
}
