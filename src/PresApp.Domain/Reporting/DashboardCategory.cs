using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;

namespace PresApp.Domain.Reporting;

public enum CategorySection
{
    Hold = 0,
    NotApplicable = 1
}

/// <summary>
/// Which dashboard a category belongs to. Mechanical (static, rotating, package) and Electrical &amp; Instrumentation
/// items each have their own catalogue: a category of one never applies to the other ("Motor removed" is no use for a vessel).
/// </summary>
public enum CategoryScope
{
    Mechanical = 0,
    ElectricalInstrument = 1
}

public static class CategoryScopes
{
    /// <summary>Code prefix of each catalogue: Cat1, Cat2… for mechanical equipment, CatE1, CatE2… for E&amp;I items.</summary>
    public static string CodePrefix(CategoryScope scope) => scope == CategoryScope.ElectricalInstrument ? "CatE" : "Cat";

    /// <summary>Catalogue of an asset type. Pipelines have no dashboard category (null).</summary>
    public static CategoryScope? Of(AssetType type) => type switch
    {
        AssetType.Equipment => CategoryScope.Mechanical,
        AssetType.ElectricalInstrument => CategoryScope.ElectricalInstrument,
        _ => null
    };

    /// <summary>"ei", "e&amp;i", "ElectricalInstrument" → E&amp;I; anything else (empty included) → Mechanical.</summary>
    public static CategoryScope Parse(string? text) =>
        (text ?? "").Trim().ToLowerInvariant() is "ei" or "e&i" or "electricalinstrument" or "electrical" ? CategoryScope.ElectricalInstrument : CategoryScope.Mechanical;

    public static string Key(CategoryScope scope) => scope == CategoryScope.ElectricalInstrument ? "ei" : "mechanical";
}

/// <summary>
/// Dashboard category of one unit (e.g. "Cat 3: DNV inspection is ongoing").
/// Template v4: the engineer picks the category of each asset by its <see cref="Code"/>
/// (<see cref="AssetPreservation.DashboardCategoryId"/>). Template v3 (still supported until the export changes):
/// an asset falls in the category whose keyword appears in its Preservation Remarks; the lowest
/// <see cref="Priority"/> wins when several match.
/// </summary>
public class DashboardCategory
{
    public const int CodeMaxLength = 10;
    public const int ShortText = 200;
    public const int LongText = 1000;

    public int Id { get; set; }
    public int UnitId { get; set; }
    public Unit Unit { get; set; } = null!;
    public CategorySection Section { get; set; }

    /// <summary>Dashboard of the category: mechanical equipment or E&amp;I items. Fixed once the category is created.</summary>
    public CategoryScope Scope { get; set; }

    public string Label { get; set; } = "";

    /// <summary>
    /// "Cat1", "Cat2"… (mechanical) or "CatE1", "CatE2"… (E&amp;I), unique per unit and section, never renumbered nor reused (the Excel stores it).
    /// Null only for rows created before template v4, until they get one.
    /// </summary>
    public string? Code { get; set; }

    public int Priority { get; set; }
    public int SortOrder { get; set; }

    // Dashboard note columns, kept here so they no longer need to be copied from file to file.
    public string? ActionBy { get; set; }
    public string? EmailAction { get; set; }

    /// <summary>"EnerMech Recommendation" column of the dashboard.</summary>
    public string? Recommendation { get; set; }
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
