using System.Globalization;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Common;

namespace PresApp.Domain.GateForms;

/// <summary>
/// Printable section acceptance gate form (Word): <see cref="Mechanical"/> (G1–G8, Equipment and Pipeline registers) or
/// <see cref="Electrical"/> (E1–E8, electrical motors of the E&amp;I register).
/// </summary>
public enum GateFormKind
{
    Mechanical = 1,
    Electrical = 2
}

/// <summary>
/// A Word template of a gate form uploaded for one plant. Every upload is kept as a version; at most one version per
/// plant and kind is active, and without an active one the app uses its built-in template.
/// </summary>
public class GateFormTemplate
{
    public int Id { get; set; }
    public GateFormKind Kind { get; set; }
    public int PlantId { get; set; }
    public Plant Plant { get; set; } = null!;
    public string FileName { get; set; } = "";
    public byte[] Content { get; set; } = [];
    public bool IsActive { get; set; }
    public DateTime UploadedAtUtc { get; set; }
    public string UploadedBy { get; set; } = "";
}

/// <summary>Starting values, taken from the client forms of Nov 2026. An admin changes them in Admin → Gate forms.</summary>
public static class GateFormDefaults
{
    /// <summary>Unit settings created once when the unit has none (an admin's later changes, emptying included, are kept).</summary>
    public static readonly (int UnitNumber, string? MechanicalSource, string? DefaultUnitId)[] Units =
    [
        (39, "DOD-52618-H2-39-69-00-156", null),
        (541, "DOD-52618-H5-541-69-00-156", "541"),
    ];
}

/// <summary>Values of the gate forms that belong to a unit (the source document reference changes per unit).</summary>
public class GateFormUnitSetting
{
    public int UnitId { get; set; }
    public Unit Unit { get; set; } = null!;

    /// <summary>Source document of the mechanical form, e.g. "DOD-52618-H2-39-69-00-156". Empty = not known yet.</summary>
    public string? MechanicalSource { get; set; }

    /// <summary>Source document of the electrical form. Empty = not known yet.</summary>
    public string? ElectricalSource { get; set; }

    /// <summary>Unit ID printed on forms that have that field (Habshan 5: "541") when the asset has none of its own.</summary>
    public string? DefaultUnitId { get; set; }
}

/// <summary>
/// The values typed for an asset's gate form, kept so the next print starts from them. Section, work pack and unit ID
/// are stored only when they differ from what the app fills by itself (tag, P&amp;ID number, the unit's Unit ID).
/// </summary>
public class GateFormEntry : AuditableEntity, IFieldAudited
{
    public const int TextMaxLength = 200;

    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public string? RecordNo { get; set; }
    public DateOnly? Date { get; set; }
    public string? Section { get; set; }
    public string? WorkPack { get; set; }
    public string? UnitIdText { get; set; }

    Guid? IFieldAudited.AuditAssetId => AssetId;
}

/// <summary>
/// Last Record No. number used per discipline code and unit, for Record Nos. like ITR-541-STAT-001. Each (code, unit) pair
/// counts independently. A number typed by hand above it moves it up, so the next automatic one follows it.
/// </summary>
public class RecordNoSequence
{
    public string Code { get; set; } = "";
    public int UnitNumber { get; set; }
    public int LastNumber { get; set; }
}

public static class RecordNoFormat
{
    public const string Start = "ITR";
    public const int CodeMaxLength = 10;

    /// <summary>Builds e.g. ITR-541-STAT-001: unit first, then the discipline code (unit and number padded to 3 digits, as in the Equipment IDs).</summary>
    public static string Build(string code, int unitNumber, int number) =>
        $"{Start}-{unitNumber:000}-{code.Trim().ToUpperInvariant()}-{number:000}";

    /// <summary>
    /// Reads ITR-UNIT-CODE-NUMBER in any case and padding; false for anything else. The first order used (ITR-CODE-UNIT-NUMBER,
    /// e.g. ITR-STAT-541-001) is still read, so a number typed that way is written again with the unit first.
    /// </summary>
    public static bool TryParse(string? value, out string code, out int unitNumber, out int number)
    {
        code = "";
        unitNumber = number = 0;
        var parts = value?.Trim().Split('-');
        if (parts is not { Length: 4 } || !string.Equals(parts[0], Start, StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0) return false;
        // Unit first (ITR-541-STAT-001); when both parts are digits, this is the order taken.
        if (IsCode(parts[2]) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out unitNumber))
            code = parts[2].ToUpperInvariant();
        else if (IsCode(parts[1]) && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out unitNumber))
            code = parts[1].ToUpperInvariant();
        else
        {
            unitNumber = number = 0;
            return false;
        }
        return true;
    }

    /// <summary>Letters and digits, 1 to <see cref="CodeMaxLength"/>.</summary>
    public static bool IsCode(string? code) =>
        code is { Length: > 0 and <= CodeMaxLength } && code.All(char.IsAsciiLetterOrDigit);
}
