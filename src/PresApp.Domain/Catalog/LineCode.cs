namespace PresApp.Domain.Catalog;

/// <summary>Which part of the line number a code belongs to.</summary>
public enum LineCodeKind
{
    /// <summary>Service designation (fluid), e.g. HCX = Hydrocarbon, sour.</summary>
    Service
}

/// <summary>Meaning of a code used in line numbers (client line numbering standard). Editable by administrators.</summary>
public class LineCode
{
    public int Id { get; set; }
    public LineCodeKind Kind { get; set; }
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;

    public const int CodeLength = 10;
    public const int DescriptionLength = 120;
}
