using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Api.Monitoring;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Common;
using PresApp.Domain.Imports;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

/// <summary>
/// Monitoring readings in bulk, from a simple sheet that is downloaded once and reused:
/// <c>Tag | Measured date | Pressure (barg) | the other readings… | Note</c>, one row per asset and date.
/// <list type="bullet">
/// <item>Rows are matched by tag (or Equipment ID) among the unit's active assets, in any order. Case, extra spaces and the kind
/// of dash do not matter. The same asset may appear on several rows with different dates.</item>
/// <item>Pressure goes to G4 - N2 Blanket while the asset is DURING at that date and to Pressure (barg) once it is POST
/// (<see cref="MonitoringService.PressureKeyFor"/>).</item>
/// <item>Empty cells are ignored. Text is not loaded (reported). Zero is loaded but reported as a warning to check.
/// A reading already in the app (same asset, measurement, time and value) is not loaded again.</item>
/// <item>A date without a time is taken at 12:00 Dubai time.</item>
/// </list>
/// Same two steps as the other uploads: preview, then apply against the data as it is at that moment.
/// </summary>
public sealed partial class ReadingsWorkbook(PresAppDbContext db, AssetEditor editor, AuditContext audit, TimeProvider clock)
{
    private const string RowField = "(row)";
    private const string TagHeader = "Tag";
    private const string DateHeader = "Measured date";
    private const string PressureHeader = "Pressure (barg)";
    private const string NoteHeader = "Note";
    private const int HeaderRow = 4;
    private static readonly TimeSpan DefaultTime = TimeSpan.FromHours(12);

    private sealed class Rejected(string message) : Exception(message);

    /// <summary>A value column: the routed pressure (<c>Type</c> null) or a measurement type.</summary>
    private sealed record ValueColumn(int Column, MeasurementType? Type, string Header);

    private sealed record SourceRow(int Number, string Tag, string? Date, DateTime? DateValue, Dictionary<ValueColumn, string> Values, string? Note);

    private sealed record Sheet(List<SourceRow> Rows, List<string> Ignored);

    /// <summary>The other readings of the sheet (pressure has its own column): active types except Pressure and N2 Blanket.</summary>
    private async Task<List<MeasurementType>> OtherTypesAsync(CancellationToken ct) =>
        (await db.MeasurementTypes.AsNoTracking().Where(t => t.IsActive && t.Key != MonitoringService.N2Key && t.Key != MonitoringService.PressureKey)
            .ToListAsync(ct)).OrderBy(t => t.SortOrder).ToList();

    // ------------------------------------------------------------------ template

    public async Task<ExportResult> TemplateAsync(int unitId, CancellationToken ct)
    {
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, ct) ?? throw ApiProblem.NotFound("Unit not found.");
        var others = await OtherTypesAsync(ct);
        var headers = new List<string> { TagHeader, DateHeader, PressureHeader };
        headers.AddRange(others.Select(t => t.Label));
        headers.Add(NoteHeader);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Readings");
        ws.Cell(1, 1).SetValue($"Monitoring readings — {unit.Name}").Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(2, 1).SetValue("One row per asset and date. Paste the tags, the measured date and the values; leave empty what was not measured. " +
                               "Pressure is saved as G4 - N2 Blanket before the asset is marked as preserved, and as Pressure (barg) after.");
        ws.Cell(3, 1).SetValue("A date without a time is saved at 12:00 Dubai time. Text and empty cells are not loaded. You can reuse this sheet: clear the rows and paste the next round.");
        ws.Range(2, 1, 3, 1).Style.Font.SetFontColor(XLColor.FromHtml("#555555"));

