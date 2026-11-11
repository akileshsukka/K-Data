using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Imports;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

public sealed record ExportResult(string FileName, byte[] Content, Guid SessionId, int Rows);

/// <summary>
/// Builds the client "Master Equipment List – Preservation" workbook of one unit from its template, as the
/// Power Automate flow did, but without sheet protection: the import checks the structure instead.
/// <list type="bullet">
/// <item>One row per active equipment, sorted like the register. Without an <c>EIList</c> table the E&amp;I rows are in
/// the MasterList, hidden by the filter; with it (template v5) they go to the EIList and the MasterList keeps the rest.</item>
/// <item>Hidden "Key" column = asset ID; very hidden <c>_meta</c> (session) and <c>_lists</c> (dropdowns).</item>
/// <item>Editable cells written as text so Excel never converts a value; list columns get dropdowns.</item>
/// <item>Categories table and the dashboard category block rebuilt from the unit's categories.</item>
/// </list>
/// The values at export and the table layout are stored with the session on the server.
/// </summary>
public sealed class UnitWorkbookExporter(PresAppDbContext db, TimeProvider clock)
{
    private static readonly HashSet<string> HiddenDisciplines = new(StringComparer.OrdinalIgnoreCase) { "Electrical", "Instrumentation", "E&I" };

    /// <summary>What a table needs to be filled.</summary>
    private sealed record FillContext(Domain.Catalog.Unit Unit, Dictionary<string, List<string>> OptionsBySet, List<DashboardCategory> Categories, ListSheet Lists);

    public async Task<ExportResult> ExportAsync(int unitId, string exportedBy, CancellationToken ct)
    {
        var unit = await db.Units.AsNoTracking().Include(u => u.Plant).FirstOrDefaultAsync(u => u.Id == unitId, ct)
                   ?? throw ApiProblem.NotFound("Unit not found.");
        var template = await db.ExcelTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.UnitId == unitId, ct)
                       ?? throw ApiProblem.Invalid($"There is no Excel template for {unit.Name} yet. An administrator can upload it in Excel templates.");

