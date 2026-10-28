using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Imports;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;

namespace PresApp.Api.Excel;

/// <summary>
/// Import of the piping line list downloaded from K Data (<see cref="PipingWorkbookExporter"/>). Same rules as the unit
/// file: rows are matched by the hidden Key, only cells the engineer changed count, a value changed in the app after the
/// export is a conflict and is not applied, list values must be in the K Data lists.
/// "Bulk liquid drained" Yes is stored as Completed; an empty "Inspection Done ?" means No.
/// </summary>
public sealed partial class ExcelImportService
{
    private async Task<Plan> AnalysePipingAsync(XLWorkbook wb, ExcelSession session, bool tracking, CancellationToken ct)
    {
        var signature = StructureSignature.FromJson(session.StructureSignature)
                        ?? throw new ImportRejected("The export of this file has no recorded structure. Download a new file.");
        var table = CheckStructure(wb, signature);
        var baseRows = BaseSnapshot.FromJson(session.BaseJson).Rows.ToDictionary(r => r.Key);

        // Columns by header, as in the template the file was made from.
        var headers = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c).Trim()).ToList();
        var map = PipingTemplate.Map(headers);
        var keyColumn = headers.IndexOf(ExcelColumns.KeyHeader) + 1;
        var tagColumn = map.Where(m => m.Key.Role == PipingRole.Tag).Select(m => m.Value + 1).FirstOrDefault();
        var editable = map.Where(m => m.Key.Editable).Select(m => (Column: m.Key, Index: m.Value + 1)).ToList();

        var rows = new List<(int Number, BaseRow Base, IXLTableRow Row)>();
        var changes = new List<ImportChange>();
        var seen = new HashSet<Guid>();
        var body = table.DataRange;
        for (var i = 1; i <= (body?.RowCount() ?? 0); i++)
        {
            var row = body!.Row(i);
            var keyText = WorkbookFormat.CellText(row.Cell(keyColumn)).Trim();
            var tag = tagColumn > 0 ? ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(tagColumn))) : null;
            if (keyText.Length == 0)
            {
                if (row.Cells().All(c => c.HasFormula || c.IsEmpty())) continue;
                changes.Add(new ImportChange
                {
                    RowNumber = i, AssetTag = Truncate(tag, 55), Field = RowField, Outcome = ImportOutcome.Rejected,
                    Reason = "New row without Key: ignored. New lines are added with Onboarding (the client line list) or Add lines in bulk."
                });
                continue;
            }
            if (!Guid.TryParse(keyText, out var key) || !baseRows.TryGetValue(key, out var baseRow))
                throw new ImportRejected($"The hidden Key column was changed in row {i}{(tag is null ? "" : $" ({tag})")}. Nothing was imported: download a new file and copy your changes into it.");
            if (!seen.Add(key))
                throw new ImportRejected($"The hidden Key of row {i}{(tag is null ? "" : $" ({tag})")} appears twice (a row was copied with its Key). Nothing was imported: download a new file.");
            rows.Add((i, baseRow, row));
        }

        var ids = rows.Select(r => r.Base.Key).ToList();
        var query = db.Assets.Include(a => a.Preservation).Include(a => a.Pipeline).Include(a => a.FieldValues).Where(a => ids.Contains(a.Id));
        var assets = await (tracking ? query : query.AsNoTracking()).ToDictionaryAsync(a => a.Id, ct);
        var defs = await PipingWorkbookExporter.LoadFixedFieldsAsync(db, tracking, ct);
        var options = await AssetEditor.LoadActiveOptionsAsync(db, ct);

        foreach (var (number, baseRow, row) in rows)
        {
            assets.TryGetValue(baseRow.Key, out var asset);
            ImportChange Change(string field, ImportOutcome outcome, string? reason, string? baseValue = null, string? excel = null, string? current = null, string? newValue = null) => new()
            {
                AssetId = baseRow.Key, AssetCode = asset?.Code ?? baseRow.Code, AssetTag = Truncate(asset?.Tag ?? baseRow.Tag, 55), RowNumber = number,
                Field = field, BaseValue = baseValue, ExcelValue = excel, CurrentValue = current, NewValue = newValue, Outcome = outcome, Reason = reason
            };

            if (tagColumn > 0)
            {
                var tagInFile = ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(tagColumn)));
                if (!string.Equals(tagInFile, ValueNormalizer.CleanText(baseRow.Tag), StringComparison.Ordinal))
                    changes.Add(Change("Tag", ImportOutcome.Warning, "Tag No was changed in the file. Line numbers are changed in the app; the row was matched by its hidden Key.",
                        baseRow.Tag, tagInFile, asset?.Tag));
            }

            foreach (var (col, index) in editable)
            {
                if (!baseRow.Values.TryGetValue(col.BaseKey, out var baseValue)) continue;
                var excel = ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(index)));
                if (PipingLayout.Same(excel, baseValue)) continue;   // the engineer did not change this cell

                var fieldName = col.Role == PipingRole.Custom ? defs.GetValueOrDefault(col.Key!)?.Label ?? col.Header : col.Key!;
                if (asset is null) { changes.Add(Change(fieldName, ImportOutcome.Rejected, "The line no longer exists.", baseValue, excel)); continue; }
                var current = PipingLayout.Display(col, asset, defs);
                if (!asset.IsActive) { changes.Add(Change(fieldName, ImportOutcome.Rejected, "Inactive line: it cannot be edited.", baseValue, excel, current)); continue; }

                // Validate on the side, then compare what the engineer would see.
                string? error;
                string? newText;
                object? value = null;
                FieldDef? field = null;
                FieldDefinition? def = null;
                var raw = excel;
                if (col.Role == PipingRole.Field)
                {
                    field = PreservationFields.Find(col.Key!)!;
                    error = FieldValues.Prepare(field, AssetType.Pipeline, raw, options, out value);
                    newText = col.Key == nameof(AssetPreservation.G1BulkLiquidDrained)
                        ? (AssetPreservation.IsCompleted(FieldValues.Format(value)) ? "Yes" : null)
                        : FieldValues.Format(value);
                }
                else
                {
                    if (!defs.TryGetValue(col.Key!, out def)) continue;
                    if (col.Key == PipingFieldKeys.InspectionDone) raw ??= "No";
                    var probe = new Asset { Id = asset.Id, FieldValues = [] };
                    error = customFields.Write(def, probe, null, raw);
                    newText = probe.FieldValues.FirstOrDefault() is { } fv ? CustomFields.CustomFieldService.Format(def, fv) : null;
                }
                if (error is not null) { changes.Add(Change(fieldName, ImportOutcome.Invalid, error, baseValue, excel, current)); continue; }
                if (PipingLayout.Same(current, newText)) { changes.Add(Change(fieldName, ImportOutcome.NoChange, "The app already has this value.", baseValue, excel, current, newText)); continue; }
                if (!PipingLayout.Same(current, baseValue))
                {
                    changes.Add(Change(fieldName, ImportOutcome.Conflict,
                        $"Changed in the app after the export (now \"{current ?? "empty"}\"). Not applied: check which value is right and set it in the app.",
                        baseValue, excel, current));
                    continue;
                }

                if (field is not null)
                {
                    asset.Preservation ??= new AssetPreservation { AssetId = asset.Id };
                    FieldValues.Write(field, asset, value);
                }
                else customFields.Write(def!, asset, null, raw);
                changes.Add(Change(fieldName, ImportOutcome.Applied, null, baseValue, excel, current, newText));
            }
        }

        changes.AddRange(await AnalyseNotesAsync(wb, session, tracking, ct));
        return new Plan(session, rows.Count, baseRows.Count - rows.Count, changes);
    }

    /// <summary>
    /// Notes under the Summary, compared like the cells: only notes the engineer added or changed count, a note changed in the
    /// app after the export is a conflict, and a note removed from the file is kept in K Data (it is removed in the app).
    /// </summary>
    private async Task<List<ImportChange>> AnalyseNotesAsync(XLWorkbook wb, ExcelSession session, bool tracking, CancellationToken ct)
    {
        var changes = new List<ImportChange>();
        var baseNotes = BaseSnapshot.FromJson(session.BaseJson).Notes;
        var inFile = baseNotes is null ? null : Piping.PipingNoteService.ReadSummary(wb, SiteClock.DateOf(session.ExportedAtUtc));
        if (baseNotes is null || inFile is null) return changes;

        var query = db.PipingNotes.Where(n => n.UnitId == session.UnitId && n.IsActive);
        var current = (await (tracking ? query : query.AsNoTracking()).ToListAsync(ct)).ToDictionary(n => Piping.PipingNoteService.Key(n.NoteDate));
        ImportChange Change(string key, ImportOutcome outcome, string? reason, string? baseValue, string? excel, string? now, string? newValue = null) => new()
        {
            Field = Piping.PipingNoteService.Label(Piping.PipingNoteService.FromKey(key)), Outcome = outcome, Reason = reason,
            BaseValue = Truncate(baseValue, 4000), ExcelValue = Truncate(excel, 4000), CurrentValue = Truncate(now, 4000), NewValue = Truncate(newValue, 4000)
        };

        foreach (var (key, text) in inFile)
        {
            baseNotes.TryGetValue(key, out var baseText);
            if (Piping.PipingNoteService.Same(text, baseText)) continue;   // not changed in the file
            current.TryGetValue(key, out var note);
            if (Piping.PipingNoteService.Same(note?.Text, text)) { changes.Add(Change(key, ImportOutcome.NoChange, "The app already has this note.", baseText, text, note?.Text, text)); continue; }
            if (!Piping.PipingNoteService.Same(note?.Text, baseText))
            {
                changes.Add(Change(key, ImportOutcome.Conflict, "Changed in the app after the export. Not applied: check which text is right and set it in the app.", baseText, text, note?.Text));
                continue;
            }
            if (text.Length > PipingNote.TextLength) { changes.Add(Change(key, ImportOutcome.Invalid, $"A note allows at most {PipingNote.TextLength} characters.", baseText, text, note?.Text)); continue; }
            if (tracking)
            {
                if (note is null) db.PipingNotes.Add(new PipingNote { UnitId = session.UnitId, NoteDate = Piping.PipingNoteService.FromKey(key), Text = text });
                else note.Text = text;
            }
            changes.Add(Change(key, ImportOutcome.Applied, baseText is null ? "New note." : null, baseText, text, note?.Text, text));
        }
        foreach (var (key, baseText) in baseNotes.Where(b => !inFile.ContainsKey(b.Key)))
        {
            if (current.ContainsKey(key))
                changes.Add(Change(key, ImportOutcome.Warning, "Removed from the file: kept in K Data. Remove it in the app (Pipeline Register → Summary notes) if it is no longer wanted.", baseText, null, current[key].Text));
        }
        return changes;
    }
}
