namespace PresApp.Domain.Catalog;

/// <summary>Which part of the line number a code belongs to.</summary>
public enum LineCodeKind
{
    /// <summary>Service designation (fluid), e.g. HCX = Hydrocarbon, sour.</summary>
    Service,

    /// <summary>Insulation code, e.g. IC. Never stored on the line: it is recognised only so it is never taken for a pipe spec.</summary>
    Insulation
}

/// <summary>
/// Meaning of a code used in line numbers (client line numbering standard). Editable by administrators.
/// A code has a general meaning (no unit) and may have exceptions: a row of the same code for one unit, whose meaning
/// replaces the general one for the lines of that unit (AD = Area drain in general, Sewage in Unit 41).
/// </summary>
public class LineCode
{
    public int Id { get; set; }
    public LineCodeKind Kind { get; set; }
    public string Code { get; set; } = "";

    /// <summary>Null for the general meaning; a unit for an exception that applies only to the lines of that unit.</summary>
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;

    public const int CodeLength = 10;
    public const int DescriptionLength = 120;
}
