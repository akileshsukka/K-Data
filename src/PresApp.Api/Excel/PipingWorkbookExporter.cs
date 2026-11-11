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

/// <summary>
/// Builds the piping line list of a unit from the project's piping template (<see cref="PipingTemplate"/>; the built-in
/// default when none was uploaded): one row per active line sorted by line number, the Summary model row copied once
/// per service code, the Summary notes, a hidden Key column and the very hidden _meta and _lists sheets. Circuits and
/// workpacks are not written. The values at export are kept with the session so the import only takes what changed.
/// </summary>
public sealed class PipingWorkbookExporter(PresAppDbContext db, TimeProvider clock)
{
    /// <param name="grouped">
    /// One row per size group (<see cref="LineNumber.GroupKey"/>): the line numbers in one cell, one per line, the P&amp;IDs
    /// listed together and each other value shown once when all the lines have it (<see cref="PipingLayout.Mixed"/> when
    /// not). The hidden Key holds the ids of all the lines of the row, so the import writes each line on its own.
    /// </param>
    public async Task<ExportResult> ExportAsync(int unitId, string exportedBy, CancellationToken ct, bool grouped = false)
    {
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, ct) ?? throw ApiProblem.NotFound("Unit not found.");
        var lines = (await db.Assets.AsNoTracking().Include(a => a.Preservation).Include(a => a.Pipeline).Include(a => a.FieldValues)
                .Where(a => a.UnitId == unitId && a.IsActive && a.Type == AssetType.Pipeline).ToListAsync(ct))
            .OrderBy(a => a.Tag ?? a.Code, NaturalComparer.Instance).ToList();
        var defs = await LoadFixedFieldsAsync(db, tracking: false, ct);
        var codes = await Piping.LineCodeMeanings.LoadAsync(db, ct);
        var options = await AssetEditor.LoadActiveOptionsAsync(db, ct);
        var notes = (await db.PipingNotes.AsNoTracking().Where(n => n.UnitId == unitId && n.IsActive).ToListAsync(ct))
            .ToDictionary(n => Piping.PipingNoteService.Key(n.NoteDate), n => n.Text);
        var templateBytes = await TemplateAsync(db, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, SiteClock.Dubai);
        var sessionId = Guid.NewGuid();
        var groups = grouped ? Group(lines) : lines.Select(a => (IReadOnlyList<Asset>)[a]).ToList();
        var n = groups.Count;

