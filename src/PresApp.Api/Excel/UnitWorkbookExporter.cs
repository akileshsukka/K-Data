using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Imports;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

public sealed record ExportResult(string FileName, byte[] Content, Guid SessionId, int Rows);

/// <summary>
/// Builds the client "Master Equipment List – Preservation" workbook of one unit from its template, as the
/// Power Automate flow did, but without sheet protection: the import checks the structure instead.
/// <list type="bullet">
/// <item>One row per active equipment (E&amp;I rows included but hidden by the filter), sorted like the register.</item>
/// <item>Hidden "Key" column = asset ID; very hidden <c>_meta</c> (session) and <c>_lists</c> (dropdowns).</item>
/// <item>Editable cells written as text so Excel never converts a value; list columns get dropdowns.</item>
/// <item>Categories table and the dashboard category block rebuilt from the unit's categories.</item>
/// </list>
/// The values at export and the table layout are stored with the session on the server.
/// </summary>
public sealed class UnitWorkbookExporter(PresAppDbContext db, TimeProvider clock)
{
    private static readonly HashSet<string> HiddenDisciplines = new(StringComparer.OrdinalIgnoreCase) { "Electrical", "Instrumentation", "E&I" };

    public async Task<ExportResult> ExportAsync(int unitId, string exportedBy, CancellationToken ct)
    {
        var unit = await db.Units.AsNoTracking().Include(u => u.Plant).FirstOrDefaultAsync(u => u.Id == unitId, ct)
                   ?? throw ApiProblem.NotFound("Unit not found.");
        var template = await db.ExcelTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.UnitId == unitId, ct)
                       ?? throw ApiProblem.Invalid($"There is no Excel template for {unit.Name} yet. An administrator can upload it in Excel templates.");

        var assets = (await db.Assets.AsNoTracking()
                .Include(a => a.Preservation).Include(a => a.Discipline)
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
        var table = WorkbookFormat.FindTable(wb, ExcelColumns.MasterTable)!;
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
                    case ColumnRole.OperatingUnit: cell.Value = unit.Number; break;
                    case ColumnRole.Discipline: cell.Value = a.Discipline.Name; break;
                    case ColumnRole.Tag: cell.Value = a.Tag ?? ""; break;
                    case ColumnRole.Description: cell.Value = a.Description ?? ""; break;
                    case ColumnRole.Formula: cell.FormulaR1C1 = formulas[col.Index]; break;
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

        // ---- E&I rows hidden by the filter (still in the file, not counted by the dashboard)
        var disciplineCol = columns.FirstOrDefault(c => c.Role == ColumnRole.Discipline);
        if (disciplineCol is not null && n > 0)
        {
            var all = assets.Select(a => a.Discipline.Name).Distinct().ToList();
            var keep = all.Where(d => !HiddenDisciplines.Contains(d.Trim())).ToList();
            if (keep.Count > 0 && keep.Count < all.Count)
            {
                var filter = table.AutoFilter.Column(disciplineCol.Index + 1);
                foreach (var d in keep) filter.AddFilter(d);
                for (var i = 0; i < n; i++)
                {
                    if (HiddenDisciplines.Contains(assets[i].Discipline.Name.Trim())) body.Row(i + 1).WorksheetRow().Hide();
                }
            }
        }

        // ---- dropdowns from the active options (very hidden _lists sheet)
        var lists = wb.Worksheets.Add(WorkbookFormat.ListsSheet);
        var listColumn = 0;
        foreach (var col in columns.Where(c => c.Editable && c.Field!.OptionSet is not null).GroupBy(c => c.Field!.OptionSet!))
        {
            var values = optionsBySet.GetValueOrDefault(col.Key) ?? [];
            listColumn++;
            lists.Cell(1, listColumn).Value = col.Key;
            if (values.Count == 0) continue;
            for (var v = 0; v < values.Count; v++) lists.Cell(v + 2, listColumn).SetValue(values[v]);
            var source = lists.Range(2, listColumn, values.Count + 1, listColumn);
            if (n == 0) continue;
            foreach (var c in col)
            {
                var dv = body.Column(c.Index + 1).CreateDataValidation();
                dv.List(source, true);
                dv.IgnoreBlanks = true;
                dv.ShowErrorMessage = true;
                dv.ErrorStyle = XLErrorStyle.Stop;
                dv.ErrorTitle = "Invalid value";
                dv.ErrorMessage = "Select a value from the list.";
            }
        }
        lists.Visibility = XLWorksheetVisibility.VeryHidden;

        // ---- placeholders, categories and dashboard
        var reportDate = local.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        ReplacePlaceholders(wb, unit.Number.ToString(CultureInfo.InvariantCulture), reportDate);
        DashboardSheetWriter.Write(wb, categories);

        // ---- session sheet
        var meta = wb.Worksheets.Add(WorkbookFormat.MetaSheet);
        string[][] metaRows =
        [
            ["SessionId", sessionId.ToString()],
            ["Unit", unit.Name],
            ["ExportedAtUtc", now.ToString("O", CultureInfo.InvariantCulture)],
            ["ExportedBy", exportedBy],
            ["FormatVersion", WorkbookFormat.FormatVersion.ToString(CultureInfo.InvariantCulture)],
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
        var finalHeaders = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c)).ToList();
        var editable = columns.Where(c => c.Editable).Select(c => c.Field!).ToList();
        db.ExcelSessions.Add(new ExcelSession
        {
            Id = sessionId,
            UnitId = unitId,
            AssetType = AssetType.Equipment,
            ExportedAtUtc = now,
            ExportedBy = exportedBy,
            Status = ExcelSessionStatus.Open,
            FormatVersion = WorkbookFormat.FormatVersion,
            FileName = fileName,
            RowCount = n,
            StructureSignature = new StructureSignature(table.Name, table.HeadersRow().RowNumber(),
                table.HeadersRow().FirstCell().Address.ColumnNumber, finalHeaders).ToJson(),
            BaseJson = new BaseSnapshot(assets.Select(a => new BaseRow(a.Id, a.Tag, a.Code,
                editable.ToDictionary(f => f.Key, f => FieldValues.Read(f, a)))).ToList()).ToJson()
        });
        await db.SaveChangesAsync(ct);

        return new ExportResult(fileName, output.ToArray(), sessionId, n);
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
        if (table.DataRange is null || table.DataRange.RowCount() < 1) return "The MasterList table must keep one empty data row.";
        var headers = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c)).ToList();
        if (headers.Any(h => ExcelColumns.Norm(h) == "key")) return "The template already has a \"Key\" column. Remove it: the app adds it.";
        if (!headers.Any(h => ExcelColumns.Norm(h) is "equipno" or "equipmentno" or "tag")) return "The MasterList table has no \"EQUIP NO\" column.";
        foreach (var name in new[] { WorkbookFormat.MetaSheet, WorkbookFormat.ListsSheet, WorkbookFormat.BaseSheet })
        {
            if (wb.TryGetWorksheet(name, out _)) return $"The template must not have a sheet named {name}. Upload the clean template, not an exported file.";
        }
        if (wb.Worksheets.Any(w => w.IsProtected)) return "The template has a protected sheet. Remove the protection and upload it again.";
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
