using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using PresApp.Domain.Reporting;

namespace PresApp.Api.Excel;

/// <summary>
/// Writes the unit's dashboard categories into the workbook:
/// <list type="bullet">
/// <item>the <c>Categories</c> table (Priority, Category, Keyword, Section) read by the master list "Category" formula;</item>
/// <item>the "Hold Category" / "N/A Category" block of the Dashboard sheet, with its totals.</item>
/// </list>
/// The block keeps the template's look: each kind of row (header, item, total, closing rows) copies the style of
/// the same kind of row in the template. When the unit has no categories yet, the template is left as it is.
/// </summary>
public static partial class DashboardSheetWriter
{
    private const int FirstCol = 2;   // B
    private const int LastCol = 10;   // J

    private const string DefaultCountFormula =
        "COUNTIFS(MasterList[Discipline],\"<>Electrical\",MasterList[Discipline],\"<>Instrumentation\",MasterList[Discipline],\"<>E&I\",MasterList[Category],TRIM(B{0}))";

    public static void Write(XLWorkbook wb, IReadOnlyList<DashboardCategory> categories)
    {
        if (categories.Count == 0) return;
        WriteCategoriesTable(wb, categories);
        if (wb.TryGetWorksheet("Dashboard", out var dashboard)) WriteBlock(dashboard, categories);
    }

    public static string SectionText(CategorySection s) => s == CategorySection.Hold ? "Hold" : "N/A";

    private static void WriteCategoriesTable(XLWorkbook wb, IReadOnlyList<DashboardCategory> categories)
    {
        var table = WorkbookFormat.FindTable(wb, ExcelColumns.CategoriesTable);
        if (table is null) return;
        var headers = table.HeadersRow().Cells().Select(c => ExcelColumns.Norm(WorkbookFormat.CellText(c))).ToList();
        int Col(string name) => headers.IndexOf(name) + 1;
        int priority = Col("priority"), category = Col("category"), keyword = Col("keyword"), section = Col("section");
        if (priority == 0 || category == 0 || keyword == 0) return;

        var rows = categories.OrderBy(c => c.Priority).ThenBy(c => c.SortOrder)
            .SelectMany(c => c.Keywords.Select(k => (c, k.Keyword))).ToList();
        var needed = Math.Max(rows.Count, 1);
        var current = table.DataRange.RowCount();
        if (needed > current) table.DataRange.LastRow().InsertRowsBelow(needed - current);
        for (var r = current; r > needed; r--) table.DataRange.Row(r).Delete();

        var body = table.DataRange;
        for (var i = 0; i < needed; i++)
        {
            var row = body.Row(i + 1);
            if (i >= rows.Count) { row.Cells().ToList().ForEach(c => c.Value = Blank.Value); continue; }
            var (c, kw) = rows[i];
            row.Cell(priority).Value = c.Priority;
            row.Cell(category).SetValue(c.Label);
            row.Cell(keyword).SetValue(kw);
            if (section > 0) row.Cell(section).SetValue(SectionText(c.Section));
        }
    }