        using var wb = Open(templateBytes);
        var table = PipingTemplate.FindTable(wb)!;
        var ws = table.Worksheet;
        var headers = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c)).ToList();
        var map = PipingTemplate.Map(headers);
        var byIndex = map.ToDictionary(m => m.Value, m => m.Key);
        var customDefs = await LoadCustomFieldsAsync(db, tracking: false, ct);
        var customMap = PipingTemplate.MapCustom(headers, customDefs);
        var customByIndex = customMap.ToDictionary(m => m.Value, m => m.Key);
        var templateRow = table.DataRange.FirstRow();
        var formulas = templateRow.Cells().Select(c => c.HasFormula ? c.FormulaR1C1 : null).ToList();

        // ---------------------------------------------------------------- rows
        if (n > 1) table.DataRange.LastRow().InsertRowsBelow(n - 1);
        var body = table.DataRange;
        for (var i = 0; i < n; i++)
        {
            var g = groups[i];
            var a = g[0];
            string? Shown(Func<Asset, string?> pick, bool join = false) => PipingLayout.GroupText(g.Select(pick).ToList(), join);
            var row = body.Row(i + 1);
            var service = a.Pipeline?.Service ?? LineNumber.Parse(a.Tag)?.Service;
            for (var c = 0; c < headers.Count; c++)
            {
                var cell = row.Cell(c + 1);
                if (formulas[c] is { } f && !byIndex.ContainsKey(c) && !customByIndex.ContainsKey(c)) { cell.FormulaR1C1 = f; continue; }
                if (customByIndex.TryGetValue(c, out var customDef))
                {
                    cell.Style.NumberFormat.Format = "@";
                    cell.SetValue(Shown(x => CustomText(customDef, x)) ?? "");
                    continue;
                }
                if (!byIndex.TryGetValue(c, out var col)) { cell.Value = Blank.Value; continue; }
                switch (col.Role)
                {
                    case PipingRole.Serial: cell.Value = i + 1; break;
                    case PipingRole.Tag:
                        cell.SetValue(string.Join('\n', g.Select(x => x.Tag ?? x.Code)));
                        if (g.Count > 1) cell.Style.Alignment.WrapText = true;
                        break;
                    case PipingRole.ProcessCode: cell.SetValue(service ?? ""); break;
                    case PipingRole.ProcessDescription: cell.SetValue(codes.Service(service, unitId)?.ToUpperInvariant() ?? ""); break;
                    case PipingRole.N2Blanket: cell.SetValue(Shown(PipingLayout.N2Blanket) ?? ""); break;
                    default:
                        cell.Style.NumberFormat.Format = "@";
                        cell.SetValue(Shown(x => PipingLayout.Display(col, x, defs), PipingLayout.Joins(col.BaseKey)) ?? "");
                        if (g.Count > 1) cell.Style.Alignment.WrapText = true;
                        break;
                }
            }
        }

        // ---------------------------------------------------------------- hidden Key column (line ID)
        table.InsertColumnsAfter(1);
        var keyHeader = table.HeadersRow().LastCell();
        keyHeader.Value = ExcelColumns.KeyHeader;
        var keyColumn = keyHeader.Address.ColumnNumber;
        for (var i = 0; i < n; i++)
        {
            var cell = body.Row(i + 1).Cell(table.DataRange.ColumnCount());
            cell.Style.NumberFormat.Format = "@";
            cell.SetValue(string.Join(KeySeparator, groups[i].Select(a => a.Id.ToString())));
        }
        var keys = ws.Range(table.HeadersRow().RowNumber(), keyColumn, table.HeadersRow().RowNumber() + Math.Max(n, 1), keyColumn);
        keys.Style.Alignment.WrapText = false;   // a wrapped id makes every row tall
        keys.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.Column(keyColumn).Width = 38;
        ws.Column(keyColumn).Hide();

        // ---------------------------------------------------------------- dropdowns (very hidden _lists)
        var lists = wb.Worksheets.Add(WorkbookFormat.ListsSheet);
        var listColumn = 0;
        void Dropdown(string header, string name, IReadOnlyList<string> values, bool strict, string message)
        {
            listColumn++;
            lists.Cell(1, listColumn).SetValue(name);
            var col = PipingLayout.Columns.First(c => c.Header == header);
            if (values.Count == 0 || n == 0 || !map.TryGetValue(col, out var index)) return;
            for (var v = 0; v < values.Count; v++) lists.Cell(v + 2, listColumn).SetValue(values[v]);
            var dv = body.Column(index + 1).CreateDataValidation();
            dv.List(lists.Range(2, listColumn, values.Count + 1, listColumn), true);
            dv.IgnoreBlanks = true;
            dv.ShowErrorMessage = true;
            dv.ErrorStyle = strict ? XLErrorStyle.Stop : XLErrorStyle.Information;
            dv.ErrorTitle = strict ? "Invalid value" : "Current Activity";
            dv.ErrorMessage = message;
        }
        List<string> Options(string? set) => set is null ? [] : options.GetValueOrDefault(set) ?? [];
        var activity = PreservationFields.Find(nameof(AssetPreservation.CurrentActivity))!;
        Dropdown("Current Activity", "CurrentActivity", Options(activity.OptionSetFor(AssetType.Pipeline)), false,
            "Pick one activity, or write several, one per line (Alt+Enter). Each one must be in the K Data list.");
        Dropdown(PipingLayout.WalkdownHeader, "EMWalkdown", Options(OptionSetKeys.EmWalkdown), true, "Select a value from the list.");
        Dropdown(PipingLayout.DrainedHeader, "BulkLiquidDrained", ["Yes"], true, "Write Yes, or leave it empty.");
        Dropdown(PipingLayout.InspectionHeader, "InspectionDone",
            CustomOptions(defs, PipingFieldKeys.InspectionDone).Select(o => PipingLayout.CellText(PipingFieldKeys.InspectionDone, o)!).ToList(), true,
            "Select a value from the list (Yes, No or N/A).");
        Dropdown(PipingLayout.CleaningRequiredHeader, "CleaningRequired", CustomOptions(defs, PipingFieldKeys.CleaningRequired), true,
            "Select a value from the list. New values are added in K Data.");
        Dropdown(PipingLayout.CleanedHeader, "Cleaned", CustomOptions(defs, PipingFieldKeys.Cleaned), true, "Select a value from the list, or leave it empty.");
        // Custom fields with a list (or Yes/No): their values as dropdowns.
        foreach (var (def, index) in customMap)
        {
            var values = def.DataType == FieldDataType.YesNo ? ["Yes", "No"]
                : def.DataType == FieldDataType.List ? def.OptionSet?.Values.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => v.Value).ToList() ?? [] : [];
            if (values.Count == 0 || n == 0) continue;
            listColumn++;
            lists.Cell(1, listColumn).SetValue(def.Key);
            for (var v = 0; v < values.Count; v++) lists.Cell(v + 2, listColumn).SetValue(values[v]);
            var dv = body.Column(index + 1).CreateDataValidation();
            dv.List(lists.Range(2, listColumn, values.Count + 1, listColumn), true);
            dv.IgnoreBlanks = true;
            dv.ShowErrorMessage = true;
            dv.ErrorStyle = XLErrorStyle.Stop;
            dv.ErrorTitle = "Invalid value";
            dv.ErrorMessage = "Select a value from the list. New values are added in K Data (Custom fields).";
        }
        lists.Visibility = XLWorksheetVisibility.VeryHidden;

        // ---------------------------------------------------------------- Summary, notes and placeholders
        var serviceCodes = lines.Select(a => a.Pipeline?.Service ?? LineNumber.Parse(a.Tag)?.Service).OfType<string>()
            .Select(s => s.ToUpperInvariant()).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        ExpandCodeRow(wb, serviceCodes);
        WriteNotes(wb, notes);
        ReplacePlaceholders(wb, unit.Number.ToString(CultureInfo.InvariantCulture), local.Date);

        // ---------------------------------------------------------------- session sheet
        var meta = wb.Worksheets.Add(WorkbookFormat.MetaSheet);
        string[][] metaRows =
        [
            ["SessionId", sessionId.ToString()],
            ["Unit", unit.Name],
            ["Kind", PipingLayout.Kind],
            ["ExportedAtUtc", now.ToString("O", CultureInfo.InvariantCulture)],
            ["ExportedBy", exportedBy],
            ["FormatVersion", WorkbookFormat.FormatVersion.ToString(CultureInfo.InvariantCulture)],
            ["RowCount", n.ToString(CultureInfo.InvariantCulture)],
            ["Grouped", grouped ? "Yes" : "No"],
            ["App", WorkbookFormat.AppName]
        ];
        for (var r = 0; r < metaRows.Length; r++)
        {
            meta.Cell(r + 1, 1).SetValue(metaRows[r][0]);
            meta.Cell(r + 1, 2).SetValue(metaRows[r][1]);
        }
        meta.Visibility = XLWorksheetVisibility.VeryHidden;

        foreach (var w in wb.Worksheets) { w.TabActive = false; w.TabSelected = false; }
        ws.TabSelected = true;
        ws.SetTabActive();
        wb.FullCalculationOnLoad = true;

        var fileName = $"U{unit.Number} ALL PIPING LINES {(grouped ? "GROUPED " : "")}{local.ToString("dd MMM yyyy HHmm", CultureInfo.InvariantCulture).ToUpperInvariant()}.xlsx";
        using var output = new MemoryStream();
        wb.SaveAs(output);

        var finalHeaders = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c)).ToList();
        db.ExcelSessions.Add(new ExcelSession
        {
            Id = sessionId,
            UnitId = unitId,
            AssetType = AssetType.Pipeline,
            ExportedAtUtc = now,
            ExportedBy = exportedBy,
            Status = ExcelSessionStatus.Open,
            FormatVersion = WorkbookFormat.FormatVersion,
            FileName = fileName,
            RowCount = lines.Count,
            StructureSignature = new StructureSignature(table.Name, table.HeadersRow().RowNumber(),
                table.HeadersRow().FirstCell().Address.ColumnNumber, finalHeaders).ToJson(),
            BaseJson = new BaseSnapshot(lines.Select(a => new BaseRow(a.Id, a.Tag, a.Code,
                map.Keys.Where(c => c.Editable).Select(c => (c.BaseKey, PipingLayout.Display(c, a, defs)))
                    .Concat(customMap.Keys.Select(d => ("cf:" + d.Key, CustomText(d, a))))
                    .ToDictionary(x => x.Item1, x => x.Item2))).ToList(), notes).ToJson()
        });
        await db.SaveChangesAsync(ct);
        return new ExportResult(fileName, output.ToArray(), sessionId, n);
    }

    /// <summary>Between the line ids of a grouped row in the hidden Key column.</summary>
    public const char KeySeparator = ',';

    /// <summary>
    /// Lines in size groups, in the order of the list (a group where its first line is); inside a group by size.
    /// A line without a group key is a group of its own.
    /// </summary>
    public static List<IReadOnlyList<Asset>> Group(IReadOnlyList<Asset> lines) =>
        lines.Select((a, i) => (Asset: a, Key: LineNumber.GroupKey(a.Tag) ?? "#" + i))
            .GroupBy(x => x.Key)
            .Select(g => (IReadOnlyList<Asset>)g.Select(x => x.Asset)
                .OrderBy(a => a.Pipeline?.SizeIn ?? LineNumber.Parse(a.Tag)?.SizeIn ?? decimal.MaxValue)
                .ThenBy(a => a.Tag ?? a.Code, NaturalComparer.Instance).ToList())
            .ToList();

    // ------------------------------------------------------------------ template

    /// <summary>The uploaded piping template, or the built-in default with the current Cleaning Required values.</summary>
    public static async Task<byte[]> TemplateAsync(PresAppDbContext db, CancellationToken ct)
    {
        var uploaded = await db.WorkbookTemplates.AsNoTracking().Where(t => t.Key == WorkbookTemplate.PipingKey).Select(t => t.Content).FirstOrDefaultAsync(ct);
        if (uploaded is not null) return uploaded;
        return PipingTemplate.BuildDefault(CustomOptions(await LoadFixedFieldsAsync(db, tracking: false, ct), PipingFieldKeys.CleaningRequired));
    }

    public static XLWorkbook Open(byte[] content)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(new MemoryStream(content)); }
        catch (Exception) { throw ApiProblem.Invalid("The piping template could not be opened. Upload an .xlsx workbook."); }
        if (PipingTemplate.Problem(wb) is not { } problem) return wb;
        wb.Dispose();
        throw ApiProblem.Invalid(problem);
    }

    private static List<string> CustomOptions(IReadOnlyDictionary<string, FieldDefinition> defs, string key) => defs.TryGetValue(key, out var d)
        ? d.DataType == FieldDataType.YesNo ? ["Yes", "No"] : d.OptionSet?.Values.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => v.Value).ToList() ?? []
        : [];

    /// <summary>Active Pipeline custom fields other than the fixed ones (they become columns when the template has their name).</summary>
    internal static async Task<List<FieldDefinition>> LoadCustomFieldsAsync(PresAppDbContext db, bool tracking, CancellationToken ct)
    {
        var query = db.FieldDefinitions.Include(f => f.OptionSet).ThenInclude(o => o!.Values)
            .Where(f => f.AssetType == AssetType.Pipeline && f.IsActive && !PipingLayout.FixedKeys.Contains(f.Key));
        return await (tracking ? query : query.AsNoTracking()).ToListAsync(ct);
    }

    /// <summary>Value of a custom field of a line as written in the cell.</summary>
    internal static string? CustomText(FieldDefinition def, Asset a) =>
        a.FieldValues.FirstOrDefault(v => v.FieldDefinitionId == def.Id) is { } row ? CustomFields.CustomFieldService.Format(def, row) : null;

    /// <summary>The fixed custom fields of the Pipeline Register, by key.</summary>
    internal static async Task<Dictionary<string, FieldDefinition>> LoadFixedFieldsAsync(PresAppDbContext db, bool tracking, CancellationToken ct)
    {
        var query = db.FieldDefinitions.Include(f => f.OptionSet).ThenInclude(o => o!.Values)
            .Where(f => f.AssetType == AssetType.Pipeline && f.IsActive && PipingLayout.FixedKeys.Contains(f.Key));
        return await (tracking ? query : query.AsNoTracking()).ToDictionaryAsync(f => f.Key, ct);
    }

    /// <summary>
    /// The Summary row holding {CODE} in column B is copied once per service code (values, styles and formulas; relative
    /// references move with the row). No code: the row is removed.
    /// </summary>
    private static void ExpandCodeRow(XLWorkbook wb, IReadOnlyList<string> codes)
    {
        var model = wb.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible)
            .SelectMany(w => w.Column(2).CellsUsed()).FirstOrDefault(c => !c.HasFormula && WorkbookFormat.CellText(c).Trim() == PipingTemplate.CodePlaceholder);
        if (model is null) return;
        var s = model.Worksheet;
        var r = model.Address.RowNumber;
        var last = Math.Max(2, s.Row(r).LastCellUsed()?.Address.ColumnNumber ?? 2);
        var cells = Enumerable.Range(1, last).Select(c => s.Cell(r, c))
            .Select(c => (Formula: c.HasFormula ? c.FormulaR1C1 : null, Text: c.HasFormula ? null : WorkbookFormat.CellText(c), Style: c.Style)).ToList();
        var height = s.Row(r).Height;
        if (codes.Count == 0) { s.Row(r).Delete(); return; }
        if (codes.Count > 1) s.Row(r).InsertRowsBelow(codes.Count - 1);
        for (var i = 0; i < codes.Count; i++)
        {
            s.Row(r + i).Height = height;
            for (var c = 0; c < cells.Count; c++)
            {
                var cell = s.Cell(r + i, c + 1);
                cell.Style = cells[c].Style;
                if (cells[c].Formula is { } f) cell.FormulaR1C1 = f;
                else if (cells[c].Text is { Length: > 0 } t) cell.SetValue(t.Replace(PipingTemplate.CodePlaceholder, codes[i]));
                else cell.Value = Blank.Value;
            }
        }
    }

    /// <summary>
    /// Notes at {NOTES} (or two rows under the last used row of the Summary): the general note first, then each dated
    /// note as "27 Oct:" and its lines, one line per row. The start is kept as the name KD_Notes for the import.
    /// </summary>
    private static void WriteNotes(XLWorkbook wb, IReadOnlyDictionary<string, string> notes)
    {
        var start = wb.Worksheets.SelectMany(w => w.CellsUsed()).FirstOrDefault(c => !c.HasFormula && WorkbookFormat.CellText(c).Trim() == PipingTemplate.NotesPlaceholder);
        if (start is null)
        {
            if (!wb.TryGetWorksheet(PipingLayout.SummarySheet, out var summary)) return;
            start = summary.Cell((summary.LastRowUsed()?.RowNumber() ?? 0) + 2, 2);
        }
        start.Value = Blank.Value;
        var s = start.Worksheet;
        var column = start.Address.ColumnNumber;
        var row = start.Address.RowNumber;
        wb.DefinedNames.Add(PipingTemplate.NotesName, s.Range(start.Address, start.Address));
        void Lines(string text)
        {
            foreach (var line in text.Split('\n')) s.Cell(row++, column).SetValue(line);
        }
        if (notes.TryGetValue("", out var general)) { Lines(general); row++; }
        foreach (var (key, text) in notes.Where(n => n.Key.Length > 0).OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            s.Cell(row++, column).SetValue(Piping.PipingNoteService.Header(Piping.PipingNoteService.FromKey(key)!.Value));
            Lines(text);
        }
    }

    /// <summary>{UNIT} in sheet names and text cells; a cell that is only {DATE} becomes the date, {DATE} inside a text the date written.</summary>
    private static void ReplacePlaceholders(XLWorkbook wb, string unitNumber, DateTime date)
    {
        foreach (var ws in wb.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible).ToList())
        {
            if (ws.Name.Contains("{UNIT}", StringComparison.Ordinal)) ws.Name = ws.Name.Replace("{UNIT}", unitNumber);
            foreach (var cell in ws.CellsUsed().Where(c => !c.HasFormula && c.Value.Type == XLDataType.Text))
            {
                var text = cell.GetText();
                if (text.Trim() == "{DATE}") { cell.Value = date; continue; }
                if (!text.Contains("{DATE}", StringComparison.Ordinal) && !text.Contains("{UNIT}", StringComparison.Ordinal)) continue;
                cell.SetValue(text.Replace("{DATE}", date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)).Replace("{UNIT}", unitNumber));
            }
        }
    }
}
