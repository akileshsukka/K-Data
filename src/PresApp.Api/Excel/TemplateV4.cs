using ClosedXML.Excel;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;

namespace PresApp.Api.Excel;

/// <summary>Category of a v4 template's Categories table, as read at upload.</summary>
public sealed record TemplateCategory(CategorySection Section, string Code, string Description, string? ActionBy,
    string? EmailAction, string? Recommendation, string? Note, int Order);

/// <summary>
/// Template v4: the engineer chooses each equipment's category by code in the "Hold Category" / "N/A Category"
/// columns, and every dashboard figure is a formula over the MasterList and Categories tables.
/// <list type="bullet">
/// <item><c>Categories</c> table (hidden sheet): Section, Code, Description, Action By, Email Action,
/// EnerMech Recommendation, Note. The app rewrites its rows from the unit's active categories.</item>
/// <item>Named ranges <c>KD_HoldSlots</c> / <c>KD_NASlots</c>: dashboard rows of each section, already holding the
/// formulas. The app shows one row per active category and hides the rest.</item>
/// </list>
/// The app never writes dashboard formulas for v4: the template owns them.
/// </summary>
public static class TemplateV4
{
    public const int FormatVersion = 4;
    public const string CategoryField = "DashboardCategory";

    private static readonly string[] Required = ["section", "code", "description"];

    /// <summary>A v4 template or file: its Categories table has a Code column.</summary>
    public static bool IsV4(XLWorkbook wb) => CategoryHeaders(wb) is { } h && h.Contains("code");

    public static string? Problem(XLWorkbook wb)
    {
        var headers = CategoryHeaders(wb) ?? [];
        var missing = Required.Where(r => !headers.Contains(r)).ToList();
        if (missing.Count > 0) return $"The Categories table needs the columns Section, Code and Description (missing: {string.Join(", ", missing)}).";
        var master = WorkbookFormat.FindTable(wb, ExcelColumns.MasterTable)!;
        var masterHeaders = master.HeadersRow().Cells().Select(c => ExcelColumns.Norm(WorkbookFormat.CellText(c))).ToList();
        if (!masterHeaders.Contains("holdcategory") || !masterHeaders.Contains("nacategory"))
            return "The MasterList table needs the columns \"Hold Category\" and \"N/A Category\".";
        foreach (var name in new[] { ExcelColumns.HoldSlotsName, ExcelColumns.NaSlotsName })
        {
            if (Slots(wb, name) is null) return $"The template has no named range {name} (dashboard rows of the categories).";
        }
        return null;
    }