        for (var c = 0; c < headers.Count; c++)
        {
            var cell = ws.Cell(HeaderRow, c + 1);
            cell.SetValue(headers[c]);
            cell.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#E8E3F5"))
                .Border.SetBottomBorder(XLBorderStyleValues.Thin).Alignment.SetWrapText();
        }
        const int lastRow = HeaderRow + 500;
        ws.Range(HeaderRow + 1, 1, lastRow, 1).Style.NumberFormat.SetFormat("@");
        var dates = ws.Range(HeaderRow + 1, 2, lastRow, 2);
        dates.Style.NumberFormat.SetFormat("dd-mmm-yy");
        dates.CreateDataValidation().Date.GreaterThan(new DateTime(2020, 1, 1));
        ws.Range(HeaderRow + 1, 3, lastRow, headers.Count - 1).Style.NumberFormat.SetFormat("0.###");
        ws.Column(1).Width = 32;
        ws.Column(2).Width = 15;
        for (var c = 3; c < headers.Count; c++) ws.Column(c).Width = 16;
        ws.Column(headers.Count).Width = 40;
        ws.Row(HeaderRow).Height = 32;
        ws.SheetView.Freeze(HeaderRow, 1);
        ws.Cell(HeaderRow + 1, 1).SetActive();

        var fileName = $"Unit_{unit.Number}_Readings_Template.xlsx";
        using var output = new MemoryStream();
        wb.SaveAs(output);
        return new ExportResult(fileName, output.ToArray(), Guid.Empty, 0);
    }

    // ------------------------------------------------------------------ upload

    public async Task<ImportRunDetail> PreviewAsync(Stream file, string fileName, int unitId, ClaimsPrincipal user, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > ExcelImportService.MaxFileBytes) throw ApiProblem.Invalid("Upload an .xlsx file of at most 20 MB.");
        if (!await db.Units.AnyAsync(u => u.Id == unitId, ct)) throw ApiProblem.NotFound("Unit not found.");
        var bytes = buffer.ToArray();
        var run = new ImportRun
        {
            Kind = ImportKind.Readings, FileName = Path.GetFileName(fileName), UnitId = unitId,
            UploadedBy = user.Identity?.Name ?? "unknown", UploadedAtUtc = clock.GetUtcNow().UtcDateTime, Status = ImportStatus.Previewed
        };
        try
        {
            var sheet = Read(bytes, await OtherTypesAsync(ct));
            var changes = await AnalyseAsync(sheet.Rows, unitId, null, ct);
            Fill(run, sheet.Rows.Count, changes);
            run.Message = IgnoredMessage(sheet.Ignored);
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

    public async Task<ImportRunDetail> ApplyAsync(long runId, ClaimsPrincipal user, CancellationToken ct)
    {
        var run = await db.ImportRuns.Include(r => r.Changes).FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw ApiProblem.NotFound("Import not found.");
        if (run.Status != ImportStatus.Previewed) throw ApiProblem.Invalid($"This import is {ExcelImportService.Text(run.Status)}; it cannot be applied.");
        var file = await db.ImportFiles.AsNoTracking().FirstOrDefaultAsync(f => f.ImportRunId == runId, ct)
                   ?? throw ApiProblem.Invalid("The file of this preview is no longer available. Upload it again.");

        var by = user.Identity?.Name ?? "unknown";
        audit.Source = ChangeSource.Excel;
        audit.BatchId = Guid.NewGuid();
        audit.UserName = by;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sheet = Read(file.Content, await OtherTypesAsync(ct));
        var changes = await AnalyseAsync(sheet.Rows, run.UnitId ?? 0, user, ct);
        db.ImportChanges.RemoveRange(run.Changes);
        run.Changes = [];
        Fill(run, sheet.Rows.Count, changes);
        run.AppliedAtUtc = clock.GetUtcNow().UtcDateTime;
        run.Status = run.IssueCount > 0 ? ImportStatus.AppliedWithIssues : ImportStatus.Applied;
        run.Message = $"Applied by {by}. {IgnoredMessage(sheet.Ignored)}".Trim();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ImportQueries.DetailAsync(db, runId, ct);
    }

    private static string? IgnoredMessage(List<string> ignored) => ignored.Count == 0 ? null
        : $"Columns not read: {string.Join(", ", ignored.Take(10))}{(ignored.Count > 10 ? $" and {ignored.Count - 10} more" : "")}.";

    private static Sheet Read(byte[] bytes, List<MeasurementType> others)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(new MemoryStream(bytes)); }
        catch (Exception) { throw new Rejected("The file could not be opened as an Excel workbook (.xlsx)."); }
        using (wb)
        {
            foreach (var ws in wb.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible))
            {
                var header = ws.Range("A1:Z15").CellsUsed().FirstOrDefault(c => ExcelColumns.Norm(WorkbookFormat.CellText(c)) == "tag");
                if (header is null) continue;
                var top = header.Address.RowNumber;
                var lastColumn = ws.Row(top).LastCellUsed()?.Address.ColumnNumber ?? header.Address.ColumnNumber;

                int? dateColumn = null, noteColumn = null;
                var values = new List<ValueColumn>();
                var ignored = new List<string>();
                for (var c = header.Address.ColumnNumber + 1; c <= lastColumn; c++)
                {
                    var text = WorkbookFormat.CellText(ws.Cell(top, c)).Trim();
                    var norm = ExcelColumns.Norm(text);
                    if (norm.Length == 0) continue;
                    if (norm is "measureddate" or "measuredat" or "date") dateColumn ??= c;
                    else if (norm is "note" or "notes" or "comment" or "comments") noteColumn ??= c;
                    else if (norm is "pressurebarg" or "pressure") values.Add(new ValueColumn(c, null, PressureHeader));
                    else if (others.FirstOrDefault(t => ExcelColumns.Norm(t.Label) == norm || ExcelColumns.Norm(t.Key) == norm) is { } type)
                        values.Add(new ValueColumn(c, type, type.Label));
                    else ignored.Add(text);
                }
                if (dateColumn is null) throw new Rejected($"The sheet \"{ws.Name}\" has a Tag column but no \"{DateHeader}\" column. Download the readings template and paste the values in it.");
                if (values.Count == 0) throw new Rejected($"The sheet \"{ws.Name}\" has no reading columns ({PressureHeader}, {string.Join(", ", others.Select(t => t.Label))}). Download the readings template.");
                if (values.GroupBy(v => v.Type?.Key).Any(g => g.Count() > 1)) throw new Rejected("A reading column appears twice in the sheet.");

                var rows = new List<SourceRow>();
                var last = ws.LastRowUsed()?.RowNumber() ?? top;
                for (var r = top + 1; r <= last; r++)
                {
                    var tag = ValueNormalizer.CleanText(WorkbookFormat.CellText(ws.Cell(r, header.Address.ColumnNumber)));
                    if (tag is null) continue;
                    var cells = new Dictionary<ValueColumn, string>();
                    foreach (var v in values)
                    {
                        var text = ValueNormalizer.CleanText(WorkbookFormat.CellText(ws.Cell(r, v.Column)));
                        if (text is not null) cells[v] = text;
                    }
                    var dateCell = ws.Cell(r, dateColumn.Value);
                    var dateValue = dateCell.Value.Type == XLDataType.DateTime ? dateCell.Value.GetDateTime() : (DateTime?)null;
                    var note = noteColumn is { } n ? ValueNormalizer.CleanText(WorkbookFormat.CellText(ws.Cell(r, n))) : null;
                    rows.Add(new SourceRow(r, tag, ValueNormalizer.CleanText(WorkbookFormat.CellText(dateCell)), dateValue, cells, note));
                }
                return new Sheet(rows, ignored);
            }
        }
        throw new Rejected("No readings sheet was found (a visible sheet with a \"Tag\" header in its first rows). Download the readings template and paste the values in it.");
    }

    /// <summary>
    /// The preview of every cell; with a <paramref name="user"/> the readings are also saved.
    /// Runs on the data as it is now, so a preview applied later is checked again.
    /// </summary>
    private async Task<List<ImportChange>> AnalyseAsync(List<SourceRow> rows, int unitId, ClaimsPrincipal? user, CancellationToken ct)
    {
        var assets = await db.Assets.AsNoTracking().Where(a => a.UnitId == unitId && a.IsActive)
            .Select(a => new { a.Id, a.Code, a.Tag, P = a.Preservation }).ToListAsync(ct);
        var byKey = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var a in assets)
            foreach (var key in new[] { a.Tag, a.Code }.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => TagKey(k!)).Distinct())
                (byKey.TryGetValue(key, out var list) ? list : byKey[key] = []).Add(a.Id);
        var byId = assets.ToDictionary(a => a.Id);

        var types = await db.MeasurementTypes.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
        var typeByKey = types.ToDictionary(t => t.Key);
        if (!typeByKey.ContainsKey(MonitoringService.N2Key) || !typeByKey.ContainsKey(MonitoringService.PressureKey))
            throw ApiProblem.Invalid("The pressure measurements (Pressure and G4 - N2 Blanket) are not active in the catalog.");

        var now = clock.GetUtcNow().UtcDateTime;
        var matched = rows.Select(r => byKey.GetValueOrDefault(TagKey(r.Tag))).Where(l => l is { Count: 1 }).Select(l => l![0]).Distinct().ToList();
        var existing = (await db.Readings.AsNoTracking().Where(r => matched.Contains(r.AssetId) && r.MeasuredAtUtc >= now.AddYears(-2))
                .Select(r => new { r.AssetId, r.MeasurementTypeId, r.MeasuredAtUtc, r.Value }).ToListAsync(ct))
            .Select(r => (r.AssetId, r.MeasurementTypeId, r.MeasuredAtUtc, r.Value)).ToHashSet();

        var changes = new List<ImportChange>();
        var seen = new HashSet<(Guid, DateTime)>();
        foreach (var row in rows)
        {
            ImportChange Change(Guid? assetId, string field, ImportOutcome outcome, string? reason, string? excel = null, string? current = null, string? newValue = null)
            {
                var a = assetId is { } id ? byId[id] : null;
                return new()
                {
                    AssetId = a?.Id, AssetCode = a?.Code, AssetTag = Truncate(a?.Tag ?? row.Tag, 55), RowNumber = row.Number,
                    Field = field, ExcelValue = Truncate(excel, 4000), CurrentValue = Truncate(current, 4000), NewValue = Truncate(newValue, 4000),
                    Outcome = outcome, Reason = reason
                };
            }

            if (row.Values.Count == 0) continue;   // a pasted tag with nothing measured
            var ids = byKey.GetValueOrDefault(TagKey(row.Tag));
            if (ids is null)
            {
                changes.Add(Change(null, RowField, ImportOutcome.Rejected, "No active item of this unit has this tag or Equipment ID.", row.Tag));
                continue;
            }
            if (ids.Count > 1)
            {
                changes.Add(Change(null, RowField, ImportOutcome.Rejected, $"{ids.Count} active items of this unit have this tag; the row cannot be matched. Fix the tags in the app.", row.Tag));
                continue;
            }
            var asset = byId[ids[0]];
            if (!TryMeasuredAt(row, out var measuredAt))
            {
                changes.Add(Change(asset.Id, RowField, ImportOutcome.Rejected,
                    row.Date is null ? "The measured date is empty; the row was not loaded." : "The measured date is not a date (use for example 06-Nov-26 or 06/11/2026); the row was not loaded.", row.Date));
                continue;
            }
            if (measuredAt > now.AddMinutes(10) || measuredAt < now.AddYears(-2))
            {
                changes.Add(Change(asset.Id, RowField, ImportOutcome.Rejected,
                    measuredAt > now ? "The measured date is in the future; the row was not loaded." : "The measured date is more than two years ago; the row was not loaded.", row.Date));
                continue;
            }
            if (!seen.Add((asset.Id, measuredAt)))
            {
                changes.Add(Change(asset.Id, RowField, ImportOutcome.Rejected, "The asset appears twice with the same date; only its first row was read.", row.Date));
                continue;
            }
            if (row.Note?.Length > 4000)
            {
                changes.Add(Change(asset.Id, RowField, ImportOutcome.Rejected, "The note allows at most 4,000 characters; the row was not loaded."));
                continue;
            }

            var when = TimeZoneInfo.ConvertTimeFromUtc(measuredAt, SiteClock.Dubai).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
            var preservedAt = asset.P?.PreservedAtUtc;
            var phase = preservedAt is { } s && measuredAt >= s ? "POST" : "DURING";
            var toSave = new List<ReadingValue>();
            foreach (var (column, text) in row.Values)
            {
                var type = column.Type ?? typeByKey[MonitoringService.PressureKeyFor(preservedAt, measuredAt)];
                var current = CurrentValue(asset.P, type.Key)?.ToString(CultureInfo.InvariantCulture);
                var where = column.Type is null ? $"{phase} on {when}: saved as {type.Label}." : $"Measured on {when}.";
                if (!ValueNormalizer.TryParseDecimal(text, out var value))
                {
                    changes.Add(Change(asset.Id, type.Key, ImportOutcome.Invalid, $"{column.Header} is not a number; it was not loaded.", text, current));
                    continue;
                }
                if (type.MinValue is { } min && value < min || type.MaxValue is { } max && value > max)
                {
                    changes.Add(Change(asset.Id, type.Key, ImportOutcome.Invalid,
                        $"{type.Label} must be between {type.MinValue?.ToString() ?? "−∞"} and {type.MaxValue?.ToString() ?? "∞"}; it was not loaded.", text, current));
                    continue;
                }
                var shown = value.ToString(CultureInfo.InvariantCulture);
                if (existing.Contains((asset.Id, type.Id, measuredAt, value)))
                {
                    changes.Add(Change(asset.Id, type.Key, ImportOutcome.NoChange, $"Already in the app with this value and date ({when}).", text, current, shown));
                    continue;
                }
                changes.Add(value == 0
                    ? Change(asset.Id, type.Key, ImportOutcome.Warning, $"{column.Header} is 0: it is loaded, check it. {where}", text, current, shown)
                    : Change(asset.Id, type.Key, ImportOutcome.Applied, where, text, current, shown));
                toSave.Add(new ReadingValue(type.Id, value));
            }

            if (user is null || toSave.Count == 0) continue;
            await editor.AddReadingsAsync(asset.Id, new AddReadingsRequest(measuredAt, row.Note, toSave), user, ct, ChangeSource.Excel);
            foreach (var v in toSave) existing.Add((asset.Id, v.MeasurementTypeId, measuredAt, v.Value));
        }
        return changes;
    }

    /// <summary>
    /// The measured time in UTC. An Excel date keeps its time when it has one; a text date is read day first
    /// (06/11/2026 = 6 November). Without a time it is 12:00 Dubai time.
    /// </summary>
    private static bool TryMeasuredAt(SourceRow row, out DateTime utc)
    {
        utc = default;
        DateTime local;
        if (row.DateValue is { } d) local = d;
        else if (row.Date is null) return false;
        else if (decimal.TryParse(row.Date, NumberStyles.Number, CultureInfo.InvariantCulture, out var serial) && serial is > 36000 and < 80000)
            local = DateTime.FromOADate((double)serial);
        else if (!DateTime.TryParseExact(DashRegex().Replace(row.Date.Trim(), "-"), TextDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out local))
            return false;
        if (local.TimeOfDay == TimeSpan.Zero) local = local.Date + DefaultTime;
        utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), SiteClock.Dubai);
        return true;
    }

    private static readonly string[] TextDateFormats =
    [
        "d-MMM-yy", "d-MMM-yyyy", "d MMM yy", "d MMM yyyy", "d-MMMM-yyyy", "d MMMM yyyy", "d/M/yyyy", "d/M/yy", "d.M.yyyy", "d-M-yyyy", "yyyy-M-d",
        "d-MMM-yy H:mm", "d-MMM-yyyy H:mm", "d MMM yyyy H:mm", "d/M/yyyy H:mm", "d/M/yy H:mm", "yyyy-M-d H:mm", "d-M-yyyy H:mm"
    ];

    /// <summary>Tag as compared: case, repeated spaces and the kind of dash (– — ‐ −) do not matter.</summary>
    internal static string TagKey(string tag) => SpacesRegex().Replace(DashRegex().Replace(tag.Trim(), "-"), " ").ToUpperInvariant();

    [GeneratedRegex("[‐‑‒–—―−]")]
    private static partial Regex DashRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    private static decimal? CurrentValue(AssetPreservation? p, string key) => p is null ? null : key switch
    {
        "PressureBarg" => p.PressureBarg,
        "G2DewPointC" => p.G2DewPointC,
        "G3O2Pct" => p.G3O2Pct,
        "G4N2BlanketBarg" => p.G4N2BlanketBarg,
        "G6H2S" => p.G6H2S,
        "G6LelPct" => p.G6LelPct,
        _ => null
    };

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
