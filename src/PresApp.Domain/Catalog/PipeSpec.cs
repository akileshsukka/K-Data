namespace PresApp.Domain.Catalog;

/// <summary>
/// Piping specification (pipe class) of the client, e.g. 61055. The lines take their rating, flange face and gasket
/// from here through the spec in their line number, so changing a spec updates every line that uses it.
/// </summary>
public class PipeSpec
{
    public int Id { get; set; }
    public string Code { get; set; } = "";

    /// <summary>Pressure rating, e.g. "150#", "600#".</summary>
    public string? PressureClass { get; set; }

    /// <summary>Flange face: RF, RTJ or FF (the RFD / RFC of the pipe class summary are kept as RF; the gasket tells them apart).</summary>
    public string? FlangeType { get; set; }

    /// <summary>Gasket type and material exactly as in the pipe class summary, e.g. "Graphite", "Spiral Wound", "Soft Iron", "AISI 316L".</summary>
    public string? Gasket { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public const int CodeLength = 20;
    public const int ClassLength = 30;
    public const int FlangeLength = 50;
    public const int GasketLength = 100;
    public const int DescriptionLength = 200;
}
