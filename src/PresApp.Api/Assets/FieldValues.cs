using System.Globalization;
using PresApp.Domain.Assets;
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
