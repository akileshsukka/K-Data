using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Common;
using PresApp.Domain.Groups;
using PresApp.Domain.Imports;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

/// <summary>
/// Loads a client master list (any version of the unit list, with or without the app's hidden columns) into a unit:
/// <list type="bullet">
/// <item>rows are matched to active equipment of the unit by EQUIP NO;</item>
/// <item>unknown tags become new equipment (Equipment ID from the discipline prefix and the unit counter);</item>
/// <item>non-empty cells fill or overwrite the field (the engineer's latest list is the most recent data);
/// empty cells never clear anything; values not in the lists are reported, never guessed.</item>
/// </list>
/// Same two steps as the Excel import: preview, then apply against the data as it is at that moment.
/// A line list (first column header "Line No." instead of "EQUIP NO") works the same way: new line numbers become
/// lines of the Pipeline Register, with size, spec, service and insulation read from the number.
/// </summary>
public sealed class OnboardingService(PresAppDbContext db, AuditContext audit, TimeProvider clock, CustomFields.CustomFieldService customFields)
{
    private const string AssetField = "(asset)";
    private const string RowField = "(row)";

    private sealed class Rejected(string message) : Exception(message);

    private sealed record SourceRow(int Number, string Tag, string? Discipline, string? Description, Dictionary<FieldDef, string> Values,
        Dictionary<string, string> Groups, Dictionary<string, string> Custom);

    /// <summary>List columns that put the row in a group of that kind (one group of each kind per asset).</summary>
    private static string? GroupKindOf(string header) => ExcelColumns.Norm(header) switch
    {
        "circuit" or "circuitno" or "circuitnumber" or "circuitid" => "Circuit",
        "workpack" or "workpackno" or "workpacknumber" or "wp" or "wpno" => "Workpack",
        "system" or "systemno" or "systemnumber" => "System",
        _ => null
    };

    public async Task<ImportRunDetail> PreviewAsync(Stream file, string fileName, int unitId, string uploadedBy, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > ExcelImportService.MaxFileBytes) throw ApiProblem.Invalid("Upload an .xlsx file of at most 20 MB.");
        var bytes = buffer.ToArray();

