using PresApp.Api.Piping;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Auth;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Common;
using PresApp.Domain.Monitoring;
using PresApp.Domain.Rules;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Assets;

/// <summary>
/// Write side of assets. Every change is checked field by field: the value the user saw must still be
/// the stored value, otherwise the field is reported as a conflict and nothing is saved.
/// Audit rows are written by the save interceptor in the same transaction.
/// </summary>
public sealed class AssetEditor(PresAppDbContext db, AssetQueries queries, AuditContext audit, TimeProvider clock)
{
    public const string PreservedAtField = "PreservedAt";
    /// <summary>Bulk edit pseudo-field: the dashboard category (value = category id, empty clears it).</summary>
    public const string CategoryField = "DashboardCategory";

    public async Task<PatchResponse> PatchAsync(Guid id, PatchRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request.Changes is not { Count: > 0 }) throw ApiProblem.Invalid("No changes were sent.");
        var asset = await queries.LoadAsync(id, tracking: true, ct);
        var options = await ActiveOptionsAsync(ct);

        var conflicts = new List<FieldConflict>();
        var errors = new Dictionary<string, string[]>();
        var changed = new List<FieldDef>();

        foreach (var change in request.Changes)
        {
            var field = PreservationFields.Find(change.Field);
            if (field is null || !field.IsEditable || (field.OnlyFor is { } only && only != asset.Type)) { errors[change.Field] = ["This field cannot be edited."]; continue; }
            if (!user.HasPermission(field.Permission)) throw ApiProblem.Forbidden($"Your role cannot edit {field.Label}.");
            if (!asset.IsActive && !user.HasPermission(Permissions.AssetsManage))
                throw ApiProblem.Forbidden("The asset is inactive. Only an administrator can edit it.");

            var current = FieldValues.Read(field, asset);
            if (!FieldValues.Same(field, current, change.From))
            {
                conflicts.Add(new FieldConflict(field.Key, field.Label, change.From, current));
                continue;
            }
            var error = FieldValues.Prepare(field, asset.Type, change.To, options, out var value);
            if (error is not null) { errors[field.Key] = [error]; continue; }
            if (FieldValues.Same(field, current, FieldValues.Format(value))) continue;

            FieldValues.Write(field, asset, value);
            changed.Add(field);
        }

        if (conflicts.Count > 0)
        {
            throw ApiProblem.Conflict(
                $"Someone else changed {string.Join(", ", conflicts.Select(c => c.Label))} while you were editing. Nothing was saved; check the current values and try again.",
                conflicts);
        }
        if (errors.Count > 0) throw ApiProblem.Invalid(errors.Values.First()[0], errors);

        // A new line number re-reads size, spec and service (unless they were edited in the same save).
        if (asset.Type == AssetType.Pipeline && changed.Any(f => f.Key == "Tag") && LineNumber.Parse(asset.Tag) is { } parts)
        {
            asset.Pipeline ??= new PipelineDetail { AssetId = asset.Id };
            if (!changed.Any(f => f.Key == nameof(PipelineDetail.SizeIn))) asset.Pipeline.SizeIn = parts.SizeIn;
            if (!changed.Any(f => f.Key == nameof(PipelineDetail.PipeSpec))) asset.Pipeline.PipeSpec = parts.Spec;
            if (!changed.Any(f => f.Key == nameof(PipelineDetail.Service))) asset.Pipeline.Service = parts.Service;
        }

        var outcome = PreservationRules.Apply(asset.Preservation!,
            gateStatusChanged: changed.Any(PreservationFields.IsGateStatus),
            fieldActionHappened: changed.Any(f => PreservationRules.IsFieldAction(f.Key)),
            SiteClock.Today(clock));
        EnsureDocumentRows(asset);

