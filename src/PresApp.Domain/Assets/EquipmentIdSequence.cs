namespace PresApp.Domain.Assets;

/// <summary>
/// Last number used per discipline prefix and unit, for IDs like ST-EMP-039-001.
/// Each (prefix, unit) pair counts independently.
/// </summary>
public class EquipmentIdSequence
{
    public string Prefix { get; set; } = "";
    public int UnitNumber { get; set; }
    public int LastNumber { get; set; }
}

public static class EquipmentIdFormat
{
    public const string Middle = "EMP";

    /// <summary>Builds e.g. ST-EMP-039-001 (unit and number padded to 3 digits).</summary>
    public static string Build(string prefix, int unitNumber, int number) =>
        $"{prefix.Trim().ToUpperInvariant()}-{Middle}-{unitNumber:000}-{number:000}";
}
