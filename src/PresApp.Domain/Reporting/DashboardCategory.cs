using PresApp.Domain.Catalog;

namespace PresApp.Domain.Reporting;

public enum CategorySection
{
    Hold = 0,
    NotApplicable = 1
}

/// <summary>
/// Dashboard category of one unit (e.g. "Cat 3: DNV inspection is ongoing").
/// An asset falls in the category whose keyword appears in its Preservation Remarks;
/// the lowest <see cref="Priority"/> wins when several match.
/// </summary>
public class DashboardCategory
{
    public int Id { get; set; }
    public int UnitId { get; set; }
    public Unit Unit { get; set; } = null!;
    public CategorySection Section { get; set; }
    public string Label { get; set; } = "";
    public int Priority { get; set; }
    public int SortOrder { get; set; }
    public string? ActionBy { get; set; }
    public string? EmailAction { get; set; }
    public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
    public List<CategoryKeyword> Keywords { get; set; } = [];
}

public class CategoryKeyword
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public DashboardCategory Category { get; set; } = null!;
    public string Keyword { get; set; } = "";
}

/// <summary>Per-unit settings of the client Excel (title, extra columns).</summary>
public class UnitReportConfig
{
    public int UnitId { get; set; }
    public Unit Unit { get; set; } = null!;

    /// <summary>Title row, may contain {UNIT} and {DATE}.</summary>
    public string? Title { get; set; }

    /// <summary>Label after the unit number, e.g. "(HC)" or "(H-5)".</summary>
    public string? UnitLabel { get; set; }

    /// <summary>JSON list of extra not-stored columns, e.g. ["WALK DOWN DATE"].</summary>
    public string? ExtraColumnsJson { get; set; }
}
