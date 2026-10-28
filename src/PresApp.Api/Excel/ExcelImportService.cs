using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Common;
using PresApp.Domain.Imports;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

/// <summary>
/// Imports a unit Excel exported by the app and edited by the engineers. Two steps:
/// <list type="number">
/// <item><b>Preview</b>: the file is checked (session, age, structure, Key column) and compared; nothing is written.</item>
/// <item><b>Apply</b>: the stored file is compared again against the data as it is at that moment and the valid
/// changes are written in one transaction. The session can then not be used again.</item>
/// </list>
/// Per-cell rules (Power Automate flow, Oct 2026): only cells the engineer changed count; a field changed in the app
/// after the export is a conflict; list values must be active options; derived Current Gate (forward only) and Last
/// Field Action Date when a gate is closed; G2/G3/G4 completed without a reading are applied with a warning.
/// The service takes a stream and a file name so a server watch folder can feed it later.
/// </summary>
public sealed partial class ExcelImportService(PresAppDbContext db, AuditContext audit, TimeProvider clock, CustomFields.CustomFieldService customFields)
{
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const int DefaultMaxAgeHours = 48;

    private const string RowField = "(row)";
    private const string CurrentGateField = nameof(AssetPreservation.CurrentGate);

    private sealed class ImportRejected(string message) : Exception(message);

    private sealed record Plan(ExcelSession Session, int RowsInFile, int MissingRows, List<ImportChange> Changes);

    // ------------------------------------------------------------------ preview

    public async Task<ImportRunDetail> PreviewAsync(Stream file, string fileName, int unitId, string uploadedBy, CancellationToken ct)
    {
        var bytes = await ReadAllAsync(file, ct);
        var run = new ImportRun
        {
            Kind = ImportKind.Update, FileName = Path.GetFileName(fileName), UnitId = unitId,
            UploadedBy = uploadedBy, UploadedAtUtc = clock.GetUtcNow().UtcDateTime, Status = ImportStatus.Previewed
        };
        try
        {
            using var wb = Open(bytes);
            var plan = await AnalyseAsync(wb, unitId, tracking: false, ct);
            Fill(run, plan);
            db.ImportFiles.Add(new ImportFile { ImportRun = run, Content = bytes });
        }
        catch (ImportRejected rejected)
        {
            run.Status = ImportStatus.Rejected;
            run.Message = rejected.Message;
        }
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return await ImportQueries.DetailAsync(db, run.Id, ct);
    }

    // ------------------------------------------------------------------ apply