        var run = new ImportRun
        {
            Kind = ImportKind.Onboarding, FileName = Path.GetFileName(fileName), UnitId = unitId,
            UploadedBy = uploadedBy, UploadedAtUtc = clock.GetUtcNow().UtcDateTime, Status = ImportStatus.Previewed
        };
        try
        {
            var (rows, ignored, lines) = Read(bytes, await CustomHeadersAsync(ct));
            var changes = await AnalyseAsync(rows, unitId, lines, apply: false, ct);
            Fill(run, rows.Count, changes, ignored);
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

    /// <summary>Decision for a row whose line number looks like an existing line: "update", "rename" or "create".</summary>
    public const string PossibleMatchField = "(possible match)";

    public async Task<ImportRunDetail> ApplyAsync(long runId, string appliedBy, CancellationToken ct, IReadOnlyDictionary<int, string>? decisions = null)
    {
        var run = await db.ImportRuns.Include(r => r.Changes).FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw ApiProblem.NotFound("Import not found.");
        if (run.Status != ImportStatus.Previewed) throw ApiProblem.Invalid($"This import is {ExcelImportService.Text(run.Status)}; it cannot be applied.");
        var file = await db.ImportFiles.AsNoTracking().FirstOrDefaultAsync(f => f.ImportRunId == runId, ct)
                   ?? throw ApiProblem.Invalid("The file of this preview is no longer available. Upload it again.");

        audit.Source = ChangeSource.Onboarding;
        audit.BatchId = Guid.NewGuid();
        audit.UserName = appliedBy;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (rows, ignored, lines) = Read(file.Content, await CustomHeadersAsync(ct));
        var changes = await AnalyseAsync(rows, run.UnitId ?? 0, lines, apply: true, ct, decisions);
        db.ImportChanges.RemoveRange(run.Changes);
        run.Changes = [];
        Fill(run, rows.Count, changes, ignored);
        run.AppliedAtUtc = clock.GetUtcNow().UtcDateTime;
        run.Status = run.IssueCount > 0 ? ImportStatus.AppliedWithIssues : ImportStatus.Applied;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ImportQueries.DetailAsync(db, runId, ct);
    }

    // ------------------------------------------------------------------ reading the client list

    private static bool IsTagHeader(string h) => ExcelColumns.Norm(h) is "equipno" or "equipmentno" or "lineno" or "linenumber";
    private static bool IsLineHeader(string h) => ExcelColumns.Norm(h) is "lineno" or "linenumber";

    /// <summary>Normalized labels of the active custom fields (any register): those columns are loaded too.</summary>
    private async Task<HashSet<string>> CustomHeadersAsync(CancellationToken ct) =>
        (await db.FieldDefinitions.AsNoTracking().Where(f => f.IsActive).Select(f => f.Label).ToListAsync(ct)).Select(ExcelColumns.Norm).ToHashSet();

    private static (List<SourceRow> Rows, List<string> Ignored, bool Lines) Read(byte[] bytes, HashSet<string> customHeaders)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(new MemoryStream(bytes)); }
        catch (Exception) { throw new Rejected("The file could not be opened as an Excel workbook (.xlsx)."); }
        using (wb)
        {
            var (headers, rows) = FindList(wb) ?? throw new Rejected("No list with an \"EQUIP NO\" or \"Line No.\" column was found on the visible sheets.");
            var columns = ExcelColumns.Map(headers, headers.Select(_ => false).ToList(), onboarding: true);
            var tagColumn = columns.First(c => c.Role == ColumnRole.Tag && IsTagHeader(c.Header));
            var tag = tagColumn.Index;
            var lines = IsLineHeader(tagColumn.Header);
            var discipline = columns.FirstOrDefault(c => c.Role == ColumnRole.Discipline)?.Index;
            var description = columns.FirstOrDefault(c => c.Role == ColumnRole.Description)?.Index;
            var fields = columns.Where(c => c.Role == ColumnRole.Field && c.Editable).ToList();
            var groupColumns = columns.Where(c => GroupKindOf(c.Header) is not null).GroupBy(c => GroupKindOf(c.Header)!).ToDictionary(g => g.Key, g => g.First().Index);
            var customColumns = columns.Where(c => c.Role == ColumnRole.NotStored && customHeaders.Contains(ExcelColumns.Norm(c.Header)))
                .GroupBy(c => ExcelColumns.Norm(c.Header)).ToDictionary(g => g.Key, g => g.First().Index);
            var ignored = columns.Where(c => c.Role == ColumnRole.NotStored && c.Header.Trim().Length > 0 && GroupKindOf(c.Header) is null
                && !customColumns.ContainsKey(ExcelColumns.Norm(c.Header))).Select(c => c.Header.Trim()).ToList();

            var result = new List<SourceRow>();
            for (var i = 0; i < rows.Count; i++)
            {
                var cells = rows[i];
                string? Get(int? index) => index is { } x && x < cells.Count ? ValueNormalizer.CleanText(cells[x]) : null;
                var t = Get(tag);
                if (t is null) continue;
                var values = new Dictionary<FieldDef, string>();
                foreach (var f in fields)
                {
                    var v = Get(f.Index);
                    if (v is not null) values[f.Field!] = v;
                }
                var groups = new Dictionary<string, string>();
                foreach (var (kind, index) in groupColumns)
                    if (Get(index) is { } code) groups[kind] = code.ToUpperInvariant();
                var custom = new Dictionary<string, string>();
                foreach (var (norm, index) in customColumns)
                    if (Get(index) is { } v) custom[norm] = v;
                result.Add(new SourceRow(i + 1, t, Get(discipline), Get(description), values, groups, custom));
            }
            return (result, ignored, lines);
        }
    }

    /// <summary>The first visible list with an EQUIP NO header: an Excel table, or a header row in the first 15 rows.</summary>
    private static (List<string> Headers, List<List<string>> Rows)? FindList(XLWorkbook wb)
    {
        foreach (var ws in wb.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible))
        {
            var table = ws.Tables.FirstOrDefault(t => t.HeadersRow().Cells().Any(c => IsTagHeader(WorkbookFormat.CellText(c))));
            if (table is not null)
            {
                var headers = table.HeadersRow().Cells().Select(c => WorkbookFormat.CellText(c)).ToList();
                var rows = table.DataRange?.Rows().Select(r => r.Cells().Select(WorkbookFormat.CellText).ToList()).ToList() ?? [];
                return (headers, rows);
            }
            var used = ws.RangeUsed();
            if (used is null) continue;
            for (var r = used.FirstRow().RowNumber(); r <= Math.Min(used.FirstRow().RowNumber() + 15, used.LastRow().RowNumber()); r++)
            {
                var row = ws.Row(r);
                if (!row.CellsUsed().Any(c => IsTagHeader(WorkbookFormat.CellText(c)))) continue;
                var first = used.FirstColumn().ColumnNumber();
                var last = used.LastColumn().ColumnNumber();
                var headers = Enumerable.Range(first, last - first + 1).Select(c => WorkbookFormat.CellText(ws.Cell(r, c))).ToList();
                var rows = new List<List<string>>();
                for (var d = r + 1; d <= used.LastRow().RowNumber(); d++)
                {
                    var cells = Enumerable.Range(first, last - first + 1).Select(c => WorkbookFormat.CellText(ws.Cell(d, c))).ToList();
                    if (cells.All(string.IsNullOrWhiteSpace)) break;
                    rows.Add(cells);
                }
                return (headers, rows);
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ analysis

    private async Task<List<ImportChange>> AnalyseAsync(List<SourceRow> rows, int unitId, bool lines, bool apply, CancellationToken ct,
        IReadOnlyDictionary<int, string>? decisions = null)
    {
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, ct) ?? throw new Rejected("Unit not found.");
        var query = db.Assets.Include(a => a.Preservation).Include(a => a.GateDocuments).Include(a => a.Pipeline).Include(a => a.FieldValues)
            .Where(a => a.UnitId == unitId && a.IsActive);
        var customDefs = await db.FieldDefinitions.Include(f => f.OptionSet).ThenInclude(o => o!.Values).Where(f => f.IsActive).ToListAsync(ct);
        var assets = await (apply ? query : query.AsNoTracking()).ToListAsync(ct);
        var byTag = assets.Where(a => a.Tag != null).GroupBy(a => ValueNormalizer.ListKey(a.Tag!)).ToDictionary(g => g.Key, g => g.ToList());
        // Lines whose number is written differently (insulation code, size format) are found by their identity.
        var byLineKey = assets.Where(a => a.Type == AssetType.Pipeline && LineNumber.Parse(a.Tag) is not null)
            .GroupBy(a => LineNumber.IdentityKey(LineNumber.Parse(a.Tag)!)).ToDictionary(g => g.Key, g => g.First());
        var disciplines = await db.Disciplines.AsNoTracking().Where(d => d.IsActive).ToListAsync(ct);
        var options = await AssetEditor.LoadActiveOptionsAsync(db, ct);
        var today = SiteClock.Today(clock);
        var groupTypes = await db.GroupTypes.AsNoTracking().Where(t => t.IsActive).ToDictionaryAsync(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase, ct);
        var groups = await (apply ? db.Groups : db.Groups.AsNoTracking()).Include(g => g.GroupType).ToListAsync(ct);
        var memberships = await db.GroupMembers.AsNoTracking().Where(m => m.Asset.UnitId == unitId)
            .Select(m => new { m.AssetId, m.GroupId }).ToListAsync(ct);
        var membersOf = memberships.GroupBy(m => m.AssetId).ToDictionary(g => g.Key, g => g.Select(m => m.GroupId).ToHashSet());
        var newGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var changes = new List<ImportChange>();
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            var key = ValueNormalizer.ListKey(row.Tag);
            ImportChange Change(Asset? a, string field, ImportOutcome outcome, string? reason, string? excel = null, string? current = null, string? newValue = null) => new()
            {
                AssetId = a?.Id, AssetCode = a?.Code, AssetTag = Truncate(row.Tag, 55), RowNumber = row.Number, Field = field,
                ExcelValue = excel, CurrentValue = current, NewValue = newValue, Outcome = outcome, Reason = reason
            };

            var what = lines ? "line number" : "EQUIP NO";
            if (!seen.Add(key)) { changes.Add(Change(null, RowField, ImportOutcome.Rejected, $"This {what} appears more than once in the file; only the first row is used.")); continue; }
            if (row.Tag.Length > 55) { changes.Add(Change(null, RowField, ImportOutcome.Invalid, $"The {what} is longer than 55 characters.")); continue; }

            Asset asset;
            var isNew = false;
            if (byTag.TryGetValue(key, out var matches))
            {
                if (matches.Count > 1)
                {
                    changes.Add(Change(null, RowField, ImportOutcome.Rejected,
                        $"Several active equipment have this tag ({string.Join(", ", matches.Select(m => m.Code))}). Deactivate the wrong one first."));
                    continue;
                }
                asset = matches[0];
                if (asset.Description is null && row.Description is not null)
                {
                    changes.Add(Change(asset, "Description", ImportOutcome.Applied, null, row.Description, null, Truncate(row.Description, 255)));
                    asset.Description = Truncate(row.Description, 255);
                }
            }
            else if (lines)
            {
                var parts = LineNumber.Parse(row.Tag);
                var piping = disciplines.Where(d => d.AssetType == AssetType.Pipeline && !string.IsNullOrWhiteSpace(d.Prefix)).OrderBy(d => d.SortOrder).FirstOrDefault();
                if (parts is null || parts.Unit != unit.Number || piping is null)
                {
                    changes.Add(Change(null, AssetField, ImportOutcome.Invalid,
                        parts is null ? "Not a line number like 39-0218-LS-16\"-61055-P: size and spec cannot be read, the line is not created."
                        : parts.Unit != unit.Number ? $"The line number starts with {parts.Unit}, but this is {unit.Name}."
                        : "There is no pipeline discipline with an Equipment ID prefix."));
                    continue;
                }
                var similar = byLineKey.GetValueOrDefault(LineNumber.IdentityKey(parts));
                var decision = similar is null ? null : decisions?.GetValueOrDefault(row.Number);
                if (similar is not null && decision != "create")
                {
                    var circuit = groups.FirstOrDefault(g => g.GroupType.Name == "Circuit" && (membersOf.GetValueOrDefault(similar.Id)?.Contains(g.Id) ?? false))?.Code;
                    var about = $"{similar.Tag} ({similar.Code}) has the same unit, sequence, service, size and spec; only the way the number is written differs. "
                        + $"P&ID: {similar.Pipeline?.PidNumber ?? "not given"} · circuit: {circuit ?? "none"} · length: {similar.Pipeline?.LengthM?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "not given"} m.";
                    if (decision is not ("update" or "rename"))
                    {
                        changes.Add(Change(similar, PossibleMatchField, ImportOutcome.Conflict,
                            about + " Not loaded: choose in the preview whether it is the same line or a new one, or check the drawing.", row.Tag, similar.Tag));
                        continue;
                    }
                    asset = similar;
                    changes.Add(Change(similar, PossibleMatchField, ImportOutcome.Applied, decision == "rename"
                        ? $"Same line as {similar.Tag}: its line number becomes {row.Tag} and it gets the values of the list."
                        : $"Same line as {similar.Tag}: it gets the values of the list; its line number stays.", row.Tag, similar.Tag, decision == "rename" ? row.Tag : similar.Tag));
                    if (decision == "rename" && apply) similar.Tag = row.Tag;
                }
                else
                {
                    isNew = true;
                    asset = new Asset
                    {
                        Id = Guid.NewGuid(), Type = AssetType.Pipeline, Tag = row.Tag, Description = Truncate(row.Description, 255),
                        DisciplineId = piping.Id, UnitId = unitId, Preservation = new AssetPreservation(),
                        Pipeline = new PipelineDetail { SizeIn = parts.SizeIn, PipeSpec = parts.Spec, Service = parts.Service }
                    };
                    if (apply)
                    {
                        asset.Code = await AssetEditor.NextCodeAsync(db, piping.Prefix!, unit.Number, ct);
                        db.Assets.Add(asset);
                    }
                    byTag[key] = [asset];
                    byLineKey[LineNumber.IdentityKey(parts)] = asset;
                    changes.Add(Change(asset, AssetField, ImportOutcome.Created,
                        $"New line ({parts.SizeText} {parts.Service}, spec {parts.Spec}){(apply ? "" : $"; its Equipment ID ({piping.Prefix!.Trim().ToUpperInvariant()}-EMP-{unit.Number:000}-…) is given when applied")}.",
                        null, null, apply ? asset.Code : null));
                }
            }
            else
            {
                var discipline = MatchDiscipline(row.Discipline, disciplines);
                if (discipline is null)
                {
                    changes.Add(Change(null, AssetField, ImportOutcome.Invalid,
                        row.Discipline is null ? "New tag without a discipline: it cannot be created." : $"Discipline \"{row.Discipline}\" is not in the list: the equipment cannot be created.", row.Discipline));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(discipline.Prefix))
                {
                    changes.Add(Change(null, AssetField, ImportOutcome.Invalid, $"The discipline {discipline.Name} has no Equipment ID prefix yet. Set it first.", row.Discipline));
                    continue;
                }
                isNew = true;
                asset = new Asset
                {
                    Id = Guid.NewGuid(), Type = discipline.AssetType, Tag = row.Tag, Description = Truncate(row.Description, 255),
                    DisciplineId = discipline.Id, UnitId = unitId, Preservation = new AssetPreservation()
                };
                if (apply)
                {
                    asset.Code = await AssetEditor.NextCodeAsync(db, discipline.Prefix, unit.Number, ct);
                    db.Assets.Add(asset);
                }
                byTag[key] = [asset];
                changes.Add(Change(asset, AssetField, ImportOutcome.Created,
                    $"New {discipline.Name} equipment{(apply ? "" : $"; its Equipment ID ({discipline.Prefix.Trim().ToUpperInvariant()}-EMP-{unit.Number:000}-…) is given when applied")}.",
                    row.Discipline, null, apply ? asset.Code : null));
            }

            asset.Preservation ??= new AssetPreservation { AssetId = asset.Id };
            var gateChanged = false;
            var gateClosed = false;
            foreach (var (field, raw) in row.Values)
            {
                if (field.OnlyFor is { } only && only != asset.Type)
                {
                    changes.Add(Change(asset, field.Key, ImportOutcome.Invalid, $"{field.Label} is only for lines.", raw));
                    continue;
                }
                var error = FieldValues.Prepare(field, raw, AssetEditor.OptionsFor(field, options), out var value);
                var current = FieldValues.Read(field, asset);
                if (error is not null) { changes.Add(Change(asset, field.Key, ImportOutcome.Invalid, error, raw, current)); continue; }
                var newText = FieldValues.Format(value);
                if (FieldValues.Same(field, current, newText)) continue;
                FieldValues.Write(field, asset, value);
                changes.Add(Change(asset, field.Key, ImportOutcome.Applied, current is null ? null : "Overwritten with the value of the list.", raw, current, newText));
                if (PreservationFields.IsGateStatus(field))
                {
                    gateChanged = true;
                    gateClosed |= AssetPreservation.IsCompleted(newText);
                }
            }

            // Custom field columns (matched by the field name, for the register of the asset).
            foreach (var (norm, raw) in row.Custom)
            {
                var def = customDefs.FirstOrDefault(d => d.AssetType == asset.Type && ExcelColumns.Norm(d.Label) == norm);
                if (def is null) continue;
                var before = asset.FieldValues.FirstOrDefault(v => v.FieldDefinitionId == def.Id) is { } fv ? CustomFields.CustomFieldService.Format(def, fv) : null;
                if (apply)
                {
                    var error = customFields.Write(def, asset, null, raw);
                    if (error is not null) { changes.Add(Change(asset, def.Label, ImportOutcome.Invalid, error, raw, before)); continue; }
                    var after = asset.FieldValues.First(v => v.FieldDefinitionId == def.Id);
                    var text = CustomFields.CustomFieldService.Format(def, after);
                    if (text != before) changes.Add(Change(asset, def.Label, ImportOutcome.Applied, before is null ? null : "Overwritten with the value of the list.", raw, before, text));
                }
                else
                {
                    var probe = new Asset { Id = asset.Id, FieldValues = [] };
                    var error = customFields.Write(def, probe, null, raw);
                    if (error is not null) { changes.Add(Change(asset, def.Label, ImportOutcome.Invalid, error, raw, before)); continue; }
                    var text = CustomFields.CustomFieldService.Format(def, probe.FieldValues[0]);
                    if (text != before) changes.Add(Change(asset, def.Label, ImportOutcome.Applied, before is null ? null : "Overwritten with the value of the list.", raw, before, text));
                }
            }

            // Circuit / Workpack / System columns: the asset goes into that group (created when it does not exist yet)
            // and leaves any other group of the same kind.
            foreach (var (kind, code) in row.Groups)
            {
                if (!groupTypes.TryGetValue(kind, out var typeId)) continue;
                if (code.Length > 50) { changes.Add(Change(asset, kind, ImportOutcome.Invalid, $"The {kind.ToLowerInvariant()} code is longer than 50 characters.", code)); continue; }
                var group = groups.FirstOrDefault(g => string.Equals(g.Code, code, StringComparison.OrdinalIgnoreCase));
                if (group is not null && (group.GroupTypeId != typeId || (group.UnitId is { } gu && gu != unitId)))
                {
                    changes.Add(Change(asset, kind, ImportOutcome.Invalid,
                        $"{code} is already used by a {group.GroupType.Name.ToLowerInvariant()}{(group.UnitId != unitId ? " of another unit" : "")}.", code));
                    continue;
                }
                var mine = membersOf.GetValueOrDefault(asset.Id) ?? [];
                var sameKind = groups.Where(g => g.GroupTypeId == typeId && mine.Contains(g.Id)).ToList();
                if (group is not null && sameKind.Count == 1 && sameKind[0].Id == group.Id) continue;
                if (group is null)
                {
                    newGroups.Add(code);
                    group = new AssetGroup { GroupTypeId = typeId, Code = code, Name = code, UnitId = unitId, GroupType = null! };
                    groups.Add(group);
                    if (apply) { group.GroupType = await db.GroupTypes.FirstAsync(t => t.Id == typeId, ct); db.Groups.Add(group); }
                    else group.GroupType = new GroupType { Id = typeId, Name = kind };
                }
                if (apply)
                {
                    foreach (var old in sameKind)
                        db.GroupMembers.Remove(await db.GroupMembers.FirstAsync(m => m.GroupId == old.Id && m.AssetId == asset.Id, ct));
                    db.GroupMembers.Add(new AssetGroupMember { Group = group, AssetId = asset.Id, AddedAtUtc = clock.GetUtcNow().UtcDateTime, AddedBy = audit.UserName ?? "" });
                }
                changes.Add(Change(asset, kind, ImportOutcome.Applied,
                    newGroups.Remove(code) ? $"New {kind.ToLowerInvariant()} {code}: it is created." : sameKind.Count > 0 ? $"Moved from {string.Join(", ", sameKind.Select(g => g.Code))}." : null,
                    code, sameKind.Count > 0 ? string.Join(", ", sameKind.Select(g => g.Code)) : null, code));
            }

            var p = asset.Preservation;
            var outcome = PreservationRules.Apply(p, gateChanged, gateClosed, today);
            if (outcome.ProposedCurrentGate is { } proposed && (isNew || (int)proposed > (int)p.CurrentGate))
            {
                changes.Add(Change(asset, nameof(AssetPreservation.CurrentGate), ImportOutcome.Derived, "Highest completed gate.",
                    current: FieldValues.CurrentGateText(p.CurrentGate), newValue: FieldValues.CurrentGateText(proposed)));
                p.CurrentGate = proposed;
            }
            if (outcome.ProposedLastFieldActionDate is { } lfad) p.LastFieldActionDate = lfad;
            AssetEditor.EnsureDocumentRows(asset);
        }
        return changes;
    }

    /// <summary>Exact name first (case and spaces ignored), then the part before "/" ("Static / Mechanical" → Static).</summary>
    private static Discipline? MatchDiscipline(string? text, List<Discipline> disciplines)
    {
        if (text is null) return null;
        var key = ValueNormalizer.ListKey(text);
        return disciplines.FirstOrDefault(d => ValueNormalizer.ListKey(d.Name) == key)
               ?? disciplines.FirstOrDefault(d => ValueNormalizer.ListKey(d.Name) == ValueNormalizer.ListKey(text.Split('/')[0]));
    }

    private static void Fill(ImportRun run, int rowsInFile, List<ImportChange> changes, List<string> ignored)
    {
        foreach (var c in changes)
        {
            c.Resolution = c.Outcome is ImportOutcome.Invalid or ImportOutcome.Conflict or ImportOutcome.Rejected or ImportOutcome.Warning
                ? IssueResolution.Open : IssueResolution.None;
            run.Changes.Add(c);
        }
        run.RowsInFile = rowsInFile;
        run.AppliedCount = changes.Count(c => c.Outcome is ImportOutcome.Applied or ImportOutcome.Derived or ImportOutcome.Created);
        run.IssueCount = changes.Count(c => c.Resolution == IssueResolution.Open && c.Outcome != ImportOutcome.Warning);
        run.WarningCount = changes.Count(c => c.Outcome == ImportOutcome.Warning);
        run.Message = ignored.Count > 0 ? $"Columns not loaded (not stored by the app or readings): {string.Join(", ", ignored.Distinct())}." : null;
        if (run.Message?.Length > 4000) run.Message = run.Message[..3990] + "…";
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
