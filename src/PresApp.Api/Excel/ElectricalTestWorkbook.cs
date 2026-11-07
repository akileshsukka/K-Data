using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Common;
using PresApp.Domain.Imports;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

/// <summary>
/// The client's E7 electrical test sheet (one row per motor):
/// <list type="bullet">
/// <item><b>Report</b>: the sheet exactly as the client uses it, filled with the unit's assets that require the test,
/// or with the assets chosen in the register (one row each, in register order; a row stays empty where there is no test yet).</item>
/// <item><b>Upload</b>: the same sheet filled by the engineers; rows are matched by ITEM TAG among the unit's active assets.
/// Filled cells set the value; empty cells never clear anything; the DAR / PI ratio columns are calculated, never read.
/// DESCRIPTION fills the Nameplate Data. A loaded row marks the asset as requiring the test.</item>
/// </list>
/// Same two steps as the other uploads: preview, then apply against the data as it is at that moment.
/// </summary>
public sealed class ElectricalTestWorkbook(PresAppDbContext db, AuditContext audit, TimeProvider clock)
{
    private const string TemplateResource = "E7_Electrical_Test_Template.xlsx";
    private const int HeaderRow = 2;
    private const int FirstDataRow = 4;
    private const string RowField = "(row)";

    /// <summary>
    /// Question of the preview for a motor whose E7 test is complete (locked in the app) and the file changes values it
    /// already has. Decision by row: "replace" (yes, load the file values) or "keep" (no, the row's test values are not loaded).
    /// Filling values the test does not have yet (e.g. the optional PI) asks nothing.
    /// </summary>
    public const string ReplaceTestField = "(replace test)";

    /// <summary>
    /// Columns of the sheet from ITEM TAG (offset 0): DESCRIPTION, then each block's values and, after DAR / PI blocks, the ratio.
    /// Built from <see cref="ElectricalTestSpec.Blocks"/>, which follows the order of the sheet.
    /// </summary>
    private static readonly List<(int Offset, ElectricalMeasure? Measure, ElectricalBlock? RatioOf)> Layout = BuildLayout();

    /// <summary>
    /// Second header row of each measure column, to recognise the sheet. Compared normalised: letters and digits only,
    /// so the units ("U1-U2 / Ω", "30Sec / GΩ") do not matter.
    /// </summary>
    private static readonly Dictionary<string, string> SubHeaders = ElectricalTestSpec.Measures.ToDictionary(m => m.Key, m => m.Block switch
    {
        "Winding" or "Insulation" => ExcelColumns.Norm(m.Label.Split(' ')[^1]),          // "Winding U1-U2" → u1u2, "IR U-V" → uv
        _ when m.Block.StartsWith("Dar", StringComparison.Ordinal) => m.Key.EndsWith("30", StringComparison.Ordinal) ? "30secg" : "60secg",
        _ => m.Key.EndsWith("10", StringComparison.Ordinal) ? "10ming" : "1ming"
    });

    /// <summary>
    /// PI blocks added in Nov 2026 (V-W … W-G). A sheet downloaded before has a single PI block (now U-V) and these columns
    /// empty: they are then simply not read.
    /// </summary>
    private static bool AddedLater(ElectricalMeasure m) => m.Block.StartsWith("Pi", StringComparison.Ordinal) && m.Block != "PiUV";

    private sealed class Rejected(string message) : Exception(message);

    private static List<(int, ElectricalMeasure?, ElectricalBlock?)> BuildLayout()
    {
        var result = new List<(int, ElectricalMeasure?, ElectricalBlock?)>();
        var offset = 2;
        foreach (var block in ElectricalTestSpec.Blocks)
        {
            foreach (var m in block.Measures) result.Add((offset++, m, null));
            if (block.RatioLabel is not null) result.Add((offset++, null, block));
        }
        return result;
    }

    // ------------------------------------------------------------------ report