    public async Task<ImportRunDetail> ApplyAsync(long runId, string appliedBy, CancellationToken ct)
    {
        var run = await db.ImportRuns.Include(r => r.Changes).FirstOrDefaultAsync(r => r.Id == runId, ct)
                  ?? throw ApiProblem.NotFound("Import not found.");
        if (run.Status != ImportStatus.Previewed) throw ApiProblem.Invalid($"This import is {Text(run.Status)}; it cannot be applied.");
        var file = await db.ImportFiles.AsNoTracking().FirstOrDefaultAsync(f => f.ImportRunId == runId, ct)
                   ?? throw ApiProblem.Invalid("The file of this preview is no longer available. Upload it again.");

        audit.Source = ChangeSource.Excel;
        audit.BatchId = Guid.NewGuid();
        audit.UserName = appliedBy;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (run.SessionId is not { } sessionId) throw new ImportRejected("The preview has no session.");
            // Claim the session so two people cannot apply the same file at the same time.
            var claimed = await db.ExcelSessions.Where(s => s.Id == sessionId && s.Status == ExcelSessionStatus.Open)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ExcelSessionStatus.Processing), ct);
            if (claimed == 0) throw new ImportRejected("This file was already imported (or is being imported right now). Download a new file.");

            using var wb = Open(file.Content);
            var plan = await AnalyseAsync(wb, run.UnitId ?? 0, tracking: true, ct, allowProcessing: true);

            plan.Session.Status = ExcelSessionStatus.Processed;
            plan.Session.ProcessedAtUtc = clock.GetUtcNow().UtcDateTime;
            db.ImportChanges.RemoveRange(run.Changes);
            run.Changes = [];
            Fill(run, plan);
            run.AppliedAtUtc = plan.Session.ProcessedAtUtc;
            run.Status = run.IssueCount > 0 ? ImportStatus.AppliedWithIssues : ImportStatus.Applied;
            run.Message = (run.Message is null ? "" : run.Message + " ") + $"Applied by {appliedBy}.";

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (ImportRejected rejected)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var fresh = await db.ImportRuns.FirstAsync(r => r.Id == runId, ct);
            fresh.Status = ImportStatus.Rejected;
            fresh.Message = rejected.Message;
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            throw ApiProblem.Conflict("Some equipment was saved by someone else while the import was running. Nothing was written; apply the preview again.");
        }
        return await ImportQueries.DetailAsync(db, runId, ct);
    }

    public async Task<ImportRunDetail> CancelAsync(long runId, CancellationToken ct)
    {
        var run = await db.ImportRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw ApiProblem.NotFound("Import not found.");
        if (run.Status != ImportStatus.Previewed) throw ApiProblem.Invalid($"This import is {Text(run.Status)}; it cannot be cancelled.");
        run.Status = ImportStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return await ImportQueries.DetailAsync(db, runId, ct);
    }

    // ------------------------------------------------------------------ analysis (shared by preview and apply)

    private async Task<Plan> AnalyseAsync(XLWorkbook wb, int unitId, bool tracking, CancellationToken ct, bool allowProcessing = false)
    {
        var session = await CheckSessionAsync(wb, unitId, tracking, allowProcessing, ct);
        if (session.AssetType == AssetType.Pipeline) return await AnalysePipingAsync(wb, session, tracking, ct);
        var signature = StructureSignature.FromJson(session.StructureSignature)
                        ?? throw new ImportRejected("The export of this file has no recorded structure. Download a new file.");
        var table = CheckStructure(wb, signature);
        var baseRows = BaseSnapshot.FromJson(session.BaseJson).Rows.ToDictionary(r => r.Key);

        var headers = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c)).ToList();
        var columns = ExcelColumns.Map(headers, headers.Select(_ => false).ToList());
        var keyColumn = columns.First(c => c.Role == ColumnRole.Key).Index + 1;
        var tagColumn = columns.FirstOrDefault(c => c.Role == ColumnRole.Tag)?.Index + 1;
        var editable = columns.Where(c => c.Editable).ToList();
        var holdColumn = columns.FirstOrDefault(c => c.Role == ColumnRole.HoldCategory);
        var naColumn = columns.FirstOrDefault(c => c.Role == ColumnRole.NaCategory);

        // ---- the Key column must be exactly as exported
        var rows = new List<(int Number, BaseRow Base, IXLTableRow Row)>();
        var changes = new List<ImportChange>();
        var seen = new HashSet<Guid>();
        var body = table.DataRange;
        for (var i = 1; i <= (body?.RowCount() ?? 0); i++)
        {
            var row = body!.Row(i);
            var keyText = WorkbookFormat.CellText(row.Cell(keyColumn)).Trim();
            var tag = tagColumn is { } tc ? ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(tc))) : null;
            if (keyText.Length == 0)
            {
                if (row.Cells().All(c => c.HasFormula || c.IsEmpty())) continue;
                changes.Add(new ImportChange
                {
                    RowNumber = i, AssetTag = Truncate(tag, 55), Field = RowField, Outcome = ImportOutcome.Rejected,
                    Reason = "New row without Key: ignored. Create new equipment in the app (New equipment) or load it with Onboarding."
                });
                continue;
            }
            if (!Guid.TryParse(keyText, out var key) || !baseRows.TryGetValue(key, out var baseRow))
                throw new ImportRejected($"The hidden Key column was changed in row {i}{(tag is null ? "" : $" ({tag})")}. Nothing was imported: download a new file and copy your changes into it.");
            if (!seen.Add(key))
                throw new ImportRejected($"The hidden Key of row {i}{(tag is null ? "" : $" ({tag})")} appears twice (a row was copied with its Key). Nothing was imported: download a new file.");
            rows.Add((i, baseRow, row));
        }
        var missing = baseRows.Count - rows.Count;

        // ---- current data
        var ids = rows.Select(r => r.Base.Key).ToList();
        var query = db.Assets.Include(a => a.Preservation).ThenInclude(p => p!.DashboardCategory).Include(a => a.GateDocuments).Where(a => ids.Contains(a.Id));
        var assets = await (tracking ? query : query.AsNoTracking()).ToDictionaryAsync(a => a.Id, ct);
        var options = await AssetEditor.LoadActiveOptionsAsync(db, ct);
        var evidence = (await db.Readings.AsNoTracking()
                .Where(r => ids.Contains(r.AssetId) && r.MeasurementType.EvidenceForGate != null)
                .Select(r => new { r.AssetId, Gate = r.MeasurementType.EvidenceForGate!.Value })
                .Distinct().ToListAsync(ct))
            .Select(x => (x.AssetId, x.Gate)).ToHashSet();
        var evidenceLabels = await db.MeasurementTypes.AsNoTracking().Where(m => m.EvidenceForGate != null)
            .ToDictionaryAsync(m => m.EvidenceForGate!.Value, m => m.Label, ct);
        var today = SiteClock.Today(clock);
        var unitCategories = holdColumn is null && naColumn is null ? []
            : await db.DashboardCategories.Where(c => c.UnitId == unitId && c.IsActive && c.Code != null).ToListAsync(ct);

        foreach (var (number, baseRow, row) in rows)
        {
            assets.TryGetValue(baseRow.Key, out var asset);
            ImportChange Change(string field, ImportOutcome outcome, string? reason, string? baseValue = null, string? excel = null, string? current = null, string? newValue = null) => new()
            {
                AssetId = baseRow.Key, AssetCode = asset?.Code ?? baseRow.Code, AssetTag = Truncate(asset?.Tag ?? baseRow.Tag, 55), RowNumber = number,
                Field = field, BaseValue = baseValue, ExcelValue = excel, CurrentValue = current, NewValue = newValue, Outcome = outcome, Reason = reason
            };

            if (tagColumn is { } tcol)
            {
                var tagInFile = ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(tcol)));
                if (!string.Equals(tagInFile, ValueNormalizer.CleanText(baseRow.Tag), StringComparison.Ordinal))
                    changes.Add(Change("Tag", ImportOutcome.Warning, "EQUIP NO was changed in the file. Tags are changed in the app; the row was matched by its hidden Key.",
                        baseRow.Tag, tagInFile, asset?.Tag));
            }

            var applied = new List<(FieldDef Field, string? NewText)>();
            foreach (var col in editable)
            {
                var field = col.Field!;
                if (!baseRow.Values.TryGetValue(field.Key, out var baseValue)) continue;
                var excel = WorkbookFormat.CellText(row.Cell(col.Index + 1));
                if (FieldValues.Same(field, excel, baseValue)) continue;   // the engineer did not change this cell
                var excelClean = ValueNormalizer.CleanText(excel);

                if (asset is null) { changes.Add(Change(field.Key, ImportOutcome.Rejected, "The equipment no longer exists.", baseValue, excelClean)); continue; }
                var current = FieldValues.Read(field, asset);
                if (!asset.IsActive) { changes.Add(Change(field.Key, ImportOutcome.Rejected, "Inactive equipment: it cannot be edited.", baseValue, excelClean, current)); continue; }

                var error = FieldValues.Prepare(field, asset.Type, excel, options, out var value);
                if (error is not null) { changes.Add(Change(field.Key, ImportOutcome.Invalid, error, baseValue, excelClean, current)); continue; }
                var newText = FieldValues.Format(value);

                if (FieldValues.Same(field, current, newText)) { changes.Add(Change(field.Key, ImportOutcome.NoChange, "The app already has this value.", baseValue, excelClean, current, newText)); continue; }
                if (!FieldValues.Same(field, current, baseValue))
                {
                    changes.Add(Change(field.Key, ImportOutcome.Conflict,
                        $"Changed in the app after the export (now \"{current ?? "empty"}\"). Not applied: check which value is right and set it in the app.",
                        baseValue, excelClean, current));
                    continue;
                }

                asset.Preservation ??= new AssetPreservation { AssetId = asset.Id };
                FieldValues.Write(field, asset, value);
                applied.Add((field, newText));
                changes.Add(Change(field.Key, ImportOutcome.Applied, null, baseValue, excelClean, current, newText));
            }

            // ---- template v4: Hold / N/A category chosen by code (one of the two columns at most)
            if ((holdColumn is not null || naColumn is not null) && baseRow.Values.TryGetValue(TemplateV4.CategoryField, out var baseCategory))
            {
                var hold = holdColumn is null ? null : ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(holdColumn.Index + 1)));
                var na = naColumn is null ? null : ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(naColumn.Index + 1)));
                var excelText = hold is not null && na is not null ? $"Hold:{hold} / N/A:{na}" : hold is not null ? $"Hold:{hold}" : na is not null ? $"N/A:{na}" : null;
                var section = hold is not null ? CategorySection.Hold : CategorySection.NotApplicable;
                var code = Reporting.UnitDashboard.NormalizeCode(hold ?? na);
                var wanted = code is null ? null : unitCategories.FirstOrDefault(c => c.Section == section && c.Code == code);
                var wantedToken = excelText is null ? null : wanted is null ? excelText : TemplateV4.Token(wanted);

                if (wantedToken != baseCategory)
                {
                    var current = TemplateV4.Token(asset?.Preservation?.DashboardCategory);
                    if (asset is null) changes.Add(Change(TemplateV4.CategoryField, ImportOutcome.Rejected, "The equipment no longer exists.", baseCategory, excelText));
                    else if (!asset.IsActive) changes.Add(Change(TemplateV4.CategoryField, ImportOutcome.Rejected, "Inactive equipment: it cannot be edited.", baseCategory, excelText, current));
                    else if (hold is not null && na is not null)
                        changes.Add(Change(TemplateV4.CategoryField, ImportOutcome.Invalid, "Both Hold Category and N/A Category are filled. Keep only one.", baseCategory, excelText, current));
                    else if (excelText is not null && wanted is null)
                        changes.Add(Change(TemplateV4.CategoryField, ImportOutcome.Invalid,
                            $"\"{hold ?? na}\" is not an active {TemplateV4.SectionText(section)} category of this unit. Categories are created in K Data (Dashboard categories).",
                            baseCategory, excelText, current));
                    else if (current == wantedToken)
                        changes.Add(Change(TemplateV4.CategoryField, ImportOutcome.NoChange, "The app already has this value.", baseCategory, excelText, current, wantedToken));
                    else if (current != baseCategory)
                        changes.Add(Change(TemplateV4.CategoryField, ImportOutcome.Conflict,
                            $"Changed in the app after the export (now \"{current ?? "none"}\"). Not applied: check which category is right and set it in the app.",
                            baseCategory, excelText, current));
                    else
                    {
                        asset.Preservation ??= new AssetPreservation { AssetId = asset.Id };
                        asset.Preservation.DashboardCategoryId = wanted?.Id;
                        asset.Preservation.DashboardCategory = wanted;
                        changes.Add(Change(TemplateV4.CategoryField, ImportOutcome.Applied, null, baseCategory, excelText, current, wantedToken));
                    }
                }
            }

            if (applied.Count == 0 || asset is null) continue;
            var p = asset.Preservation!;

            // ---- derived rules
            var gateChanged = applied.Any(a => PreservationFields.IsGateStatus(a.Field));
            var gateClosed = applied.Any(a => PreservationFields.IsGateStatus(a.Field) && AssetPreservation.IsCompleted(a.NewText));
            var before = (p.PreservationDate, p.N2BlanketedDate, p.LastFieldActionDate);
            var outcome = PreservationRules.Apply(p, gateChanged, gateClosed, today);

            if (outcome.ProposedCurrentGate is { } proposed)
            {
                var oldGate = FieldValues.CurrentGateText(p.CurrentGate);
                if ((int)proposed > (int)p.CurrentGate)
                {
                    p.CurrentGate = proposed;
                    changes.Add(Change(CurrentGateField, ImportOutcome.Derived, "Highest completed gate.", current: oldGate, newValue: FieldValues.CurrentGateText(proposed)));
                }
                else
                {
                    changes.Add(Change(CurrentGateField, ImportOutcome.NoChange,
                        $"Current Gate kept as {oldGate} (the highest completed gate is now {FieldValues.CurrentGateText(proposed)}; it never moves back automatically). Check it in the app.",
                        current: oldGate, newValue: oldGate));
                }
            }
            if (outcome.ProposedLastFieldActionDate is { } lfad)
            {
                changes.Add(Change(nameof(AssetPreservation.LastFieldActionDate), ImportOutcome.Derived, "A gate was closed.",
                    current: FieldValues.Format(p.LastFieldActionDate), newValue: FieldValues.Format(lfad)));
                p.LastFieldActionDate = lfad;
            }
            if (before.LastFieldActionDate is null && p.LastFieldActionDate is not null)
                changes.Add(Change(nameof(AssetPreservation.LastFieldActionDate), ImportOutcome.Derived, "A gate was closed.", newValue: FieldValues.Format(p.LastFieldActionDate)));
            if (before.PreservationDate is null && p.PreservationDate is not null)
                changes.Add(Change(nameof(AssetPreservation.PreservationDate), ImportOutcome.Derived, "All gates are Completed or Not Applicable.", newValue: FieldValues.Format(p.PreservationDate)));
            if (before.N2BlanketedDate is null && p.N2BlanketedDate is not null)
                changes.Add(Change(nameof(AssetPreservation.N2BlanketedDate), ImportOutcome.Derived, "G4 was completed.", newValue: FieldValues.Format(p.N2BlanketedDate)));
            AssetEditor.EnsureDocumentRows(asset);

            // ---- gate evidence
            foreach (var (field, newText) in applied)
            {
                if (field.Gate is not { } gate || !PreservationFields.IsGateStatus(field) || !AssetPreservation.IsCompleted(newText)) continue;
                if (!evidenceLabels.TryGetValue(gate, out var measurement) || evidence.Contains((asset.Id, gate))) continue;
                changes.Add(Change(field.Key, ImportOutcome.Warning,
                    $"G{gate} completed without a {measurement} reading. Ask the operator to record it in the app.", newValue: newText));
            }
        }

        return new Plan(session, rows.Count, missing, changes);
    }

    private async Task<ExcelSession> CheckSessionAsync(XLWorkbook wb, int unitId, bool tracking, bool allowProcessing, CancellationToken ct)
    {
        var meta = WorkbookFormat.ReadMeta(wb);
        if (!meta.TryGetValue("SessionId", out var sessionText))
        {
            throw new ImportRejected(WorkbookFormat.FindTable(wb, ExcelColumns.MasterTable) is null && WorkbookFormat.FindTable(wb, PipingLayout.TableName) is null
                ? "This is not a unit master list or piping line list downloaded from K Data."
                : "This file was not downloaded from K Data, so its changes cannot be traced. Download the unit file from the Excel page; client lists are loaded with Onboarding.");
        }
        var version = int.TryParse(meta.GetValueOrDefault("FormatVersion"), out var v) ? v : 0;
        if (version < WorkbookFormat.FormatVersion || !string.Equals(meta.GetValueOrDefault("App"), WorkbookFormat.AppName, StringComparison.Ordinal))
            throw new ImportRejected("This file was produced by the old Power App flow. Download a new file from K Data and copy the changes into it.");
        if (!Guid.TryParse(sessionText, out var sessionId)) throw new ImportRejected("The file session is not valid. Download a new file.");

        var query = db.ExcelSessions.Include(s => s.Unit).Where(s => s.Id == sessionId);
        var session = await (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct)
                      ?? throw new ImportRejected("This file's session is unknown (it was exported from another installation). Download a new file.");
        if (session.UnitId != unitId)
            throw new ImportRejected($"This file is for {session.Unit.Name}. Upload it from the {session.Unit.Name} Excel page.");
        if (session.Status == ExcelSessionStatus.Processed)
            throw new ImportRejected($"This file was already imported on {SiteClock.DateOf(session.ProcessedAtUtc ?? session.ExportedAtUtc):dd MMM yyyy}. Each downloaded file can be imported once: download a new one.");
        if (session.Status == ExcelSessionStatus.Processing && !allowProcessing)
            throw new ImportRejected("This file is being imported right now.");

        var maxAge = await MaxAgeHoursAsync(db, ct);
        var age = clock.GetUtcNow().UtcDateTime - session.ExportedAtUtc;
        if (age > TimeSpan.FromHours(maxAge))
            throw new ImportRejected($"This file was downloaded {Math.Floor(age.TotalHours):0} hours ago; files older than {maxAge} hours are not accepted because the data may have moved on. Download a new file and copy the changes into it.");
        return session;
    }

    /// <summary>Same table, same headers in the same columns. Hiding columns or rows and filtering are fine.</summary>
    private static IXLTable CheckStructure(XLWorkbook wb, StructureSignature expected)
    {
        var table = WorkbookFormat.FindTable(wb, expected.Table)
                    ?? throw new ImportRejected($"The {expected.Table} table was deleted or renamed. Nothing was imported: download a new file.");
        var headers = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c).Trim()).ToList();
        var want = expected.Headers.Select(h => h.Trim()).ToList();
        var firstColumn = table.HeadersRow().FirstCell().Address.ColumnNumber;
        var headerRow = table.HeadersRow().RowNumber();

        var problems = new List<string>();
        if (headerRow != expected.HeaderRow) problems.Add($"the table starts on row {headerRow} instead of row {expected.HeaderRow} (rows were inserted or deleted above it)");
        if (firstColumn != expected.FirstColumn) problems.Add($"the table starts in column {Letter(firstColumn)} instead of {Letter(expected.FirstColumn)} (columns were inserted or deleted before it)");

        var max = Math.Max(headers.Count, want.Count);
        for (var i = 0; i < max && problems.Count < 4; i++)
        {
            var col = Letter(expected.FirstColumn + i);
            var got = i < headers.Count ? headers[i] : null;
            var exp = i < want.Count ? want[i] : null;
            if (string.Equals(got, exp, StringComparison.Ordinal)) continue;
            if (exp is null) problems.Add($"a column \"{got}\" was added in {col}");
            else if (got is null) problems.Add($"column \"{exp}\" was deleted");
            else if (headers.Contains(exp, StringComparer.Ordinal)) problems.Add($"column \"{exp}\" was moved (expected in {col}, found \"{got}\" there)");
            else if (!want.Contains(got, StringComparer.Ordinal)) problems.Add($"column \"{exp}\" in {col} was renamed to \"{got}\" (or a column was inserted)");
            else problems.Add($"column \"{exp}\" is missing from {col} (found \"{got}\")");
        }
        if (problems.Count > 0)
            throw new ImportRejected($"The structure of the file was changed: {string.Join("; ", problems)}. Nothing was imported. Columns can be hidden, but not moved, renamed, inserted or deleted: download a new file and copy your changes into it.");
        return table;
    }

    // ------------------------------------------------------------------ helpers

    public static async Task<int> MaxAgeHoursAsync(PresAppDbContext db, CancellationToken ct)
    {
        var text = await db.Settings.AsNoTracking().Where(s => s.Key == AppSettingKeys.ExcelMaxAgeHours).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h > 0 ? h : DefaultMaxAgeHours;
    }

    private static void Fill(ImportRun run, Plan plan)
    {
        foreach (var c in plan.Changes)
        {
            c.Resolution = c.Outcome is ImportOutcome.Invalid or ImportOutcome.Conflict or ImportOutcome.Rejected or ImportOutcome.Warning
                ? IssueResolution.Open : IssueResolution.None;
            run.Changes.Add(c);
        }
        run.SessionId = plan.Session.Id;
        run.RowsInFile = plan.RowsInFile;
        run.AppliedCount = plan.Changes.Count(c => c.Outcome is ImportOutcome.Applied or ImportOutcome.Derived);
        run.IssueCount = plan.Changes.Count(c => c.Resolution == IssueResolution.Open && c.Outcome != ImportOutcome.Warning);
        run.WarningCount = plan.Changes.Count(c => c.Outcome == ImportOutcome.Warning);
        run.Message = plan.MissingRows > 0 ? $"{plan.MissingRows} exported rows are not in the file (deleted rows are simply not imported)." : null;
    }

    private static XLWorkbook Open(byte[] bytes)
    {
        try { return new XLWorkbook(new MemoryStream(bytes)); }
        catch (Exception) { throw new ImportRejected("The file could not be opened as an Excel workbook (.xlsx). If it is open in Excel, save it and try again."); }
    }

    private static async Task<byte[]> ReadAllAsync(Stream file, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) throw ApiProblem.Invalid("The file is empty.");
        if (buffer.Length > MaxFileBytes) throw ApiProblem.Invalid("The file is larger than 20 MB.");
        return buffer.ToArray();
    }

    private static string Letter(int column) => XLHelper.GetColumnLetterFromNumber(column);

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    internal static string Text(ImportStatus s) => s switch
    {
        ImportStatus.AppliedWithIssues => "applied with issues",
        _ => s.ToString().ToLowerInvariant()
    };
}