        var assets = (await db.Assets.AsNoTracking()
                .Include(a => a.Preservation).ThenInclude(p => p!.DashboardCategory).Include(a => a.Discipline)
                .Where(a => a.UnitId == unitId && a.IsActive && a.Type != AssetType.Pipeline)
                .ToListAsync(ct))
            .OrderBy(a => AssetQueries.DisciplineRank(a.Discipline.Name)).ThenBy(a => a.Tag ?? a.Code, NaturalComparer.Instance)
            .ToList();
        var options = await db.OptionValues.AsNoTracking().Where(o => o.IsActive)
            .OrderBy(o => o.SortOrder).Select(o => new { o.OptionSet.Key, o.Value }).ToListAsync(ct);
        var optionsBySet = options.GroupBy(o => o.Key).ToDictionary(g => g.Key, g => g.Select(o => o.Value).Distinct().ToList());
        var categories = await db.DashboardCategories.AsNoTracking().Include(c => c.Keywords)
            .Where(c => c.UnitId == unitId && c.IsActive).OrderBy(c => c.Section).ThenBy(c => c.SortOrder).ThenBy(c => c.Priority)
            .ToListAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, SiteClock.Dubai);
        var sessionId = Guid.NewGuid();

        using var wb = OpenTemplate(template.Content);
        var v4 = TemplateV4.IsV4(wb);
        var formatVersion = v4 ? TemplateV4.FormatVersion : WorkbookFormat.FormatVersion;
        List<DashboardCategory> Coded(CategoryScope scope) => categories.Where(c => c.Code is not null && c.Scope == scope).ToList();
        if (v4)
        {
            foreach (var block in TemplateV4.Blocks(wb))
                if (TemplateV4.ShowSlots(wb, Coded(block.Scope), block) is { } slotProblem) throw ApiProblem.Invalid(slotProblem);
        }
        var table = WorkbookFormat.FindTable(wb, ExcelColumns.MasterTable)!;
        var sheet = table.Worksheet;
        var eiTable = WorkbookFormat.FindTable(wb, ExcelColumns.EiTable);

        // ---- rows: with an E&I list, the E&I items go there and the MasterList keeps the static and rotating equipment
        var masterAssets = eiTable is null ? assets : assets.Where(a => a.Type != AssetType.ElectricalInstrument).ToList();
        var eiAssets = eiTable is null ? [] : assets.Where(a => a.Type == AssetType.ElectricalInstrument).ToList();
        var lists = new ListSheet(wb.Worksheets.Add(WorkbookFormat.ListsSheet));
        // Category dropdowns: each list offers its own catalogue (an old file without EIList keeps every item in the MasterList).
        var context = new FillContext(unit, optionsBySet, eiTable is null ? categories : Coded(CategoryScope.Mechanical), lists);
        var masterColumns = Fill(table, masterAssets, context);
        var eiColumns = eiTable is null ? [] : Fill(eiTable, eiAssets, context with { Categories = Coded(CategoryScope.ElectricalInstrument) });

        // ---- E&I rows of the MasterList hidden by the filter (still in the file, not counted by the dashboard)
        var disciplineCol = masterColumns.FirstOrDefault(c => c.Role == ColumnRole.Discipline);
        if (disciplineCol is not null && masterAssets.Count > 0)
        {
            var body = table.DataRange;
            var all = masterAssets.Select(a => a.Discipline.Name).Distinct().ToList();
            var keep = all.Where(d => !HiddenDisciplines.Contains(d.Trim())).ToList();
            if (keep.Count > 0 && keep.Count < all.Count)
            {
                var filter = table.AutoFilter.Column(disciplineCol.Index + 1);
                foreach (var d in keep) filter.AddFilter(d);
                for (var i = 0; i < masterAssets.Count; i++)
                {
                    if (HiddenDisciplines.Contains(masterAssets[i].Discipline.Name.Trim())) body.Row(i + 1).WorksheetRow().Hide();
                }
            }
        }
        lists.Sheet.Visibility = XLWorksheetVisibility.VeryHidden;

        // ---- placeholders, categories and dashboard
        var reportDate = local.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        ReplacePlaceholders(wb, unit.Number.ToString(CultureInfo.InvariantCulture), reportDate);
        if (v4)
        {
            foreach (var block in TemplateV4.Blocks(wb)) TemplateV4.WriteCategories(wb, Coded(block.Scope), block);
        }
        else DashboardSheetWriter.Write(wb, categories.Where(c => c.Scope == CategoryScope.Mechanical).ToList());

        // ---- session sheet
        var n = assets.Count;
        var meta = wb.Worksheets.Add(WorkbookFormat.MetaSheet);
        string[][] metaRows =
        [
            ["SessionId", sessionId.ToString()],
            ["Unit", unit.Name],
            ["ExportedAtUtc", now.ToString("O", CultureInfo.InvariantCulture)],
            ["ExportedBy", exportedBy],
            ["FormatVersion", formatVersion.ToString(CultureInfo.InvariantCulture)],
            ["RowCount", n.ToString(CultureInfo.InvariantCulture)],
            ["App", WorkbookFormat.AppName]
        ];
        for (var r = 0; r < metaRows.Length; r++)
        {
            meta.Cell(r + 1, 1).SetValue(metaRows[r][0]);
            meta.Cell(r + 1, 2).SetValue(metaRows[r][1]);
        }
        meta.Visibility = XLWorksheetVisibility.VeryHidden;

        // Only the master list is selected and active; a template saved with another sheet selected
        // would otherwise open with both grouped ("cannot make changes ... when multiple sheets are selected").
        foreach (var w in wb.Worksheets) { w.TabActive = false; w.TabSelected = false; }
        sheet.TabSelected = true;
        sheet.SetTabActive();
        wb.FullCalculationOnLoad = true;

        var fileName = $"Unit_{unit.Number}_Master_Equipment_List_Preservation_{local.ToString("dd_MMM_yyyy_HHmm", CultureInfo.InvariantCulture)}.xlsx";
        using var output = new MemoryStream();
        wb.SaveAs(output);

        // ---- session: layout and values at export, kept on the server
        var signature = StructureSignature.Of(table);
        if (eiTable is not null) signature = signature with { Others = [StructureSignature.Of(eiTable)] };
        var editable = masterColumns.Concat(eiColumns).Where(c => c.Editable).Select(c => c.Field!).DistinctBy(f => f.Key).ToList();
        db.ExcelSessions.Add(new ExcelSession
        {
            Id = sessionId,
            UnitId = unitId,
            AssetType = AssetType.Equipment,
            ExportedAtUtc = now,
            ExportedBy = exportedBy,
            Status = ExcelSessionStatus.Open,
            FormatVersion = formatVersion,
            FileName = fileName,
            RowCount = n,
            StructureSignature = signature.ToJson(),
            BaseJson = new BaseSnapshot(assets.Select(a =>
            {
                var values = editable.ToDictionary(f => f.Key, f => FieldValues.Read(f, a));
                if (v4) values[TemplateV4.CategoryField] = a.Preservation?.DashboardCategory is { IsActive: true } c ? TemplateV4.Token(c) : null;
                return new BaseRow(a.Id, a.Tag, a.Code, values);
            }).ToList()).ToJson()
        });
        await db.SaveChangesAsync(ct);

        return new ExportResult(fileName, output.ToArray(), sessionId, n);
    }

    /// <summary>Writes one row per asset into a list table, adds its hidden Key column and its dropdowns.</summary>
    private static List<ExcelColumn> Fill(IXLTable table, List<Asset> assets, FillContext context)
    {
        var sheet = table.Worksheet;
        var headers = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c)).ToList();
        var templateRow = table.DataRange.FirstRow();
        var formulas = templateRow.Cells().Select(c => c.HasFormula ? c.FormulaR1C1 : null).ToList();
        var columns = ExcelColumns.Map(headers, formulas.Select(f => f is not null).ToList());

        // ---- rows
        var n = assets.Count;
        if (n > 1) table.DataRange.LastRow().InsertRowsBelow(n - 1);
        var body = table.DataRange;
        for (var i = 0; i < n; i++)
        {
            var a = assets[i];
            var row = body.Row(i + 1);
            foreach (var col in columns)
            {
                var cell = row.Cell(col.Index + 1);
                switch (col.Role)
                {
                    case ColumnRole.Serial: cell.Value = i + 1; break;
                    case ColumnRole.OperatingUnit: cell.Value = context.Unit.Number; break;
                    case ColumnRole.Discipline: cell.Value = a.Discipline.Name; break;
                    case ColumnRole.Tag: cell.Value = a.Tag ?? ""; break;
                    case ColumnRole.Description: cell.Value = a.Description ?? ""; break;
                    case ColumnRole.Formula: cell.FormulaR1C1 = formulas[col.Index]; break;
                    case ColumnRole.HoldCategory or ColumnRole.NaCategory:
                        var category = a.Preservation?.DashboardCategory;
                        var section = col.Role == ColumnRole.HoldCategory ? CategorySection.Hold : CategorySection.NotApplicable;
                        cell.Style.NumberFormat.Format = "@";
                        cell.SetValue(category is { IsActive: true, Code: not null } && category.Section == section ? category.Code : "");
                        break;
                    case ColumnRole.Field:
                        var text = FieldValues.Read(col.Field!, a) ?? "";
                        if (col.Editable) { cell.Style.NumberFormat.Format = "@"; cell.SetValue(text); }
                        else cell.Value = text;
                        break;
                    default: cell.Value = Blank.Value; break;
                }
            }
        }

        // ---- hidden Key column (asset ID)
        table.InsertColumnsAfter(1);
        var keyHeader = table.HeadersRow().LastCell();
        keyHeader.Value = ExcelColumns.KeyHeader;
        var keyColumn = keyHeader.Address.ColumnNumber;
        for (var i = 0; i < n; i++)
        {
            var cell = body.Row(i + 1).Cell(table.DataRange.ColumnCount());
            cell.Style.NumberFormat.Format = "@";
            cell.SetValue(assets[i].Id.ToString());
        }
        sheet.Column(keyColumn).Hide();
        // A template that hides every column after the table (e.g. Z:XFD) would swallow the Key column into that
        // hidden block; the last used column is then hidden up to XFD and Excel's horizontal scrollbar snaps back.
        if (keyColumn < XLHelper.MaxColumnNumber) sheet.Columns(keyColumn + 1, XLHelper.MaxColumnNumber).Unhide();

        // ---- dropdowns from the active options (very hidden _lists sheet)
        if (n == 0) return columns;
        foreach (var col in columns.Where(c => c.Editable && c.Field!.OptionSet is not null))
        {
            var setKey = col.Field!.OptionSet!;
            var source = context.Lists.Source(setKey, context.OptionsBySet.GetValueOrDefault(setKey) ?? []);
            if (source is null) continue;
            AddList(body.Column(col.Index + 1), source, "Invalid value", "Select a value from the list.");
        }
        // v4: Hold / N/A category columns take the active codes of their section
        foreach (var (role, section) in new[] { (ColumnRole.HoldCategory, CategorySection.Hold), (ColumnRole.NaCategory, CategorySection.NotApplicable) })
        {
            if (!columns.Any(c => c.Role == role)) continue;
            var codes = TemplateV4.Ordered(context.Categories).Where(c => c.Section == section).Select(c => c.Code!).ToList();
            var ei = context.Categories.Count > 0 && context.Categories.All(c => c.Scope == CategoryScope.ElectricalInstrument);
            var source = context.Lists.Source((ei ? "EI" : "") + (section == CategorySection.Hold ? "HoldCategory" : "NACategory"), codes);
            if (source is null) continue;
            foreach (var c in columns.Where(c => c.Role == role))
                AddList(body.Column(c.Index + 1), source, "Invalid category", "Pick a category code of this section. Categories are created in K Data.");
        }
        return columns;
    }

    private static void AddList(IXLRangeColumn column, IXLRange source, string title, string message)
    {
        var dv = column.CreateDataValidation();
        dv.List(source, true);
        dv.IgnoreBlanks = true;
        dv.ShowErrorMessage = true;
        dv.ErrorStyle = XLErrorStyle.Stop;
        dv.ErrorTitle = title;
        dv.ErrorMessage = message;
    }

    /// <summary>The very hidden <c>_lists</c> sheet: one column per list (header = list key), written once and shared by every table.</summary>
    private sealed class ListSheet(IXLWorksheet sheet)
    {
        private readonly Dictionary<string, IXLRange?> written = new(StringComparer.Ordinal);
        public IXLWorksheet Sheet { get; } = sheet;

        /// <summary>Range of the values of a list (null when it has none).</summary>
        public IXLRange? Source(string key, IReadOnlyList<string> values)
        {
            if (written.TryGetValue(key, out var range)) return range;
            var column = written.Count + 1;
            Sheet.Cell(1, column).Value = key;
            for (var v = 0; v < values.Count; v++) Sheet.Cell(v + 2, column).SetValue(values[v]);
            range = values.Count == 0 ? null : Sheet.Range(2, column, values.Count + 1, column);
            written[key] = range;
            return range;
        }
    }

    /// <summary>Opens a template and checks the rules every template must follow.</summary>
    public static XLWorkbook OpenTemplate(byte[] content)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(new MemoryStream(content)); }
        catch (Exception) { throw ApiProblem.Invalid("The template could not be opened. Upload an .xlsx workbook."); }

        var problem = TemplateProblem(wb);
        if (problem is null) return wb;
        wb.Dispose();
        throw ApiProblem.Invalid(problem);
    }

    public static string? TemplateProblem(XLWorkbook wb)
    {
        var table = WorkbookFormat.FindTable(wb, ExcelColumns.MasterTable);
        if (table is null) return "The template has no table named \"MasterList\".";
        if (ListProblem(table) is { } problem) return problem;
        if (WorkbookFormat.FindTable(wb, ExcelColumns.EiTable) is { } ei && ListProblem(ei) is { } eiProblem) return eiProblem;
        foreach (var name in new[] { WorkbookFormat.MetaSheet, WorkbookFormat.ListsSheet, WorkbookFormat.BaseSheet })
        {
            if (wb.TryGetWorksheet(name, out _)) return $"The template must not have a sheet named {name}. Upload the clean template, not an exported file.";
        }
        if (wb.Worksheets.Any(w => w.IsProtected)) return "The template has a protected sheet. Remove the protection and upload it again.";
        return TemplateV4.IsV4(wb) ? TemplateV4.Problem(wb) : null;
    }

    /// <summary>Rules of a list table of the template (MasterList, EIList).</summary>
    private static string? ListProblem(IXLTable table)
    {
        if (table.DataRange is null || table.DataRange.RowCount() < 1) return $"The {table.Name} table must keep one empty data row.";
        var headers = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c)).ToList();
        if (headers.Any(h => ExcelColumns.Norm(h) == "key")) return $"The {table.Name} table already has a \"Key\" column. Remove it: the app adds it.";
        if (!headers.Any(h => ExcelColumns.Norm(h) is "equipno" or "equipmentno" or "tag")) return $"The {table.Name} table has no \"EQUIP NO\" column.";
        return null;
    }

    private static void ReplacePlaceholders(XLWorkbook wb, string unitNumber, string reportDate)
    {
        foreach (var ws in wb.Worksheets.ToList())
        {
            if (ws.Name.Contains("{UNIT}", StringComparison.Ordinal)) ws.Name = ws.Name.Replace("{UNIT}", unitNumber);
            foreach (var cell in ws.Range("A1:Z3").CellsUsed())
            {
                if (cell.HasFormula || cell.Value.Type != XLDataType.Text) continue;
                var text = WorkbookFormat.CellText(cell);
                if (!text.Contains("{DATE}", StringComparison.Ordinal) && !text.Contains("{UNIT}", StringComparison.Ordinal)) continue;
                cell.Value = text.Replace("{DATE}", reportDate).Replace("{UNIT}", unitNumber);
            }
        }
    }
}
