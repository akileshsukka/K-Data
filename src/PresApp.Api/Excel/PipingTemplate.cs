using ClosedXML.Excel;

namespace PresApp.Api.Excel;

/// <summary>
/// The piping line list template, one for the whole project (Admin → Excel templates). K Data fills it on every export:
/// <list type="bullet">
/// <item><b>Line list</b>: an Excel table with a "Tag No" column and one empty row. Columns are recognised by their
/// header (see <see cref="Find"/>), in any order; other columns are left empty and never imported.</item>
/// <item><b>Summary</b>: the row whose column B is <c>{CODE}</c> is the model row: it is copied once per service code of the
/// unit's lines, with its formulas (relative references move with the row). Everything else is kept as written.</item>
/// <item><c>{NOTES}</c>: where the Summary notes are written (if missing: two rows under the Summary).</item>
/// <item><c>{UNIT}</c> in any text or sheet name becomes the unit number; a cell <c>{DATE}</c> becomes the export date.</item>
/// </list>
/// Without an uploaded template the built-in default (<see cref="BuildDefault"/>) is used: the engineers' U39 layout.
/// </summary>
public static class PipingTemplate
{
    public const string CodePlaceholder = "{CODE}";
    public const string NotesPlaceholder = "{NOTES}";
    public const string NotesName = "KD_Notes";
    public const string DefaultFileName = "Piping line list template (default).xlsx";

    private static readonly XLColor Blue = XLColor.FromHtml("#0070C0");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#F2F2F2");

