using PresApp.Api.Piping;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Common;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Assets;

/// <summary>Read side: registers, profile, search, history, unit summary.</summary>
public sealed class AssetQueries(PresAppDbContext db)
{
    public async Task<List<RegisterRow>> RegisterAsync(int unitId, AssetType type, bool includeInactive, CancellationToken ct)
    {
        var rows = await db.Assets.AsNoTracking()
            .Where(a => a.UnitId == unitId && a.Type == type && (includeInactive || a.IsActive))
            .Select(a => new
            {
                a.Id, a.Code, a.Tag, a.Description, Discipline = a.Discipline.Name, a.Priority, a.IsActive, a.UpdatedAtUtc,
                P = a.Preservation,
                L = a.Pipeline,
                Groups = a.GroupMemberships.Where(m => m.Group.IsActive).Select(m => m.Group.Code).ToList(),
                Circuit = a.GroupMemberships.Where(m => m.Group.IsActive && m.Group.GroupType.Name == "Circuit").Select(m => m.Group.Code).FirstOrDefault(),
                DocsPending = a.GateDocuments.Count(d => d.Status == DocumentationStatus.Pending)
            })
            .ToListAsync(ct);

        // Custom fields marked "show in register" for this asset type.
        // Lines always carry Inspection Done, Cleaning Required and Cleaned (indicators and columns of the Pipeline Register).
        string[] piping = type == AssetType.Pipeline ? [PipingFieldKeys.InspectionDone, PipingFieldKeys.CleaningRequired, PipingFieldKeys.Cleaned] : [];
        var columns = await db.FieldDefinitions.AsNoTracking().Where(f => f.AssetType == type && f.IsActive && (f.ShowInRegister || piping.Contains(f.Key))).ToListAsync(ct);
        var columnIds = columns.Select(c => c.Id).ToList();
        var custom = columns.Count == 0 ? [] : (await db.FieldValues.AsNoTracking()
                .Where(v => columnIds.Contains(v.FieldDefinitionId) && v.Asset.UnitId == unitId && v.Asset.Type == type).ToListAsync(ct))
            .GroupBy(v => v.AssetId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string?>)g.ToDictionary(
                v => columns.First(c => c.Id == v.FieldDefinitionId).Key, v => CustomFields.CustomFieldService.Format(columns.First(c => c.Id == v.FieldDefinitionId), v)));
        var specs = type == AssetType.Pipeline ? await PipeSpecLookup.LoadAsync(db, ct) : new PipeSpecLookup([]);
        return rows
            .OrderBy(r => DisciplineRank(r.Discipline)).ThenBy(r => r.Tag ?? r.Code, NaturalComparer.Instance)
            .Select(r => new RegisterRow(
                r.Id, r.Code, r.Tag, r.Description, r.Discipline, r.Priority,
                FieldValues.CurrentGateText(r.P?.CurrentGate ?? CurrentGate.New),
                Enumerable.Range(1, 8).Select(g => r.P?.GetGateStatus(g)).ToList(),
                r.P?.AssetAvailability, r.P?.PreservationStatusAdnoc, r.P?.ThirdPartyInspectionStatus, r.P?.EmWalkdownStatus,
                r.P?.PreservedByEu, r.P?.ActionBy, r.P?.N2BlanketStatus, r.P?.CurrentActivity, r.P?.PreservationRemarks,
                r.P?.LastFieldActionDate, r.P?.PreservedAtUtc, r.DocsPending, r.IsActive,
                r.P is null || r.UpdatedAtUtc > r.P.UpdatedAtUtc ? r.UpdatedAtUtc : r.P.UpdatedAtUtc,
                r.L == null ? null : new LineInfo(r.L.SizeIn, r.L.PipeSpec, specs.Class(r.L.PipeSpec), specs.Flange(r.L.PipeSpec), specs.Gasket(r.L.PipeSpec), r.L.Service, r.L.PidNumber, r.L.LengthM), r.Groups, r.Circuit, custom.GetValueOrDefault(r.Id),
                r.P?.G1BulkLiquidDrained))
            .ToList();
    }

    public async Task<AssetProfile> ProfileAsync(Guid id, CancellationToken ct)
    {
        var asset = await LoadAsync(id, tracking: false, ct);
        return ToProfile(asset);
    }

    internal async Task<Asset> LoadAsync(Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.Assets
            .Include(a => a.Preservation).ThenInclude(p => p!.DashboardCategory)
            .Include(a => a.Pipeline)
            .Include(a => a.Discipline)
            .Include(a => a.Unit).ThenInclude(u => u.Plant)
            .Include(a => a.GateDocuments)
            .Include(a => a.GroupMemberships).ThenInclude(m => m.Group).ThenInclude(g => g.GroupType)
            .AsSplitQuery();
        var asset = await (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(a => a.Id == id, ct)
                    ?? throw ApiProblem.NotFound("The asset");
        if (tracking && asset.Preservation is null)
        {
            asset.Preservation = new AssetPreservation { AssetId = asset.Id };
            db.Preservations.Add(asset.Preservation);
        }
        if (tracking && asset.Type == AssetType.Pipeline && asset.Pipeline is null)
        {
            asset.Pipeline = new PipelineDetail { AssetId = asset.Id };
            db.PipelineDetails.Add(asset.Pipeline);
        }
        return asset;
    }

    internal static AssetProfile ToProfile(Asset a)
    {
        var p = a.Preservation ?? new AssetPreservation();
        var values = PreservationFields.All.Where(f => f.OnlyFor is null || f.OnlyFor == a.Type).ToDictionary(f => f.Key, f => FieldValues.Read(f, a));
        var updated = p.UpdatedAtUtc > a.UpdatedAtUtc ? (p.UpdatedBy, p.UpdatedAtUtc) : (a.UpdatedBy, a.UpdatedAtUtc);
        return new AssetProfile(
            a.Id, a.Code, a.Type.ToString(), new NamedRef(a.Discipline.Id, a.Discipline.Name),
            new UnitRef(a.Unit.Id, a.Unit.Name, a.Unit.Number, a.Unit.Plant?.Name ?? ""),
            a.IsActive, a.SimproFlangeId,
            FieldValues.CurrentGateText(p.CurrentGate),
            FieldValues.CurrentGateText(Domain.Rules.PreservationRules.SuggestedCurrentGate(p)),
            values,
            a.GateDocuments.OrderBy(d => d.Gate)
                .Select(d => new GateDocDto(d.Gate, d.Status.ToString(), d.ReceivedOn, d.Reference, d.Note, d.UpdatedBy, d.UpdatedAtUtc)).ToList(),
            a.GroupMemberships.Select(m => new GroupRefDto(m.Group.Id, m.Group.Code, m.Group.Name, m.Group.GroupType.Name)).ToList(),
            p.PreservedAtUtc, p.PreservedBy, updated.Item1, updated.Item2,
            p.DashboardCategory is { } c
                ? new AssetCategoryDto(c.Id, c.Code, c.Label, Reporting.UnitDashboard.SectionKey(c.Section))
                : null);
    }

    public async Task<List<SearchHit>> SearchAsync(string q, int? unitId, AssetType? type, int take, CancellationToken ct)
    {
        var term = q.Trim();
        if (term.Length < 2) return [];
        // LIKE is case-insensitive on both SQL Server (CI collation) and SQLite (tests).
        var pattern = "%" + term.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        var query = db.Assets.AsNoTracking()
            .Where(a => EF.Functions.Like(a.Code, pattern) || EF.Functions.Like(a.Tag!, pattern) || EF.Functions.Like(a.Description!, pattern));
        if (unitId is not null) query = query.Where(a => a.UnitId == unitId);
        if (type is not null) query = query.Where(a => a.Type == type);

        var hits = await query
            .Select(a => new { a.Id, a.Code, a.Tag, a.Description, Unit = a.Unit.Name, a.Type, a.IsActive })
            .Take(200)
            .ToListAsync(ct);

        // Best matches first: tag/ID starting with the term, then active ones.
        return hits
            .OrderByDescending(h => (h.Tag ?? "").StartsWith(term, StringComparison.OrdinalIgnoreCase) || h.Code.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(h => h.IsActive)
            .ThenBy(h => h.Tag ?? h.Code, NaturalComparer.Instance)
            .Take(Math.Clamp(take, 1, 50))
            .Select(h => new SearchHit(h.Id, h.Code, h.Tag, h.Description, h.Unit, h.Type.ToString(), h.IsActive))
            .ToList();
    }

    public async Task<List<HistoryEntry>> HistoryAsync(Guid? assetId, int? unitId, DateTime? before, int take, CancellationToken ct)
    {
        var query = db.ChangeLogs.AsNoTracking().Where(c => c.AssetId != null);
        if (assetId is not null) query = query.Where(c => c.AssetId == assetId);
        if (unitId is not null) query = query.Where(c => db.Assets.Any(a => a.Id == c.AssetId && a.UnitId == unitId));
        if (before is not null) query = query.Where(c => c.ChangedAtUtc < before);

        var rows = await query.OrderByDescending(c => c.ChangedAtUtc).ThenByDescending(c => c.Id)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(c => new
            {
                c.ChangedAtUtc, c.AssetId, c.EntityType, c.EntityKey, c.Field, c.OldValue, c.NewValue, c.ChangedBy, c.Source,
                Asset = db.Assets.Where(a => a.Id == c.AssetId).Select(a => new { a.Code, a.Tag }).FirstOrDefault()
            })
            .ToListAsync(ct);

        var customIds = rows.Where(r => r.Field.StartsWith("cf:", StringComparison.Ordinal))
            .Select(r => int.TryParse(r.Field[3..], out var n) ? n : 0).Distinct().ToList();
        var customLabels = customIds.Count == 0 ? [] : await db.FieldDefinitions.AsNoTracking().Where(f => customIds.Contains(f.Id)).ToDictionaryAsync(f => $"cf:{f.Id}", f => f.Label, ct);

        // Category changes are stored as ids; show "Hold Cat1 – Under inspection" instead.
        const string categoryField = nameof(AssetPreservation.DashboardCategoryId);
        var categoryIds = rows.Where(r => r.Field == categoryField).SelectMany(r => new[] { r.OldValue, r.NewValue })
            .Select(v => int.TryParse(v, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
        var categoryNames = categoryIds.Count == 0 ? [] : (await db.DashboardCategories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToListAsync(ct))
            .ToDictionary(c => c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                c => $"{(c.Section == Domain.Reporting.CategorySection.Hold ? "Hold" : "N/A")} {c.Code} – {c.Label}");
        string? Value(string field, string? v) => v is null ? null
            : field == categoryField ? categoryNames.GetValueOrDefault(v, v)
            : field == nameof(AssetPreservation.CurrentGate) && FieldValues.TryParseCurrentGate(v, out var gate) ? FieldValues.CurrentGateText(gate)
            : v;

        return rows.Select(r => new HistoryEntry(
            r.ChangedAtUtc, r.AssetId, r.Asset?.Code, r.Asset?.Tag, r.Field, customLabels.GetValueOrDefault(r.Field) ?? LabelOf(r.EntityType, r.EntityKey, r.Field),
            Value(r.Field, r.OldValue), Value(r.Field, r.NewValue), r.ChangedBy, r.Source.ToString())).ToList();
    }

    private static string LabelOf(string entityType, string entityKey, string field) => entityType switch
    {
        nameof(GateDocumentation) => $"Gate documentation {field}",
        nameof(AssetFieldValue) => $"Custom field {field}",
        _ => PreservationFields.LabelOf(field)
    };

    public async Task<UnitSummary> SummaryAsync(int unitId, CancellationToken ct)
    {
        var assets = await db.Assets.AsNoTracking().Where(a => a.UnitId == unitId)
            .Select(a => new { a.Type, a.IsActive, Gate = a.Preservation != null ? a.Preservation.CurrentGate : CurrentGate.New })
            .ToListAsync(ct);
        var active = assets.Where(a => a.IsActive).ToList();
        var pending = await db.GateDocuments.CountAsync(d => d.Asset.UnitId == unitId && d.Asset.IsActive && d.Status == DocumentationStatus.Pending, ct);
        return new UnitSummary(
            active.Count(a => a.Type == AssetType.Equipment),
            active.Count(a => a.Type == AssetType.ElectricalInstrument),
            active.Count(a => a.Type == AssetType.Pipeline),
            assets.Count(a => !a.IsActive),
            active.Where(a => a.Type == AssetType.Equipment)
                .GroupBy(a => FieldValues.CurrentGateText(a.Gate))
                .OrderBy(g => g.Key == "New" ? 0 : g.Key == "Removed from Site" ? 99 : g.Key[1] - '0')
                .ToDictionary(g => g.Key, g => g.Count()),
            pending);
    }

    /// <summary>Static → Rotating → Package Mechanical → others → E&amp;I, as in the client Excel.</summary>
    public static int DisciplineRank(string discipline) => discipline.ToLowerInvariant() switch
    {
        "static" => 0,
        "rotating" => 1,
        "package mechanical" => 2,
        "electrical" or "instrumentation" or "e&i" => 200,
        _ => 100
    };
}

/// <summary>Sorts "41-P-2" before "41-P-10".</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.Ordinal);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i; while (i < x.Length && char.IsDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