    public async Task<ExportResult> ExportAsync(int unitId, CancellationToken ct, IReadOnlyCollection<Guid>? assetIds = null)
    {
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, ct) ?? throw ApiProblem.NotFound("Unit not found.");
        if (assetIds is { Count: 0 }) throw ApiProblem.Invalid("Select at least one equipment.");
        if (assetIds is { Count: > 2000 }) throw ApiProblem.Invalid("Select at most 2,000 equipment.");
        var query = db.Assets.AsNoTracking().Include(a => a.ElectricalTest).Include(a => a.Discipline)
            .Where(a => a.UnitId == unitId && a.Type != AssetType.Pipeline);
        query = assetIds is null
            ? query.Where(a => a.IsActive && a.ElectricalTest != null && a.ElectricalTest.Required)
            : query.Where(a => assetIds.Contains(a.Id));
        var assets = (await query.ToListAsync(ct))
            .OrderBy(a => AssetQueries.DisciplineRank(a.Discipline.Name)).ThenBy(a => a.Tag ?? a.Code, NaturalComparer.Instance)
            .ToList();

        using var wb = OpenTemplate();
        var ws = wb.Worksheet(1);
        var lastColumn = 1 + Layout.Max(l => l.Offset);
        if (assets.Count > 1) ws.Row(FirstDataRow).InsertRowsBelow(assets.Count - 1);
        for (var i = 0; i < assets.Count; i++)
        {
            var a = assets[i];
            var t = a.ElectricalTest ?? new ElectricalTest();
            var row = FirstDataRow + i;
            if (i > 0)
            {
                for (var c = 1; c <= lastColumn; c++) ws.Cell(row, c).Style = ws.Cell(FirstDataRow, c).Style;
                ws.Row(row).Height = ws.Row(FirstDataRow).Height;
            }
            ws.Cell(row, 1).SetValue(a.Tag ?? a.Code);
            ws.Cell(row, 2).SetValue(a.NameplateData ?? "");
            foreach (var (offset, measure, ratioOf) in Layout)
            {
                var value = measure is not null ? ElectricalTestSpec.Get(t, measure.Key) : ElectricalTestSpec.Ratio(t, ratioOf!);
                if (value is { } v) ws.Cell(row, 1 + offset).SetValue(v);
            }
        }

        // Only the printed equipment: rows the template had formatted below them (empty borders and fills) are cleared.
        var lastData = FirstDataRow + Math.Max(assets.Count, 1) - 1;
        var lastFormatted = ws.LastRowUsed(XLCellsUsedOptions.All)?.RowNumber() ?? lastData;
        if (lastFormatted > lastData)
        {
            ws.Range(lastData + 1, 1, lastFormatted, Math.Max(lastColumn, ws.LastColumnUsed(XLCellsUsedOptions.All)?.ColumnNumber() ?? lastColumn))
                .Clear(XLClearOptions.All);
            for (var r = lastData + 1; r <= lastFormatted; r++) ws.Row(r).ClearHeight();
        }

        // Opens at A1 with ITEM TAG / DESCRIPTION and the three header rows frozen, whatever scroll position the template was saved with
        // (a template saved scrolled to column E would otherwise freeze C:D and hide A:B).
        ws.SheetView.TopLeftCellAddress = ws.Cell(1, 1).Address;
        ws.SheetView.Freeze(FirstDataRow - 1, 2);
        ws.SelectedRanges.RemoveAll();
        ws.Cell(FirstDataRow, 1).SetActive();
        ws.Cell(FirstDataRow, 1).Select();

