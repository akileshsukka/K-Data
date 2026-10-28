using System.Globalization;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Rules;

namespace PresApp.Api.Assets;

/// <summary>Reads and writes registry fields as strings (the API's wire format).</summary>
public static class FieldValues
{
    public static string CurrentGateText(CurrentGate gate) => gate switch
    {
        CurrentGate.New => "New",
        CurrentGate.RemovedFromSite => "Removed from Site",
        _ => gate.ToString()
    };

    public static bool TryParseCurrentGate(string? text, out CurrentGate gate)
    {
        gate = CurrentGate.New;
        var key = ValueNormalizer.ListKey(text ?? "");
        if (key is "new" or "") return true;
        if (key == "removedfromsite") { gate = CurrentGate.RemovedFromSite; return true; }
        if (key.Length == 2 && key[0] == 'g' && key[1] is >= '1' and <= '8') { gate = (CurrentGate)(key[1] - '0'); return true; }
        return false;
    }

    public static string? Read(FieldDef field, Asset asset)
    {
        object? owner = field.Owner switch { FieldOwner.Asset => asset, FieldOwner.Pipeline => asset.Pipeline, _ => asset.Preservation };
        if (owner is null) return null;
        return Format(field.Property.GetValue(owner));
    }

    public static string? Format(object? value) => value switch
    {
        null => null,
        string s => s,
        CurrentGate g => CurrentGateText(g),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.############", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    /// <summary>Same value for the user? Lists ignore case and spaces; text ignores extra spaces.</summary>
    public static bool Same(FieldDef field, string? a, string? b)
    {
        var x = ValueNormalizer.CleanText(a);
        var y = ValueNormalizer.CleanText(b);
        if (x is null || y is null) return x is null && y is null;
        if (field.Kind == FieldKind.Number && ValueNormalizer.TryParseDecimal(x, out var dx) && ValueNormalizer.TryParseDecimal(y, out var dy)) return dx == dy;
        return field.Kind == FieldKind.Choice ? ValueNormalizer.ListKey(x) == ValueNormalizer.ListKey(y) : x == y;
    }

    /// <summary>
    /// Other spellings accepted for a list (per list key). The piping line list writes "Yes" in "Bulk liquid drained":
    /// it is stored as Completed (and written back as "Yes" in the piping Excel).
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> ListAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [OptionSetKeys.PipingBulkLiquidDrained] = new(StringComparer.OrdinalIgnoreCase) { ["yes"] = AssetPreservation.Completed, ["y"] = AssetPreservation.Completed },
    };

    /// <summary>
    /// Validates and converts a value for an asset of the given type: the list of that type is used
    /// (lines have their own Current Activity and Bulk liquid drained lists), and fields that allow several
    /// values take one per line. Returns an error message or null.
    /// </summary>
    public static string? Prepare(FieldDef field, AssetType type, string? raw, IReadOnlyDictionary<string, List<string>> allOptions, out object? value)
    {
        var setKey = field.OptionSetFor(type);
        IReadOnlyList<string> options = setKey is null ? [] : allOptions.GetValueOrDefault(setKey) ?? [];
        if (field.Kind != FieldKind.Choice || setKey is null) return Prepare(field, raw, options, out value);

        value = null;
        var text = ValueNormalizer.CleanText(raw);
        if (text is null) return null;
        var parts = field.MultipleFor(type) ? text.Split('\n') : [text];
        var result = new List<string>();
        foreach (var part in parts.Select(ValueNormalizer.CleanText).OfType<string>())
        {
            var item = ListAliases.GetValueOrDefault(setKey)?.GetValueOrDefault(part.Trim()) ?? part;
            var (canonical, matched) = ValueNormalizer.Canonical(item, options, PreservationFields.IsGateStatus(field));
            // "debris inside; jetting required." = "Debris inside; jetting required": a final full stop does not count.
            if (!matched) (canonical, matched) = ValueNormalizer.Canonical(item.TrimEnd('.', ' '), options, PreservationFields.IsGateStatus(field));
            if (!matched)
                return $"\"{part}\" is not an option of {field.Label}{(type == AssetType.Pipeline && field.PipelineOptionSet is not null ? " for lines" : "")}.";
            if (!result.Contains(canonical!, StringComparer.OrdinalIgnoreCase)) result.Add(canonical!);
        }
        if (result.Count == 0) return null;
        var joined = string.Join("\n", result);
        if (joined.Length > field.MaxLength) return $"{field.Label} allows at most {field.MaxLength} characters.";
        value = joined;
        return null;
    }

    /// <summary>Validates and converts a new value. Returns an error message or null.</summary>
    public static string? Prepare(FieldDef field, string? raw, IReadOnlyList<string> activeOptions, out object? value)
    {
        value = null;
        var text = ValueNormalizer.CleanText(raw);
        if (text is null) return null;

        switch (field.Kind)
        {
            case FieldKind.Choice:
                var (canonical, matched) = ValueNormalizer.Canonical(text, activeOptions, PreservationFields.IsGateStatus(field));
                if (!matched) return $"\"{text}\" is not an option of {field.Label}.";
                value = canonical;
                return null;
            case FieldKind.Text:
            case FieldKind.LongText:
                if (text.Length > field.MaxLength) return $"{field.Label} allows at most {field.MaxLength} characters.";
                value = text;
                return null;
            case FieldKind.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return $"{field.Label} must be a date (yyyy-MM-dd).";
                value = d;
                return null;
            case FieldKind.Number:
                if (!ValueNormalizer.TryParseDecimal(text, out var n)) return $"{field.Label} must be a number.";
                if (field.Min is { } min && n < min) return $"{field.Label} cannot be less than {min}.";
                value = n;
                return null;
            default:
                return $"{field.Label} cannot be edited.";
        }
    }

    public static void Write(FieldDef field, Asset asset, object? value)
    {
        object owner = field.Owner switch
        {
            FieldOwner.Asset => asset,
            FieldOwner.Pipeline => asset.Pipeline ??= new PipelineDetail { AssetId = asset.Id },
            _ => asset.Preservation!
        };
        field.Property.SetValue(owner, value);
    }
}