    /// <summary>Categories listed in the template's Categories table (rows without a code are skipped).</summary>
    public static List<TemplateCategory> ReadCategories(XLWorkbook wb)
    {
        var table = WorkbookFormat.FindTable(wb, ExcelColumns.CategoriesTable);
        if (table?.DataRange is null) return [];
        var headers = table.HeadersRow().Cells().Select(c => ExcelColumns.Norm(WorkbookFormat.CellText(c))).ToList();
        string? Get(IXLTableRow row, params string[] names)
        {
            var i = headers.FindIndex(names.Contains);
            return i < 0 ? null : ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(i + 1)));
        }

        var result = new List<TemplateCategory>();
        var order = 0;
        foreach (var row in table.DataRange.Rows())
        {
            var code = Reporting.UnitDashboard.NormalizeCode(Get(row, "code"));
            var description = Get(row, "description");
            if (code is null || description is null) continue;
            var section = ParseSection(Get(row, "section"));
            if (section is null || result.Any(c => c.Section == section && c.Code == code)) continue;
            result.Add(new TemplateCategory(section.Value, code, Cut(description, DashboardCategory.ShortText)!,
                Cut(Get(row, "actionby"), DashboardCategory.ShortText), Cut(Get(row, "emailaction"), DashboardCategory.ShortText),
                Cut(Get(row, "enermechrecommendation", "recommendation"), DashboardCategory.LongText), Cut(Get(row, "note", "notes"), DashboardCategory.LongText),
                ++order));
        }
        return result;
    }

    /// <summary>Rewrites the Categories table with the unit's active categories (Hold first, then N/A).</summary>
    public static void WriteCategories(XLWorkbook wb, IReadOnlyList<DashboardCategory> categories)
    {
        var table = WorkbookFormat.FindTable(wb, ExcelColumns.CategoriesTable)!;
        var headers = table.HeadersRow().Cells().Select(c => ExcelColumns.Norm(WorkbookFormat.CellText(c))).ToList();
        int Col(params string[] names) => headers.FindIndex(names.Contains) + 1;
        int section = Col("section"), code = Col("code"), description = Col("description"), actionBy = Col("actionby"),
            email = Col("emailaction"), recommendation = Col("enermechrecommendation", "recommendation"), note = Col("note", "notes");

        var rows = Ordered(categories).ToList();
        var needed = Math.Max(rows.Count, 1);
        var current = table.DataRange.RowCount();
        if (needed > current) table.DataRange.LastRow().InsertRowsBelow(needed - current);
        for (var r = current; r > needed; r--) table.DataRange.Row(r).Delete();

        var body = table.DataRange;
        for (var i = 0; i < needed; i++)
        {
            var row = body.Row(i + 1);
            foreach (var cell in row.Cells()) cell.Value = Blank.Value;
            if (i >= rows.Count) continue;
            var c = rows[i];
            void Set(int col, string? value) { if (col > 0 && value is not null) row.Cell(col).SetValue(value); }
            Set(section, SectionText(c.Section));
            Set(code, c.Code);
            Set(description, c.Label);
            Set(actionBy, c.ActionBy);
            Set(email, c.EmailAction);
            Set(recommendation, c.Recommendation);
            Set(note, c.Note);
        }
    }

    /// <summary>
    /// Shows one dashboard row per active category of each section and hides the others.
    /// Returns a problem when a section has more categories than the template has rows.
    /// </summary>
    public static string? ShowSlots(XLWorkbook wb, IReadOnlyList<DashboardCategory> categories)
    {
        foreach (var (name, section) in new[] { (ExcelColumns.HoldSlotsName, CategorySection.Hold), (ExcelColumns.NaSlotsName, CategorySection.NotApplicable) })
        {
            var range = Slots(wb, name)!;
            var count = categories.Count(c => c.Section == section);
            var rows = range.RowCount();
            if (count > rows)
                return $"This unit has {count} {SectionText(section)} categories but the template has rows for {rows}. Add rows to {name} in the template, or deactivate categories.";
            for (var i = 1; i <= rows; i++)
            {
                var row = range.Row(i).WorksheetRow();
                if (i <= Math.Max(count, 1)) row.Unhide(); else row.Hide();
            }
        }
        return null;
    }

    /// <summary>Value stored in the export snapshot for an asset's category: "Hold:Cat1", "N/A:Cat2" or null.</summary>
    public static string? Token(DashboardCategory? c) => c is null || c.Code is null ? null : $"{SectionText(c.Section)}:{c.Code}";

    public static string SectionText(CategorySection s) => s == CategorySection.Hold ? "Hold" : "N/A";

    public static IEnumerable<DashboardCategory> Ordered(IEnumerable<DashboardCategory> categories) =>
        categories.Where(c => c.Code is not null).OrderBy(c => c.Section).ThenBy(c => c.SortOrder).ThenBy(c => c.Id);

    public static IXLRange? Slots(XLWorkbook wb, string name)
    {
        var defined = wb.DefinedNames.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        return defined?.Ranges.FirstOrDefault();
    }

    private static List<string>? CategoryHeaders(XLWorkbook wb) =>
        WorkbookFormat.FindTable(wb, ExcelColumns.CategoriesTable)?.HeadersRow().Cells().Select(c => ExcelColumns.Norm(WorkbookFormat.CellText(c))).ToList();

    private static CategorySection? ParseSection(string? text) => ExcelColumns.Norm(text) switch
    {
        "hold" => CategorySection.Hold,
        "na" or "notapplicable" => CategorySection.NotApplicable,
        _ => null
    };

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