    /// <summary>Other names accepted for each column of <see cref="PipingLayout.Columns"/> (normalised: lower case, letters and digits only).</summary>
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["Sr. No."] = ["srno", "sno", "sr", "no", "serial"],
        [PipingLayout.TagHeader] = ["tagno", "lineno", "linenumber", "tag"],
        ["P&ID Number"] = ["pidnumber", "pidno", "pid", "pandid", "pandidno", "pandidnumber"],
        [PipingLayout.ProcessCodeHeader] = ["processcode", "servicecode", "service"],
        [PipingLayout.ProcessDescriptionHeader] = ["processdescription", "servicedescription"],
        ["Current Activity"] = ["currentactivity"],
        [PipingLayout.WalkdownHeader] = ["emwalkdownstatus", "emwalkdown", "walkdownstatus", "walkdown"],
        [PipingLayout.DrainedHeader] = ["bulkliquiddrained", "g1bulkliquiddrained", "drained", "draining"],
        [PipingLayout.InspectionHeader] = ["inspectiondone", "inspection"],
        [PipingLayout.CleaningRequiredHeader] = ["cleaningrequired", "cleaningscope"],
        [PipingLayout.CleanedHeader] = ["cleaned", "cleaningdone", "cleaningperformed"],
    };

    /// <summary>The K Data column a header stands for, or null (a column K Data does not fill).</summary>
    public static PipingColumn? Find(string header)
    {
        var k = ExcelColumns.Norm(header);
        if (k.Length == 0) return null;
        return PipingLayout.Columns.FirstOrDefault(c => Aliases[c.Header].Contains(k));
    }

    /// <summary>Header position (0-based) of each K Data column in a table; the first matching header wins.</summary>
    public static Dictionary<PipingColumn, int> Map(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<PipingColumn, int>();
        for (var i = 0; i < headers.Count; i++)
            if (Find(headers[i]) is { } c) map.TryAdd(c, i);
        return map;
    }

    /// <summary>The line list table: the first table with a "Tag No" (or Line No.) column.</summary>
    public static IXLTable? FindTable(XLWorkbook wb) =>
        wb.Worksheets.SelectMany(w => w.Tables).FirstOrDefault(t => t.HeadersRow().Cells().Any(c => Find(WorkbookFormat.CellText(c))?.Role == PipingRole.Tag));

    /// <summary>Why a workbook cannot be the template, or null.</summary>
    public static string? Problem(XLWorkbook wb)
    {
        var table = FindTable(wb);
        if (table is null) return "The template has no Excel table with a \"Tag No\" column.";
        if (table.DataRange is null || table.DataRange.RowCount() < 1) return $"The table {table.Name} must keep one empty data row.";
        if (table.HeadersRow().Cells().Any(c => ExcelColumns.Norm(WorkbookFormat.CellText(c)) == "key")) return "The template already has a \"Key\" column. Remove it: the app adds it.";
        foreach (var name in new[] { WorkbookFormat.MetaSheet, WorkbookFormat.ListsSheet, WorkbookFormat.BaseSheet })
            if (wb.TryGetWorksheet(name, out _)) return $"The template must not have a sheet named {name}. Upload the clean template, not an exported file.";
        if (wb.Worksheets.Any(w => w.IsProtected)) return "The template has a protected sheet. Remove the protection and upload it again.";
        if (wb.Worksheets.SelectMany(w => w.CellsUsed()).Count(c => !c.HasFormula && WorkbookFormat.CellText(c).Trim() == CodePlaceholder) > 1)
            return $"Only one Summary row can hold {CodePlaceholder}.";
        return null;
    }

    /// <summary>What the uploaded template contains, shown after the upload.</summary>
    public static List<string> Describe(XLWorkbook wb)
    {
        var notes = new List<string>();
        var table = FindTable(wb)!;
        var headers = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c)).ToList();
        var map = Map(headers);
        var missing = PipingLayout.Columns.Where(c => !map.ContainsKey(c)).Select(c => c.Header).ToList();
        if (missing.Count > 0) notes.Add($"Columns K Data will not fill (not in the template): {string.Join(", ", missing)}.");
        var extra = headers.Where(h => h.Trim().Length > 0 && Find(h) is null).ToList();
        if (extra.Count > 0) notes.Add($"Columns left empty and never imported: {string.Join(", ", extra)}.");
        var cells = wb.Worksheets.SelectMany(w => w.CellsUsed()).Where(c => !c.HasFormula).Select(c => WorkbookFormat.CellText(c).Trim()).ToList();
        if (!cells.Contains(CodePlaceholder)) notes.Add($"No Summary row with {CodePlaceholder} in column B: the Summary is not filled per service code.");
        if (!cells.Contains(NotesPlaceholder)) notes.Add($"No {NotesPlaceholder} cell: the notes go two rows under the Summary.");
        return notes;
    }

    // ------------------------------------------------------------------ built-in default

    /// <summary>
    /// The engineers' layout (U39 ALL PIPING LINES, 27-Oct-2026) as a template. The Cleaning scope columns are the
    /// current Cleaning Required values; their formulas read the value from the column header, so renaming a header in
    /// the template is enough. Cleaning Performed % = cleaned / cleaning scope (every scope column except TBC and similar).
    /// </summary>
    public static byte[] BuildDefault(IReadOnlyList<string> cleaningOptions)
    {
        using var wb = new XLWorkbook();
        wb.Style.Font.FontName = "Calibri";
        wb.Style.Font.FontSize = 10;
        wb.Style.Font.FontColor = Blue;
        var columns = PipingLayout.Columns;

        // ---- Line List: headers on row 2 from column B, one empty row
        var ws = wb.Worksheets.Add(PipingLayout.LineSheet);
        ws.ShowGridLines = false;
        ws.Column(1).Width = 3.7;
        for (var c = 0; c < columns.Count; c++)
        {
            ws.Cell(PipingLayout.HeaderRow, PipingLayout.FirstColumn + c).SetValue(columns[c].Header);
            ws.Column(PipingLayout.FirstColumn + c).Width = columns[c].Width;
        }
        var lastCol = PipingLayout.FirstColumn + columns.Count - 1;
        var range = ws.Range(PipingLayout.HeaderRow, PipingLayout.FirstColumn, PipingLayout.HeaderRow + 1, lastCol);
        var table = range.CreateTable(PipingLayout.TableName);
        table.Theme = XLTableTheme.None;
        table.ShowAutoFilter = true;
        var header = ws.Range(PipingLayout.HeaderRow, PipingLayout.FirstColumn, PipingLayout.HeaderRow, lastCol);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = HeaderFill;
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        header.Style.Alignment.WrapText = true;
        ws.Row(PipingLayout.HeaderRow).Height = 27.6;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Hair;
        var body = ws.Range(PipingLayout.HeaderRow + 1, PipingLayout.FirstColumn, PipingLayout.HeaderRow + 1, lastCol);
        body.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        body.Style.Alignment.WrapText = true;
        ws.SheetView.FreezeRows(PipingLayout.HeaderRow);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(PipingLayout.HeaderRow, PipingLayout.HeaderRow);

        WriteSummary(wb.Worksheets.Add(PipingLayout.SummarySheet), cleaningOptions);

        using var output = new MemoryStream();
        wb.SaveAs(output);
        return output.ToArray();
    }

    public const string ScopeHeader = "% of scope";

    /// <summary>Cleaning Required values that are not a cleaning to do: not part of the scope.</summary>
    public static bool NotScope(string option) =>
        Domain.Rules.ValueNormalizer.ListKey(option) is "tbc" or "notapplicable" or "na" or "n/a" or "notrequired" or "none" or "no";

    private static void WriteSummary(IXLWorksheet s, IReadOnlyList<string> cleaningOptions)
    {
        s.ShowGridLines = false;
        s.Column(1).Width = 2.5;
        s.Column(2).Width = 12;
        s.Column(3).Width = 43;
        s.Column(4).Width = 11;

        s.Cell(2, 2).SetValue("UNIT {UNIT} PIPING STATUS SUMMARY");
        s.Cell(2, 2).Style.Font.Bold = true;
        var dateCell = s.Cell(3, 2);
        dateCell.SetValue("{DATE}");
        dateCell.Style.NumberFormat.Format = "d-mmm-yy";
        dateCell.Style.Font.Bold = true;
        dateCell.Style.Font.FontColor = XLColor.Red;
        dateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        // (header, criteria column, criteria taken from the header cell, percent)
        var blocks = new List<(string Group, List<(string Header, string? Column, bool FromHeader, bool Percent)> Cols)>
        {
            ("Walkdown", [("Completed", PipingLayout.WalkdownHeader, true, false), ("Complete %", null, false, true)]),
            ("Draining", [("Yes", PipingLayout.DrainedHeader, true, false), ("Complete %", null, false, true)]),
            ("Inspection", [("Yes", PipingLayout.InspectionHeader, true, false), ("Complete %", null, false, true)]),
        };
        if (cleaningOptions.Count > 0)
            blocks.Add(("Cleaning scope", cleaningOptions.Select(o => (o, (string?)PipingLayout.CleaningRequiredHeader, true, false)).ToList()));
        blocks.Add(("Cleaning Performed", [("Yes", PipingLayout.CleanedHeader, true, false), (ScopeHeader, null, false, true)]));

        const int groupRow = 3, headerRow = 4, totalRow = 5, codeRow = 6;
        s.Cell(headerRow, 2).SetValue("Process code");
        s.Cell(headerRow, 3).SetValue("Process description");
        s.Cell(headerRow, 4).SetValue("Total lines");
        s.Cell(totalRow, 2).SetValue("All lines");
        s.Cell(totalRow, 3).SetValue("UNIT {UNIT} TOTAL");
        s.Cell(totalRow, 4).FormulaA1 = $"COUNTIFS({T(PipingLayout.TagHeader)},\"<>\")";
        s.Cell(codeRow, 2).SetValue(CodePlaceholder);
        var r = codeRow;
        s.Cell(r, 3).FormulaA1 =
            $"IF($B{r}=\"\",\"\",IF(COUNTIFS($B${codeRow}:B{r},$B{r})>1,\"Duplicate code - counted above\",IF(COUNTIFS({T(PipingLayout.ProcessCodeHeader)},$B{r})=0,\"Code not found in line list\",INDEX({T(PipingLayout.ProcessDescriptionHeader)},MATCH($B{r},{T(PipingLayout.ProcessCodeHeader)},0)))))";
        s.Cell(r, 4).FormulaA1 = CodeFormula(r, codeRow, null);

        var scopeColumns = new List<string>();
        var col = 5;
        foreach (var (group, cols) in blocks)
        {
            var first = col;
            foreach (var (head, column, fromHeader, percent) in cols)
            {
                s.Cell(headerRow, col).SetValue(head);
                s.Column(col).Width = Math.Max(10, head.Length + 2);
                var letter = XLHelper.GetColumnLetterFromNumber(col);
                if (group == "Cleaning scope" && !NotScope(head)) scopeColumns.Add(letter);
                var countLetter = XLHelper.GetColumnLetterFromNumber(col - 1);
                // The criteria is the header cell (e.g. K$4 = "Hydromilling"): renaming the header changes what is counted.
                var criteria = $"{letter}${headerRow}";
                string Percent(int row) => head == ScopeHeader && scopeColumns.Count > 0
                    ? $"IF(SUM({string.Join(",", scopeColumns.Select(c => $"{c}{row}"))})>0,{countLetter}{row}/SUM({string.Join(",", scopeColumns.Select(c => $"{c}{row}"))}),\"\")"
                    : $"IF(AND(ISNUMBER(D{row}),D{row}>0),{countLetter}{row}/D{row},\"\")";
                s.Cell(totalRow, col).FormulaA1 = percent ? Percent(totalRow) : $"COUNTIFS({T(PipingLayout.TagHeader)},\"<>\",{T(column!)},{criteria})";
                s.Cell(codeRow, col).FormulaA1 = percent ? Percent(codeRow) : CodeFormula(codeRow, codeRow, $"{T(column!)},{criteria}");
                var cells = s.Range(totalRow, col, codeRow, col);
                cells.Style.NumberFormat.Format = percent ? "0.0%" : "#,##0";
                cells.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                col++;
            }
            var groupRange = s.Range(groupRow, first, groupRow, col - 1);
            if (col - 1 > first) groupRange.Merge();
            groupRange.FirstCell().SetValue(group);
            groupRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            groupRange.Style.Font.Bold = true;
            groupRange.Style.Fill.BackgroundColor = HeaderFill;
            groupRange.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        }
        var lastCol = col - 1;

        var head4 = s.Range(headerRow, 2, headerRow, lastCol);
        head4.Style.Font.Bold = true;
        head4.Style.Fill.BackgroundColor = HeaderFill;
        head4.Style.Alignment.WrapText = true;
        head4.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        s.Row(headerRow).Height = 27.6;
        var total = s.Range(totalRow, 2, totalRow, lastCol);
        total.Style.Font.Bold = true;
        total.Style.Fill.BackgroundColor = HeaderFill;
        s.Range(totalRow, 4, codeRow, 4).Style.NumberFormat.Format = "#,##0";
        var grid = s.Range(headerRow, 2, codeRow, lastCol);
        grid.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        grid.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        s.Cell(codeRow + 2, 2).SetValue(NotesPlaceholder);
        s.SheetView.FreezeRows(headerRow);
        s.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        s.PageSetup.FitToPages(1, 0);
    }

    /// <summary>Lines of the code in column B (the first time it appears), optionally with one more condition.</summary>
    private static string CodeFormula(int r, int firstCodeRow, string? extra)
    {
        var more = extra is null ? "" : "," + extra;
        return $"IF($B{r}=\"\",\"\",IF(COUNTIFS($B${firstCodeRow}:B{r},$B{r})>1,\"\",COUNTIFS({T(PipingLayout.TagHeader)},\"<>\",{T(PipingLayout.ProcessCodeHeader)},$B{r}{more})))";
    }

    private static string T(string header) => $"{PipingLayout.TableName}[{header}]";
}