        await db.SaveChangesAsync(ct);
        return new PatchResponse(await queries.ProfileAsync(id, ct), ToProposals(outcome));
    }

    /// <summary>Sets the Hold / N/A dashboard category of an asset (an active category of the asset's unit, or none).</summary>
    public async Task<AssetProfile> SetCategoryAsync(Guid id, SetCategoryRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var asset = await queries.LoadAsync(id, tracking: true, ct);
        if (!asset.IsActive && !user.HasPermission(Permissions.AssetsManage))
            throw ApiProblem.Forbidden("The asset is inactive. Only an administrator can edit it.");
        var p = asset.Preservation!;
        if (p.DashboardCategoryId != request.Expected)
            throw ApiProblem.Conflict("Someone else changed the dashboard category while you were editing. Check it and try again.");
        if (request.CategoryId is { } categoryId
            && !await db.DashboardCategories.AnyAsync(c => c.Id == categoryId && c.UnitId == asset.UnitId && c.IsActive && c.Code != null, ct))
            throw ApiProblem.Invalid("That category is not an active category of this unit.");
        if (p.DashboardCategoryId == request.CategoryId) return await queries.ProfileAsync(id, ct);
        p.DashboardCategoryId = request.CategoryId;
        p.DashboardCategory = null;
        await db.SaveChangesAsync(ct);
        return await queries.ProfileAsync(id, ct);
    }

    public async Task<AssetProfile> SetCurrentGateAsync(Guid id, SetCurrentGateRequest request, CancellationToken ct)
    {
        if (!FieldValues.TryParseCurrentGate(request.Value, out var gate)) throw ApiProblem.Invalid($"\"{request.Value}\" is not a gate.");
        var asset = await queries.LoadAsync(id, tracking: true, ct);
        var p = asset.Preservation!;
        if (request.Expected is not null && FieldValues.TryParseCurrentGate(request.Expected, out var expected) && expected != p.CurrentGate)
        {
            throw ApiProblem.Conflict($"Current Gate was changed to {FieldValues.CurrentGateText(p.CurrentGate)} by someone else. Check it and try again.");
        }
        p.CurrentGate = gate;
        await db.SaveChangesAsync(ct);
        return await queries.ProfileAsync(id, ct);
    }

    public async Task<AssetProfile> MarkPreservedAsync(Guid id, DateOnly? preservedOn, ClaimsPrincipal user, CancellationToken ct)
    {
        var asset = await queries.LoadAsync(id, tracking: true, ct);
        await ApplyPreservedAsync(asset, preservedOn, user.Identity?.Name ?? "", ct);
        await db.SaveChangesAsync(ct);
        return await queries.ProfileAsync(id, ct);
    }

    /// <summary>Sets (or clears) the official start of POST monitoring and reclassifies the asset's readings.</summary>
    private async Task ApplyPreservedAsync(Asset asset, DateOnly? preservedOn, string by, CancellationToken ct)
    {
        var p = asset.Preservation!;
        if (preservedOn is { } day && day > SiteClock.Today(clock)) throw ApiProblem.Invalid("The preservation date cannot be in the future.");
        p.PreservedAtUtc = preservedOn is { } d
            ? TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(TimeOnly.MinValue), SiteClock.Dubai)
            : null;
        p.PreservedBy = preservedOn is null ? null : by;

        var readings = await db.Readings.Where(r => r.AssetId == asset.Id).ToListAsync(ct);
        foreach (var r in readings)
        {
            r.Phase = p.PreservedAtUtc is { } start && r.MeasuredAtUtc >= start ? MonitoringPhase.Post : MonitoringPhase.During;
        }
    }

    public async Task<AssetProfile> SetGateDocumentAsync(Guid id, int gate, GateDocRequest request, CancellationToken ct)
    {
        if (gate is < 1 or > 8) throw ApiProblem.Invalid("Gate must be 1 to 8.");
        if (!Enum.TryParse<DocumentationStatus>(request.Status, true, out var status)) throw ApiProblem.Invalid($"\"{request.Status}\" is not a documentation status.");
        var reference = ValueNormalizer.CleanText(request.Reference);
        var note = ValueNormalizer.CleanText(request.Note);
        if (reference?.Length > 255 || note?.Length > 1000) throw ApiProblem.Invalid("Reference allows 255 characters and note 1,000.");

        var asset = await queries.LoadAsync(id, tracking: true, ct);
        var doc = asset.GateDocuments.FirstOrDefault(d => d.Gate == gate);
        if (doc is null)
        {
            doc = new GateDocumentation { AssetId = id, Gate = gate };
            asset.GateDocuments.Add(doc);
        }
        doc.Status = status;
        doc.ReceivedOn = status == DocumentationStatus.Received ? request.ReceivedOn ?? SiteClock.Today(clock) : null;
        doc.Reference = reference;
        doc.Note = note;
        await db.SaveChangesAsync(ct);
        return await queries.ProfileAsync(id, ct);
    }

    public async Task<AssetProfile> CreateAsync(CreateAssetRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<AssetType>(request.Type, true, out var type)) throw ApiProblem.Invalid("Unknown asset type.");
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == request.UnitId && u.IsActive, ct) ?? throw ApiProblem.Invalid("Choose a valid unit.");
        var discipline = await db.Disciplines.FirstOrDefaultAsync(d => d.Id == request.DisciplineId && d.IsActive, ct)
                         ?? throw ApiProblem.Invalid("Choose a valid discipline.");
        if (string.IsNullOrWhiteSpace(discipline.Prefix))
            throw ApiProblem.Invalid($"The discipline {discipline.Name} has no Equipment ID prefix yet. An administrator must set it first.");

        if (discipline.AssetType != type) throw ApiProblem.Invalid($"{discipline.Name} is not a discipline for this kind of asset.");
        var tag = ValueNormalizer.CleanText(request.Tag);
        var description = ValueNormalizer.CleanText(request.Description);
        if (tag is null) throw ApiProblem.Invalid(type == AssetType.Pipeline ? "Enter the line number." : "Enter the equipment tag.");
        var line = type == AssetType.Pipeline ? LineNumber.Parse(tag) : null;
        if (line is not null && line.Unit != unit.Number)
            throw ApiProblem.Invalid($"The line number starts with {line.Unit}, but this is {unit.Name}.");
        if (request.LengthM is < 0) throw ApiProblem.Invalid("The length cannot be negative.");
        var pid = ValueNormalizer.CleanText(request.PidNumber);
        if (pid?.Length > PipelineDetail.PidLength) throw ApiProblem.Invalid($"The P&ID number allows at most {PipelineDetail.PidLength} characters.");
        if (tag.Length > 55 || description?.Length > 255) throw ApiProblem.Invalid("Tag allows 55 characters and description 255.");
        string? priority = null;
        if (request.Priority is not null)
        {
            var (value, matched) = ValueNormalizer.Canonical(request.Priority, (await ActiveOptionsAsync(ct)).GetValueOrDefault(OptionSetKeys.Priority) ?? []);
            if (!matched) throw ApiProblem.Invalid($"\"{request.Priority}\" is not a priority.");
            priority = value;
        }
        if (await db.Assets.AnyAsync(a => a.UnitId == unit.Id && a.Tag == tag && a.IsActive, ct))
            throw ApiProblem.Conflict($"An active asset with tag {tag} already exists in {unit.Name}.");
        if (line is not null && !request.AllowSimilar)
        {
            var key = LineNumber.IdentityKey(line);
            var similar = (await db.Assets.AsNoTracking().Where(a => a.UnitId == unit.Id && a.IsActive && a.Type == AssetType.Pipeline)
                    .Select(a => new { a.Id, a.Code, a.Tag, Pid = a.Pipeline!.PidNumber }).ToListAsync(ct))
                .FirstOrDefault(a => LineNumber.Parse(a.Tag) is { } p && LineNumber.IdentityKey(p) == key);
            if (similar is not null)
                throw new ApiProblem(409, "Possible duplicate",
                    $"{similar.Tag} ({similar.Code}, P&ID {similar.Pid ?? "not given"}) is the same line written differently. Create it anyway only if it is really a different line.",
                    new { similar = new { similar.Id, similar.Code, similar.Tag, similar.Pid } });
        }

        var code = await NextCodeAsync(db, discipline.Prefix, unit.Number, ct);
        var asset = new Asset
        {
            Id = Guid.NewGuid(), Type = type, Code = code, Tag = tag, Description = description,
            DisciplineId = discipline.Id, UnitId = unit.Id, Priority = priority,
            Preservation = new AssetPreservation(),
            Pipeline = type == AssetType.Pipeline
                ? new PipelineDetail { SizeIn = line?.SizeIn, PipeSpec = line?.Spec, Service = line?.Service, LengthM = request.LengthM, PidNumber = pid }
                : null
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync(ct);
        return await queries.ProfileAsync(asset.Id, ct);
    }

    /// <summary>Next free ID for (prefix, unit): e.g. ST-EMP-039-132. Skips numbers already taken.</summary>
    internal static async Task<string> NextCodeAsync(PresAppDbContext db, string prefix, int unitNumber, CancellationToken ct)
    {
        prefix = prefix.Trim().ToUpperInvariant();
        // Local first: several assets may be created in one save (onboarding).
        var seq = db.EquipmentIdSequences.Local.FirstOrDefault(s => s.Prefix == prefix && s.UnitNumber == unitNumber)
                  ?? await db.EquipmentIdSequences.FirstOrDefaultAsync(s => s.Prefix == prefix && s.UnitNumber == unitNumber, ct);
        if (seq is null)
        {
            seq = new EquipmentIdSequence { Prefix = prefix, UnitNumber = unitNumber };
            db.EquipmentIdSequences.Add(seq);
        }
        string code;
        do
        {
            seq.LastNumber++;
            code = EquipmentIdFormat.Build(prefix, unitNumber, seq.LastNumber);
        } while (db.Assets.Local.Any(a => a.Code == code) || await db.Assets.AnyAsync(a => a.Code == code, ct));
        return code;
    }

    public async Task<AssetProfile> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        var asset = await queries.LoadAsync(id, tracking: true, ct);
        asset.IsActive = active;
        await db.SaveChangesAsync(ct);
        return await queries.ProfileAsync(id, ct);
    }

    // ------------------------------------------------------------------ lines in bulk

    /// <summary>
    /// Pasted line list (one per row: line number, optionally a tab or comma and the length in metres).
    /// Preview shows what each row becomes; apply creates the new lines with their Equipment ID.
    /// </summary>
    public async Task<BulkLinesResult> BulkLinesAsync(int unitId, string text, bool apply, CancellationToken ct)
    {
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId, ct) ?? throw ApiProblem.NotFound("Unit not found.");
        var discipline = await db.Disciplines.Where(d => d.AssetType == AssetType.Pipeline && d.IsActive && d.Prefix != null)
            .OrderBy(d => d.SortOrder).FirstOrDefaultAsync(ct) ?? throw ApiProblem.Invalid("There is no pipeline discipline with an ID prefix.");
        var existing = (await db.Assets.Where(a => a.UnitId == unitId && a.IsActive && a.Tag != null).Select(a => new { a.Id, a.Code, a.Tag }).ToListAsync(ct))
            .GroupBy(a => ValueNormalizer.ListKey(a.Tag!)).ToDictionary(g => g.Key, g => g.First());
        var byLineKey = (await db.Assets.Where(a => a.UnitId == unitId && a.IsActive && a.Type == AssetType.Pipeline)
                .Select(a => new { a.Id, a.Code, a.Tag, Pid = a.Pipeline!.PidNumber }).ToListAsync(ct))
            .Where(a => LineNumber.Parse(a.Tag) is not null)
            .GroupBy(a => LineNumber.IdentityKey(LineNumber.Parse(a.Tag)!)).ToDictionary(g => g.Key, g => g.First());
        var newKeys = new HashSet<string>();

        var codes = (await db.LineCodes.AsNoTracking().Where(c => c.IsActive).Select(c => new { c.Kind, c.Code }).ToListAsync(ct))
            .Select(c => (c.Kind, c.Code)).ToHashSet();
        var specs = await PipeSpecLookup.LoadAsync(db, ct);
        string? Unknown(LineNumberParts p)
        {
            var missing = new List<string>();
            if (!codes.Contains((LineCodeKind.Service, p.Service))) missing.Add($"Service {p.Service} is not in the line codes.");
            if (!specs.Has(p.Spec)) missing.Add($"Pipe spec {p.Spec} is not in the pipe spec list: rating, face and gasket stay empty until it is added.");
            return missing.Count == 0 ? null : string.Join(" ", missing);
        }
        var rows = new List<BulkLineRow>();
        var seen = new HashSet<string>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 3000) throw ApiProblem.Invalid("Paste at most 3,000 lines at a time.");
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0) continue;
            var cells = raw.Split(raw.Contains('\t') ? '\t' : raw.Contains(';') ? ';' : ',').Select(c => c.Trim()).ToArray();
            var tag = ValueNormalizer.CleanText(cells[0]) ?? "";
            if (ExcelHeader(tag)) continue;
            decimal? length = null;
            string? problem = null;
            if (cells.Length > 1 && cells[1].Length > 0)
            {
                if (ValueNormalizer.TryParseDecimal(cells[1].Replace("m", "", StringComparison.OrdinalIgnoreCase), out var l) && l >= 0) length = l;
                else problem = $"Length \"{cells[1]}\" is not a number of metres.";
            }
            var parts = LineNumber.Parse(tag);
            var key = ValueNormalizer.ListKey(tag);
            problem ??= tag.Length > 55 ? "Longer than 55 characters."
                : parts is null ? "Not a line number like 39-0218-LS-16\"-61055-P (size and spec cannot be read)."
                : parts.Unit != unit.Number ? $"Starts with {parts.Unit}, but this is {unit.Name}."
                : !seen.Add(key) ? "Appears twice in the list."
                : null;
            if (problem is not null)
            {
                rows.Add(new BulkLineRow(i + 1, tag, parts?.SizeIn, parts?.Spec, specs.Class(parts?.Spec), specs.Flange(parts?.Spec), parts?.Service, length, "Invalid", problem, null, null));
                continue;
            }
            if (existing.TryGetValue(key, out var found))
            {
                rows.Add(new BulkLineRow(i + 1, tag, parts!.SizeIn, parts.Spec, specs.Class(parts.Spec), specs.Flange(parts.Spec), parts.Service, length, "Exists", "Already in the register.", found.Id, found.Code));
                continue;
            }
            var identity = LineNumber.IdentityKey(parts!);
            if (byLineKey.TryGetValue(identity, out var like))
            {
                rows.Add(new BulkLineRow(i + 1, tag, parts!.SizeIn, parts.Spec, specs.Class(parts.Spec), specs.Flange(parts.Spec), parts.Service, length, "Possible match",
                    $"Same line as {like.Tag} ({like.Code}, P&ID {like.Pid ?? "not given"}) written differently. Not created: check the drawing; create it from New line if it is really different.", like.Id, like.Code));
                continue;
            }
            if (!newKeys.Add(identity))
            {
                rows.Add(new BulkLineRow(i + 1, tag, parts!.SizeIn, parts.Spec, specs.Class(parts.Spec), specs.Flange(parts.Spec), parts.Service, length, "Invalid",
                    "The same line appears twice in the list, written differently.", null, null));
                continue;
            }
            Guid? id = null;
            string? code = null;
            if (apply)
            {
                code = await NextCodeAsync(db, discipline.Prefix!, unit.Number, ct);
                var asset = new Asset
                {
                    Id = Guid.NewGuid(), Type = AssetType.Pipeline, Code = code, Tag = tag, DisciplineId = discipline.Id, UnitId = unit.Id,
                    Preservation = new AssetPreservation(),
                    Pipeline = new PipelineDetail { SizeIn = parts!.SizeIn, PipeSpec = parts.Spec, Service = parts.Service, LengthM = length }
                };
                db.Assets.Add(asset);
                id = asset.Id;
            }
            rows.Add(new BulkLineRow(i + 1, tag, parts!.SizeIn, parts.Spec, specs.Class(parts.Spec), specs.Flange(parts.Spec), parts.Service, length, apply ? "Created" : "New", null, id, code, Unknown(parts)));
        }
        if (apply) await db.SaveChangesAsync(ct);
        return new BulkLinesResult(rows.Count(r => r.Outcome is "New" or "Created"), rows.Count(r => r.Outcome == "Exists"), rows.Count(r => r.Outcome == "Invalid"), rows);
    }

    private static bool ExcelHeader(string cell) => ValueNormalizer.ListKey(cell) is "lineno" or "linenumber" or "line" or "lineno." or "line#";

    // ------------------------------------------------------------------ bulk edit

    public async Task<BulkResult> BulkAsync(BulkRequest request, bool apply, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request.AssetIds is not { Count: > 0 }) throw ApiProblem.Invalid("Select at least one asset.");
        if (request.AssetIds.Count > 2000) throw ApiProblem.Invalid("Select at most 2,000 assets at a time.");
        if (request.Field.Equals(CategoryField, StringComparison.OrdinalIgnoreCase)) return await BulkCategoryAsync(request, apply, user, ct);

        var isPreserved = request.Field.Equals(PreservedAtField, StringComparison.OrdinalIgnoreCase);
        var field = isPreserved ? null : PreservationFields.Find(request.Field);
        if (!isPreserved && (field is null || !field.IsEditable || !field.BulkEditable)) throw ApiProblem.Invalid("This field cannot be bulk edited.");
        var permission = isPreserved ? Permissions.AssetsMarkPreserved : field!.Permission;
        if (!user.HasPermission(permission)) throw ApiProblem.Forbidden("Your role cannot change this field.");

        object? newValue = null;
        DateOnly? preservedOn = null;
        if (isPreserved)
        {
            if (!string.IsNullOrWhiteSpace(request.Value))
            {
                if (!DateOnly.TryParse(request.Value, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    throw ApiProblem.Invalid("The preservation date must be yyyy-MM-dd.");
                preservedOn = d;
            }
        }

        var ids = request.AssetIds.Distinct().ToList();
        var assets = await db.Assets.Include(a => a.Preservation).Include(a => a.GateDocuments).Include(a => a.Pipeline)
            .Where(a => ids.Contains(a.Id)).ToListAsync(ct);

        // Lines and equipment can use different lists: the value is checked against the list of each type.
        var prepared = new Dictionary<AssetType, (string? Error, object? Value)>();
        if (!isPreserved)
        {
            var options = await ActiveOptionsAsync(ct);
            foreach (var type in assets.Select(a => a.Type).Distinct().DefaultIfEmpty(AssetType.Equipment))
            {
                var error = FieldValues.Prepare(field!, type, request.Value, options, out var v);
                prepared[type] = (error, v);
            }
            if (prepared.Values.All(p => p.Error is not null)) throw ApiProblem.Invalid(prepared.Values.First().Error!);
            newValue = prepared.Values.First(p => p.Error is null).Value;
        }
        var newText = isPreserved ? preservedOn?.ToString("yyyy-MM-dd") : FieldValues.Format(newValue);
        var rows = new List<BulkRow>();
        var today = SiteClock.Today(clock);
        audit.Source = ChangeSource.Bulk;

        foreach (var asset in assets.OrderBy(a => a.Tag ?? a.Code, NaturalComparer.Instance))
        {
            asset.Preservation ??= new AssetPreservation { AssetId = asset.Id };
            var current = isPreserved
                ? (asset.Preservation.PreservedAtUtc is { } pu ? SiteClock.DateOf(pu).ToString("yyyy-MM-dd") : null)
                : FieldValues.Read(field!, asset);

            if (!asset.IsActive)
            {
                rows.Add(new BulkRow(asset.Id, asset.Code, asset.Tag, current, newText, "Skipped", "Inactive asset"));
                continue;
            }
            if (field?.OnlyFor is { } only && only != asset.Type)
            {
                rows.Add(new BulkRow(asset.Id, asset.Code, asset.Tag, null, newText, "Skipped", "Only for pipelines"));
                continue;
            }
            if (!isPreserved && prepared.TryGetValue(asset.Type, out var forType))
            {
                if (forType.Error is not null)
                {
                    rows.Add(new BulkRow(asset.Id, asset.Code, asset.Tag, current, newText, "Skipped", forType.Error));
                    continue;
                }
                newValue = forType.Value;
            }
            var assetText = isPreserved ? newText : FieldValues.Format(newValue);
            var same = isPreserved ? current == newText : FieldValues.Same(field!, current, assetText);
            if (same)
            {
                rows.Add(new BulkRow(asset.Id, asset.Code, asset.Tag, current, assetText, "No change", null));
                continue;
            }
            rows.Add(new BulkRow(asset.Id, asset.Code, asset.Tag, current, assetText, "Change", null));
            if (!apply) continue;

            if (isPreserved) await ApplyPreservedAsync(asset, preservedOn, user.Identity?.Name ?? "", ct);
            else
            {
                FieldValues.Write(field!, asset, newValue);
                // Bulk edit sets automatic milestones but never rewrites Current Gate (as in the Power App).
                PreservationRules.Apply(asset.Preservation, PreservationFields.IsGateStatus(field!), PreservationRules.IsFieldAction(field!.Key), today);
                EnsureDocumentRows(asset);
            }
        }

        foreach (var missing in ids.Except(assets.Select(a => a.Id)))
        {
            rows.Add(new BulkRow(missing, "?", null, null, newText, "Skipped", "Asset not found"));
        }

        if (apply) await db.SaveChangesAsync(ct);
        return new BulkResult(rows.Count(r => r.Outcome == "Change"), rows.Count(r => r.Outcome == "No change"),
            rows.Count(r => r.Outcome == "Skipped"), rows);
    }

    /// <summary>
    /// Bulk assignment of the dashboard category (Hold / N/A). The value is the category id, or empty to clear it.
    /// Same rules as the asset page: the category must be active, have a code and belong to the asset's unit;
    /// pipelines are not counted in the dashboard tables, so they are skipped.
    /// </summary>
    private async Task<BulkResult> BulkCategoryAsync(BulkRequest request, bool apply, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!user.HasPermission(Permissions.AssetsEdit)) throw ApiProblem.Forbidden("Your role cannot change the dashboard category.");

        Domain.Reporting.DashboardCategory? category = null;
        if (!string.IsNullOrWhiteSpace(request.Value))
        {
            if (!int.TryParse(request.Value, out var categoryId)) throw ApiProblem.Invalid("Choose a category from the list.");
            category = await db.DashboardCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct);
            if (category is null || !category.IsActive || category.Code is null)
                throw ApiProblem.Invalid("That category is not active. Refresh the page and choose it again.");
        }

        var ids = request.AssetIds.Distinct().ToList();
        var assets = await db.Assets.Include(a => a.Preservation).ThenInclude(p => p!.DashboardCategory)
            .Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        var newText = CategoryText(category);
        var rows = new List<BulkRow>();
        audit.Source = ChangeSource.Bulk;

        foreach (var asset in assets.OrderBy(a => a.Tag ?? a.Code, NaturalComparer.Instance))
        {
            asset.Preservation ??= new AssetPreservation { AssetId = asset.Id };
            var p = asset.Preservation;
            var current = CategoryText(p.DashboardCategory);
            string? skip = !asset.IsActive ? "Inactive asset"
                : asset.Type == AssetType.Pipeline ? "Pipelines have no dashboard category"
                : category is not null && category.UnitId != asset.UnitId ? "The category belongs to another unit"
                : null;
            if (skip is not null) { rows.Add(new BulkRow(asset.Id, asset.Code, asset.Tag, current, newText, "Skipped", skip)); continue; }
            if (p.DashboardCategoryId == category?.Id) { rows.Add(new BulkRow(asset.Id, asset.Code, asset.Tag, current, newText, "No change", null)); continue; }

            rows.Add(new BulkRow(asset.Id, asset.Code, asset.Tag, current, newText, "Change", null));
            if (!apply) continue;
            p.DashboardCategoryId = category?.Id;
            p.DashboardCategory = null;
        }
        foreach (var missing in ids.Except(assets.Select(a => a.Id)))
        {
            rows.Add(new BulkRow(missing, "?", null, null, newText, "Skipped", "Asset not found"));
        }

        if (apply) await db.SaveChangesAsync(ct);
        return new BulkResult(rows.Count(r => r.Outcome == "Change"), rows.Count(r => r.Outcome == "No change"),
            rows.Count(r => r.Outcome == "Skipped"), rows);
    }

    private static string? CategoryText(Domain.Reporting.DashboardCategory? c) =>
        c is null ? null : $"{(c.Section == Domain.Reporting.CategorySection.Hold ? "Hold" : "N/A")} {c.Code} – {c.Label}";

    // ------------------------------------------------------------------ readings

    public async Task<AddReadingsResponse> AddReadingsAsync(Guid id, AddReadingsRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var values = request.Values?.Where(v => v is not null).ToList() ?? [];
        if (values.Count == 0) throw ApiProblem.Invalid("Enter and confirm at least one reading.");
        var now = clock.GetUtcNow().UtcDateTime;
        var measuredAt = DateTime.SpecifyKind(request.MeasuredAtUtc, DateTimeKind.Utc);
        if (measuredAt > now.AddMinutes(10)) throw ApiProblem.Invalid("The measurement time cannot be in the future.");
        if (measuredAt < now.AddYears(-2)) throw ApiProblem.Invalid("The measurement time is more than two years ago.");
        var note = ValueNormalizer.CleanText(request.Note);
        if (note?.Length > 4000) throw ApiProblem.Invalid("The note allows at most 4,000 characters.");

        var asset = await queries.LoadAsync(id, tracking: true, ct);
        if (!asset.IsActive) throw ApiProblem.Forbidden("The asset is inactive.");
        var types = await db.MeasurementTypes.Where(t => t.IsActive).ToDictionaryAsync(t => t.Id, ct);
        var p = asset.Preservation!;

        foreach (var v in values)
        {
            if (!types.TryGetValue(v.MeasurementTypeId, out var type)) throw ApiProblem.Invalid("Unknown measurement.");
            if (type.MinValue is { } min && v.Value < min || type.MaxValue is { } max && v.Value > max)
                throw ApiProblem.Invalid($"{type.Label} must be between {type.MinValue?.ToString() ?? "−∞"} and {type.MaxValue?.ToString() ?? "∞"}.");

            db.Readings.Add(new Reading
            {
                AssetId = id, MeasurementTypeId = type.Id, Value = v.Value, MeasuredAtUtc = measuredAt, RecordedAtUtc = now,
                RecordedBy = user.Identity?.Name ?? "", Source = ChangeSource.App, Note = note,
                Phase = p.PreservedAtUtc is { } start && measuredAt >= start ? MonitoringPhase.Post : MonitoringPhase.During
            });

            // The asset shows the reading with the latest measurement time, not the latest entered.
            var newer = await db.Readings.AnyAsync(r => r.AssetId == id && r.MeasurementTypeId == type.Id && r.MeasuredAtUtc > measuredAt, ct);
            if (!newer) SetLatest(p, type.Key, v.Value);
        }
        if (p.LastReadingAtUtc is null || measuredAt > p.LastReadingAtUtc) p.LastReadingAtUtc = measuredAt;

        var outcome = PreservationRules.Apply(p, gateStatusChanged: false, fieldActionHappened: true, SiteClock.DateOf(measuredAt));
        await db.SaveChangesAsync(ct);
        return new AddReadingsResponse(values.Count, ToProposals(outcome), await queries.ProfileAsync(id, ct));
    }

    public async Task<List<ReadingDto>> ReadingsAsync(Guid id, CancellationToken ct) =>
        (await db.Readings.AsNoTracking().Where(r => r.AssetId == id)
            .OrderByDescending(r => r.MeasuredAtUtc).ThenByDescending(r => r.Id)
            .Select(r => new { r, r.MeasurementType })
            .ToListAsync(ct))
        .Select(x => new ReadingDto(x.r.Id, x.MeasurementType.Key, x.MeasurementType.Label, x.r.Value, x.MeasurementType.UnitOfMeasure,
            x.r.MeasuredAtUtc, x.r.RecordedAtUtc, x.r.RecordedBy, x.r.Source.ToString(), x.r.Phase.ToString(), x.r.Note))
        .ToList();

    private static void SetLatest(AssetPreservation p, string key, decimal value)
    {
        switch (key)
        {
            case "PressureBarg": p.PressureBarg = value; break;
            case "G2DewPointC": p.G2DewPointC = value; break;
            case "G3O2Pct": p.G3O2Pct = value; break;
            case "G4N2BlanketBarg": p.G4N2BlanketBarg = value; break;
            case "G6H2S": p.G6H2S = value; break;
            case "G6LelPct": p.G6LelPct = value; break;
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Every Completed gate gets a documentation row (Pending until the paperwork arrives).</summary>
    internal static void EnsureDocumentRows(Asset asset)
    {
        for (var g = 1; g <= 8; g++)
        {
            if (!AssetPreservation.IsCompleted(asset.Preservation!.GetGateStatus(g)) || asset.GateDocuments.Any(d => d.Gate == g)) continue;
            asset.GateDocuments.Add(new GateDocumentation { AssetId = asset.Id, Gate = g, Status = DocumentationStatus.Pending });
        }
    }

    private static Proposals ToProposals(DerivedOutcome o) => new(
        o.ProposedCurrentGate is { } g ? FieldValues.CurrentGateText(g) : null, o.ProposedLastFieldActionDate);

    private Task<Dictionary<string, List<string>>> ActiveOptionsAsync(CancellationToken ct) => LoadActiveOptionsAsync(db, ct);

    /// <summary>Active values of every option set, in display order.</summary>
    internal static async Task<Dictionary<string, List<string>>> LoadActiveOptionsAsync(PresAppDbContext db, CancellationToken ct) =>
        (await db.OptionSets.AsNoTracking()
            .Select(s => new { s.Key, Values = s.Values.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => v.Value).ToList() })
            .ToListAsync(ct))
        .ToDictionary(s => s.Key, s => s.Values, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> OptionsFor(FieldDef field, Dictionary<string, List<string>> options, AssetType type = AssetType.Equipment) =>
        field.OptionSetFor(type) is { } key ? options.GetValueOrDefault(key) ?? [] : [];
}

public static class PrincipalExtensions
{
    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.HasClaim(AuthConstants.PermissionClaim, permission);
}