    private static void WriteBlock(IXLWorksheet ws, IReadOnlyList<DashboardCategory> categories)
    {
        int? Find(Func<string, bool> match, int from = 1)
        {
            var last = ws.LastRowUsed()?.RowNumber() ?? 0;
            for (var r = from; r <= last; r++)
            {
                if (match(ExcelColumns.Norm(WorkbookFormat.CellText(ws.Cell(r, FirstCol))))) return r;
            }
            return null;
        }

        var holdHeader = Find(k => k.StartsWith("holdcategor", StringComparison.Ordinal));
        if (holdHeader is null) return;
        var naHeader = Find(k => k.StartsWith("nacategor", StringComparison.Ordinal), holdHeader.Value + 1);
        var end = Find(k => k.StartsWith("availableforpreservation", StringComparison.Ordinal), holdHeader.Value + 1);
        if (naHeader is null || end is null || naHeader > end) return;

        // ---- template rows to copy from (style + texts); styles parked on a scratch sheet while the block is cleared
        var scratch = ws.Workbook.AddWorksheet("_block_" + Guid.NewGuid().ToString("N")[..8]);
        RowSnapshot Snapshot(IXLWorksheet sheet, int r) => TakeSnapshot(sheet, r, scratch);
        var headerRow = Snapshot(ws, holdHeader.Value);
        var naHeaderRow = Snapshot(ws, naHeader.Value);
        var itemRow = Snapshot(ws, holdHeader.Value + 1);
        var total1 = Find(k => k.StartsWith("total", StringComparison.Ordinal), holdHeader.Value + 1) ?? naHeader.Value - 1;
        var totalRow = Snapshot(ws, total1);
        var grand = Find(k => k.StartsWith("gran", StringComparison.Ordinal), naHeader.Value + 1);
        var equipment = Find(k => k.StartsWith("totalequipment", StringComparison.Ordinal), naHeader.Value + 1);
        var grandRow = Snapshot(ws, grand ?? end.Value - 2);
        var equipmentRow = Snapshot(ws, equipment ?? end.Value - 1);
        var endRow = Snapshot(ws, end.Value);
        var countFormula = ws.Cell(holdHeader.Value + 1, 3).HasFormula
            ? CategoryRef().Replace(ws.Cell(holdHeader.Value + 1, 3).FormulaA1, "TRIM(B{0})")
            : DefaultCountFormula;

        // ---- clear the old block (values, styles, row heights)
        for (var r = holdHeader.Value; r <= end.Value; r++)
        {
            ws.Range(r, FirstCol, r, LastCol + 1).Clear();
            ws.Row(r).ClearHeight();
        }

        // ---- write the new block, pushing anything below it down when it grows
        var hold = categories.Where(c => c.Section == CategorySection.Hold).OrderBy(c => c.SortOrder).ThenBy(c => c.Priority).ToList();
        var na = categories.Where(c => c.Section == CategorySection.NotApplicable).OrderBy(c => c.SortOrder).ThenBy(c => c.Priority).ToList();
        var oldLength = end.Value - holdHeader.Value + 1;
        var newLength = 1 + Math.Max(hold.Count, 1) + 1 + 1 + Math.Max(na.Count, 1) + 1 + 3;
        if (newLength > oldLength) ws.Row(end.Value).InsertRowsBelow(newLength - oldLength);

        var r0 = holdHeader.Value;
        var row = r0;
        int WriteSection(RowSnapshot header, List<DashboardCategory> items, string totalLabel)
        {
            Paste(ws, row++, header, keepTexts: true);
            var first = row;
            if (items.Count == 0) Paste(ws, row++, itemRow, keepTexts: false);
            foreach (var c in items)
            {
                Paste(ws, row, itemRow, keepTexts: false);
                ws.Cell(row, FirstCol).SetValue(c.Label);
                ws.Cell(row, 3).FormulaA1 = string.Format(CultureInfo.InvariantCulture, countFormula, row);
                if (c.ActionBy is not null) ws.Cell(row, 4).SetValue(c.ActionBy);
                if (c.EmailAction is not null) ws.Cell(row, 5).SetValue(c.EmailAction);
                if (c.Note is not null) ws.Cell(row, 6).SetValue(c.Note);
                row++;
            }
            var totalAt = row;
            Paste(ws, row, totalRow, keepTexts: false);
            ws.Cell(row, FirstCol).SetValue(totalLabel);
            ws.Cell(row, 3).FormulaA1 = items.Count == 0 ? "0" : $"SUM(C{first}:C{row - 1})";
            row++;
            return totalAt;
        }

        var t1 = WriteSection(headerRow, hold, "Total -1");
        var t2 = WriteSection(naHeaderRow, na, "Total -2");

        Paste(ws, row, grandRow, keepTexts: true);
        ws.Cell(row, 3).FormulaA1 = $"C{t1}+C{t2}";
        var grandAt = row++;
        Paste(ws, row, equipmentRow, keepTexts: true);
        ws.Cell(row, 3).FormulaA1 = equipmentRow.Formulas.GetValueOrDefault(3) ?? "G5";
        var equipmentAt = row++;
        Paste(ws, row, endRow, keepTexts: true);
        ws.Cell(row, 3).FormulaA1 = $"C{equipmentAt}-C{grandAt}";
        // rows freed when the block shrank are left empty
        scratch.Delete();
    }

    private sealed record RowSnapshot(IXLWorksheet Scratch, int ScratchRow, double Height,
        Dictionary<int, XLCellValue> Values, Dictionary<int, string> Formulas);

    private static RowSnapshot TakeSnapshot(IXLWorksheet ws, int row, IXLWorksheet scratch)
    {
        var at = (scratch.LastRowUsed(XLCellsUsedOptions.All)?.RowNumber() ?? 0) + 1;
        var values = new Dictionary<int, XLCellValue>();
        var formulas = new Dictionary<int, string>();
        for (var c = FirstCol; c <= LastCol + 1; c++)
        {
            var cell = ws.Cell(row, c);
            scratch.Cell(at, c).Style = cell.Style;
            scratch.Cell(at, c).Value = "x";   // keeps the scratch row "used"
            if (cell.HasFormula) formulas[c] = cell.FormulaA1;
            else if (!cell.Value.IsBlank) values[c] = cell.Value;
        }
        return new RowSnapshot(scratch, at, ws.Row(row).Height, values, formulas);
    }

    private static void Paste(IXLWorksheet ws, int row, RowSnapshot snap, bool keepTexts)
    {
        for (var c = FirstCol; c <= LastCol + 1; c++) ws.Cell(row, c).Style = snap.Scratch.Cell(snap.ScratchRow, c).Style;
        ws.Row(row).Height = snap.Height;
        if (!keepTexts) return;
        foreach (var (c, v) in snap.Values) ws.Cell(row, c).Value = v;
        foreach (var (c, f) in snap.Formulas)
        {
            if (c != 3) ws.Cell(row, c).FormulaA1 = f;   // column C is rewritten by the caller
        }
    }

    [GeneratedRegex(@"TRIM\(\$?B\$?\d+\)")]
    private static partial Regex CategoryRef();
}