        var local = TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, SiteClock.Dubai);
        var fileName = $"Unit_{unit.Number}_E7_Electrical_Test_{local.ToString("dd_MMM_yyyy_HHmm", CultureInfo.InvariantCulture)}.xlsx";
        using var output = new MemoryStream();
        wb.SaveAs(output);
        return new ExportResult(fileName, output.ToArray(), Guid.Empty, assets.Count);
    }

    private static XLWorkbook OpenTemplate()
    {
        using var stream = typeof(ElectricalTestWorkbook).Assembly.GetManifestResourceStream(TemplateResource)
                           ?? throw new InvalidOperationException("The E7 electrical test template is missing from the build.");
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
    }

    // ------------------------------------------------------------------ upload

    private sealed record SourceRow(int Number, string Tag, string? Nameplate, Dictionary<ElectricalMeasure, string> Values);

    public async Task<ImportRunDetail> PreviewAsync(Stream file, string fileName, int unitId, string uploadedBy, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > ExcelImportService.MaxFileBytes) throw ApiProblem.Invalid("Upload an .xlsx file of at most 20 MB.");
        var bytes = buffer.ToArray();
        var run = new ImportRun
        {
            Kind = ImportKind.ElectricalTest, FileName = Path.GetFileName(fileName), UnitId = unitId,
            UploadedBy = uploadedBy, UploadedAtUtc = clock.GetUtcNow().UtcDateTime, Status = ImportStatus.Previewed
        };
        try
        {
            var rows = Read(bytes);
            var changes = await AnalyseAsync(rows, unitId, apply: false, ct);
            Fill(run, rows.Count, changes);
            db.ImportFiles.Add(new ImportFile { ImportRun = run, Content = bytes });
        }
        catch (Rejected r)
        {
            run.Status = ImportStatus.Rejected;
            run.Message = r.Message;
        }
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return await ImportQueries.DetailAsync(db, run.Id, ct);
    }

    public async Task<ImportRunDetail> ApplyAsync(long runId, string appliedBy, CancellationToken ct, IReadOnlyDictionary<int, string>? decisions = null)
    {
        var run = await db.ImportRuns.Include(r => r.Changes).FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw ApiProblem.NotFound("Import not found.");
        if (run.Status != ImportStatus.Previewed) throw ApiProblem.Invalid($"This import is {ExcelImportService.Text(run.Status)}; it cannot be applied.");
        var file = await db.ImportFiles.AsNoTracking().FirstOrDefaultAsync(f => f.ImportRunId == runId, ct)
                   ?? throw ApiProblem.Invalid("The file of this preview is no longer available. Upload it again.");

        audit.Source = ChangeSource.Excel;
        audit.BatchId = Guid.NewGuid();
        audit.UserName = appliedBy;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = Read(file.Content);
        var changes = await AnalyseAsync(rows, run.UnitId ?? 0, apply: true, ct, decisions);
        db.ImportChanges.RemoveRange(run.Changes);
        run.Changes = [];
        Fill(run, rows.Count, changes);
        run.AppliedAtUtc = clock.GetUtcNow().UtcDateTime;
        run.Status = run.IssueCount > 0 ? ImportStatus.AppliedWithIssues : ImportStatus.Applied;
        run.Message = $"Applied by {appliedBy}.";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ImportQueries.DetailAsync(db, runId, ct);
    }

    private static List<SourceRow> Read(byte[] bytes)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(new MemoryStream(bytes)); }
        catch (Exception) { throw new Rejected("The file could not be opened as an Excel workbook (.xlsx)."); }
        using (wb)
        {
            foreach (var ws in wb.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible))
            {
                var header = ws.Range("A1:J10").CellsUsed().FirstOrDefault(c => ExcelColumns.Norm(WorkbookFormat.CellText(c)) == "itemtag");
                if (header is null) continue;
                var (top, left) = (header.Address.RowNumber, header.Address.ColumnNumber);
                var absent = new HashSet<ElectricalMeasure>();
                var matches = true;
                foreach (var (offset, measure, _) in Layout)
                {
                    if (measure is null) continue;
                    var got = ExcelColumns.Norm(WorkbookFormat.CellText(ws.Cell(top + 1, left + offset)));
                    if (got == SubHeaders[measure.Key]) continue;
                    if (got.Length == 0 && AddedLater(measure)) absent.Add(measure);
                    else matches = false;
                }
                if (!matches)
                    throw new Rejected("The columns of the sheet do not match the E7 electrical test sheet (ITEM TAG, DESCRIPTION, winding U1-U2…, IR U-V…, DAR 30Sec / 60Sec…, PI 1 Min / 10 Min). Download the E7 report and fill it in.");

                var result = new List<SourceRow>();
                var last = ws.LastRowUsed()?.RowNumber() ?? top;
                for (var r = top + 2; r <= last; r++)
                {
                    var tag = ValueNormalizer.CleanText(WorkbookFormat.CellText(ws.Cell(r, left)));
                    if (tag is null) continue;
                    var values = new Dictionary<ElectricalMeasure, string>();
                    foreach (var (offset, measure, _) in Layout)
                    {
                        if (measure is null || absent.Contains(measure)) continue;
                        var text = ValueNormalizer.CleanText(WorkbookFormat.CellText(ws.Cell(r, left + offset)));
                        if (text is not null) values[measure] = text;
                    }
                    result.Add(new SourceRow(r - top - 1, tag, ValueNormalizer.CleanText(WorkbookFormat.CellText(ws.Cell(r, left + 1))), values));
                }
                return result;
            }
        }
        throw new Rejected("No E7 electrical test sheet was found (a visible sheet with an \"ITEM TAG\" header in its first rows).");
    }

    private async Task<List<ImportChange>> AnalyseAsync(List<SourceRow> rows, int unitId, bool apply, CancellationToken ct, IReadOnlyDictionary<int, string>? decisions = null)
    {
        var query = db.Assets.Include(a => a.ElectricalTest).Where(a => a.UnitId == unitId && a.IsActive && a.Tag != null);
        var assets = await (apply ? query : query.AsNoTracking()).ToListAsync(ct);
        var byTag = assets.GroupBy(a => a.Tag!.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var nameplateField = PreservationFields.Find(nameof(Asset.NameplateData))!;
        var changes = new List<ImportChange>();
        var seen = new HashSet<Guid>();

        foreach (var row in rows)
        {
            ImportChange Change(Asset? a, string field, ImportOutcome outcome, string? reason, string? excel = null, string? current = null, string? newValue = null) => new()
            {
                AssetId = a?.Id, AssetCode = a?.Code, AssetTag = Truncate(a?.Tag ?? row.Tag, 55), RowNumber = row.Number,
                Field = field, ExcelValue = Truncate(excel, 4000), CurrentValue = Truncate(current, 4000), NewValue = Truncate(newValue, 4000), Outcome = outcome, Reason = reason
            };

            if (!byTag.TryGetValue(row.Tag, out var matches))
            {
                changes.Add(Change(null, RowField, ImportOutcome.Rejected, "No active equipment of this unit has this tag. Create it first (or load it with Onboarding)."));
                continue;
            }
            if (matches.Count > 1)
            {
                changes.Add(Change(null, RowField, ImportOutcome.Rejected, $"{matches.Count} active items of this unit have this tag; the row cannot be matched. Fix the tags in the app."));
                continue;
            }
            var asset = matches[0];
            if (asset.Type == AssetType.Pipeline) { changes.Add(Change(asset, RowField, ImportOutcome.Rejected, "This tag is a line; lines have no electrical test.")); continue; }
            if (!seen.Add(asset.Id)) { changes.Add(Change(asset, RowField, ImportOutcome.Rejected, "The tag appears twice in the file; only its first row was read.")); continue; }

            var test = asset.ElectricalTest;
            var proposed = new Dictionary<ElectricalMeasure, decimal>();
            var testChanges = new List<ImportChange>();
            var overwritten = 0;
            foreach (var (measure, text) in row.Values)
            {
                var current = test is null ? null : ElectricalTestSpec.Get(test, measure.Key);
                var currentText = current?.ToString(CultureInfo.InvariantCulture);
                if (!ValueNormalizer.TryParseDecimal(text, out var value) || value < 0)
                {
                    testChanges.Add(Change(asset, measure.Key, ImportOutcome.Invalid, $"{measure.Label} must be a number (0 or more).", text, currentText));
                    continue;
                }
                if (current == value) { testChanges.Add(Change(asset, measure.Key, ImportOutcome.NoChange, "The app already has this value.", text, currentText, currentText)); continue; }
                proposed[measure] = value;
                if (current is not null) overwritten++;
                testChanges.Add(Change(asset, measure.Key, ImportOutcome.Applied, null, text, currentText, value.ToString(CultureInfo.InvariantCulture)));
            }

            // A complete test is locked in the app: changing its values from the file needs a yes / no in the preview.
            if (overwritten > 0 && ElectricalTestSpec.IsComplete(test))
            {
                var about = $"This motor already has a complete E7 test{(test!.TestedOn is { } on ? $" of {on:dd MMM yyyy}" : "")}, saved by {test.UpdatedBy}. "
                            + $"The file changes {overwritten} of its value{(overwritten == 1 ? "" : "s")}.";
                var current = $"Test saved by {test.UpdatedBy}";
                var excel = $"{overwritten} value{(overwritten == 1 ? "" : "s")} changed";
                switch (decisions?.GetValueOrDefault(row.Number))
                {
                    case "replace":
                        changes.Add(Change(asset, ReplaceTestField, ImportOutcome.Applied, about + " Yes, chosen in the preview: the file values replace those of the current test.", excel, current, "Replaced"));
                        break;
                    case "keep":
                        changes.Add(Change(asset, ReplaceTestField, ImportOutcome.NoChange, about + " No, chosen in the preview: the current test was kept and the test values of this row were not loaded.", excel, current, "Kept"));
                        testChanges.Clear();
                        proposed.Clear();
                        break;
                    default:
                        changes.Add(Change(asset, ReplaceTestField, ImportOutcome.Conflict, about + (apply
                            ? " Not loaded: no choice was made in the preview. Check the values and upload the file again."
                            : " Choose whether the file replaces it."), excel, current));
                        if (apply)
                        {
                            testChanges.RemoveAll(c => c.Outcome == ImportOutcome.Applied);
                            proposed.Clear();
                        }
                        else foreach (var c in testChanges.Where(c => c.Outcome == ImportOutcome.Applied)) c.Reason = "Loaded only if you answer Yes.";
                        break;
                }
            }
            changes.AddRange(testChanges);

            string? nameplate = null;
            if (row.Nameplate is { } np)
            {
                if (np.Length > Asset.NameplateMaxLength)
                    changes.Add(Change(asset, nameplateField.Key, ImportOutcome.Invalid, $"{nameplateField.Label} allows at most {Asset.NameplateMaxLength} characters.", np, asset.NameplateData));
                else if (np == asset.NameplateData) changes.Add(Change(asset, nameplateField.Key, ImportOutcome.NoChange, "The app already has this value.", np, np, np));
                else
                {
                    nameplate = np;
                    changes.Add(Change(asset, nameplateField.Key, ImportOutcome.Applied, null, np, asset.NameplateData, np));
                }
            }

            var activate = row.Values.Count > 0 && test is not { Required: true };
            if (activate)
                changes.Add(Change(asset, nameof(ElectricalTest.Required), ImportOutcome.Derived, "The file has test values for it: the electrical test is marked as required.", current: "No", newValue: "Yes"));

            if (!apply) continue;
            if (nameplate is not null) asset.NameplateData = nameplate;
            if (proposed.Count == 0 && !activate) continue;
            if (test is null)
            {
                test = new ElectricalTest { AssetId = asset.Id };
                asset.ElectricalTest = test;
                db.ElectricalTests.Add(test);
            }
            if (activate) test.Required = true;
            foreach (var (measure, value) in proposed) ElectricalTestSpec.Set(test, measure.Key, value);
        }
        return changes;
    }

    private static void Fill(ImportRun run, int rowCount, List<ImportChange> changes)
    {
        foreach (var c in changes)
        {
            c.Resolution = c.Outcome is ImportOutcome.Invalid or ImportOutcome.Conflict or ImportOutcome.Rejected or ImportOutcome.Warning
                ? IssueResolution.Open : IssueResolution.None;
            run.Changes.Add(c);
        }
        run.RowsInFile = rowCount;
        run.AppliedCount = changes.Count(c => c.Outcome is ImportOutcome.Applied or ImportOutcome.Derived);
        run.IssueCount = changes.Count(c => c.Resolution == IssueResolution.Open && c.Outcome != ImportOutcome.Warning);
        run.WarningCount = changes.Count(c => c.Outcome == ImportOutcome.Warning);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
