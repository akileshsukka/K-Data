using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Domain.Common;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Reporting;

/// <summary>
/// The walkdown and gate statuses of the assets of one unit as they were at an earlier moment, rebuilt from the change
/// log: the value at a moment is the old value of the first change after it, or the current value when nothing changed
/// since. Loads the changes after <c>fromUtc</c> once, so any moment from then on can be asked.
/// <list type="bullet">
/// <item>Changes written by the SharePoint migration or a history import are not work done on site: they count as if
/// they had always been there.</item>
/// <item>An asset created after <c>fromUtc</c> keeps the statuses it was created with from <c>fromUtc</c> on, so loading a
/// list of new equipment is never counted as work completed that day.</item>
/// </list>
/// </summary>
public sealed class StatusHistory
{
    /// <summary>Status fields of <see cref="AssetPreservation"/> read by the dashboards: walkdown and G1–G8 (E1–E8 for E&amp;I).</summary>
    public static readonly string[] Fields =
    [
        nameof(AssetPreservation.EmWalkdownStatus),
        nameof(AssetPreservation.G1Status), nameof(AssetPreservation.G2Status), nameof(AssetPreservation.G3Status), nameof(AssetPreservation.G4Status),
        nameof(AssetPreservation.G5Status), nameof(AssetPreservation.G6Status), nameof(AssetPreservation.G7Status), nameof(AssetPreservation.G8Status),
    ];

    private static readonly ChangeSource[] NotWork = [ChangeSource.Migration, ChangeSource.HistoryImport];

    private readonly Dictionary<(Guid Asset, string Field), List<Change>> changes;
    private readonly Dictionary<Guid, DateTime> createdAt;
    private readonly DateTime fromUtc;

    private sealed record Change(DateTime AtUtc, string? Old, string? New, bool Counts);

    private StatusHistory(DateTime fromUtc, Dictionary<(Guid, string), List<Change>> changes, Dictionary<Guid, DateTime> createdAt)
    {
        this.fromUtc = fromUtc;
        this.changes = changes;
        this.createdAt = createdAt;
    }

    public static async Task<StatusHistory> LoadAsync(PresAppDbContext db, int unitId, DateTime fromUtc, CancellationToken ct)
    {
        var unitAssets = db.Assets.Where(a => a.UnitId == unitId).Select(a => (Guid?)a.Id);
        var preservation = nameof(AssetPreservation);
        var rows = await db.ChangeLogs.AsNoTracking()
            .Where(c => c.ChangedAtUtc > fromUtc && c.AssetId != null && unitAssets.Contains(c.AssetId)
                        && ((c.EntityType == preservation && Fields.Contains(c.Field)) || c.Field == "(created)"))
            .Select(c => new { AssetId = c.AssetId!.Value, c.Field, c.OldValue, c.NewValue, c.ChangedAtUtc, c.Source, c.Id })
            .ToListAsync(ct);
        var created = rows.Where(r => r.Field == "(created)").GroupBy(r => r.AssetId).ToDictionary(g => g.Key, g => g.Min(r => r.ChangedAtUtc));
        var byField = rows.Where(r => r.Field != "(created)")
            .GroupBy(r => (r.AssetId, r.Field))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.ChangedAtUtc).ThenBy(r => r.Id)
                .Select(r => new Change(r.ChangedAtUtc, r.OldValue, r.NewValue, !NotWork.Contains(r.Source))).ToList());
        return new StatusHistory(fromUtc, byField, created);
    }

    /// <summary>Value of one status field of one asset at <paramref name="atUtc"/> (not before the moment the history was loaded from).</summary>
    public string? ValueAt(Guid assetId, string field, string? current, DateTime atUtc)
    {
        if (atUtc < fromUtc) throw new ArgumentOutOfRangeException(nameof(atUtc), "The history was loaded from a later moment.");
        // A new asset: its statuses at creation are its starting point, not work done.
        if (createdAt.TryGetValue(assetId, out var created) && atUtc < created) atUtc = created;
        if (!changes.TryGetValue((assetId, field), out var list)) return current;
        string? carried = null;
        var hasCarried = false;
        foreach (var c in list)
        {
            if (c.AtUtc <= atUtc) continue;
            if (!c.Counts) { carried = c.New; hasCarried = true; continue; }
            return hasCarried ? carried : c.Old;
        }
        return current;
    }
}
