using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Common;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Assets;

public sealed record DeleteAssetsRequest(IReadOnlyList<Guid>? AssetIds, string? Confirmation);

/// <summary>One selected asset; <see cref="Blocked"/> says why it cannot be deleted (null: it can).</summary>
public sealed record DeletionItem(Guid Id, string Code, string? Tag, string? Description, string Unit, string? Blocked);

public sealed record DeletionPreview(List<DeletionItem> Items, int Deletable, string ConfirmationWord);

public sealed record DeletionResult(int Deleted, List<DeletionItem> NotDeleted);

/// <summary>
/// Permanent removal of assets created by mistake (a client list loaded into the wrong unit, a test item).
/// Everything else is deactivated, never deleted. An asset is only deleted when nothing real depends on it:
/// not in Simpro, no monitoring readings, no signed documents, no E7 test values. Its satellites (preservation,
/// gate documentation, custom fields, group memberships, pipeline detail) go with it. One "(deleted)" row per asset
/// stays in the change log. The Equipment ID counter goes back when the deleted IDs were the last ones given.
/// </summary>
public sealed partial class AssetDeletion(PresAppDbContext db, AuditContext audit, TimeProvider clock)
{
    /// <summary>The word typed to confirm, as on GitHub. Checked by the API, not only by the page.</summary>
    public const string ConfirmationWord = "DELETE";
    public const int MaxAssets = 1000;

    public async Task<DeletionPreview> PreviewAsync(IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        var items = await CheckAsync(Validate(ids), ct);
        return new DeletionPreview(items, items.Count(i => i.Blocked is null), ConfirmationWord);
    }

    public async Task<DeletionResult> DeleteAsync(DeleteAssetsRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.Confirmation?.Trim(), ConfirmationWord, StringComparison.Ordinal))
            throw ApiProblem.Invalid($"Type {ConfirmationWord} to confirm the deletion.");
        var ids = Validate(request.AssetIds);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var items = await CheckAsync(ids, ct);
        var deletable = items.Where(i => i.Blocked is null).Select(i => i.Id).ToList();
        if (deletable.Count == 0) return new DeletionResult(0, items);

        var assets = await db.Assets
            .Include(a => a.Unit).Include(a => a.Preservation).Include(a => a.Pipeline).Include(a => a.ElectricalTest)
            .Include(a => a.GateDocuments).Include(a => a.FieldValues).Include(a => a.GroupMemberships)
            .Where(a => deletable.Contains(a.Id)).ToListAsync(ct);

        // Links removed earlier still point at the asset; the forced-sync mark too.
        db.DocumentLinks.RemoveRange(await db.DocumentLinks.Where(l => deletable.Contains(l.AssetId)).ToListAsync(ct));
        db.SimproForcedAssets.RemoveRange(await db.SimproForcedAssets.Where(f => deletable.Contains(f.AssetId)).ToListAsync(ct));

        var batch = Guid.NewGuid();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var a in assets)
        {
            db.ChangeLogs.Add(new ChangeLog
            {
                EntityType = nameof(Asset), EntityKey = a.Id.ToString(), AssetId = a.Id, Field = "(deleted)",
                OldValue = string.Join(" · ", new[] { a.Code, a.Tag, a.Description, a.Unit.Name }.Where(s => !string.IsNullOrWhiteSpace(s))),
                ChangedAtUtc = now, ChangedBy = audit.UserName, Source = ChangeSource.App, BatchId = batch
            });
        }
        var counters = assets.Select(a => (Code: CodeParts(a.Code), a.Unit.Number)).Where(x => x.Code is not null)
            .Select(x => (x.Code!.Value.Prefix, x.Number)).Distinct().ToList();
        db.Assets.RemoveRange(assets);
        await db.SaveChangesAsync(ct);

        await RollBackCountersAsync(counters, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new DeletionResult(assets.Count, items.Where(i => i.Blocked is not null).ToList());
    }

    private static List<Guid> Validate(IReadOnlyList<Guid>? ids)
    {
        var list = (ids ?? []).Distinct().ToList();
        if (list.Count == 0) throw ApiProblem.Invalid("Select at least one asset.");
        if (list.Count > MaxAssets) throw ApiProblem.Invalid($"At most {MaxAssets} assets can be deleted at once.");
        return list;
    }

    private async Task<List<DeletionItem>> CheckAsync(List<Guid> ids, CancellationToken ct)
    {
        var assets = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Code, a.Tag, a.Description, Unit = a.Unit.Name, a.SimproFlangeId, a.ElectricalTest })
            .ToListAsync(ct);
        if (assets.Count != ids.Count) throw ApiProblem.NotFound("Some of the selected assets no longer exist. Reload the page.");
        var readings = (await db.Readings.Where(r => ids.Contains(r.AssetId)).Select(r => r.AssetId).Distinct().ToListAsync(ct)).ToHashSet();
        var documents = (await db.DocumentLinks.Where(l => ids.Contains(l.AssetId) && l.RemovedAtUtc == null).Select(l => l.AssetId).Distinct().ToListAsync(ct)).ToHashSet();

        return assets.OrderBy(a => a.Code, StringComparer.OrdinalIgnoreCase).Select(a => new DeletionItem(a.Id, a.Code, a.Tag, a.Description, a.Unit,
            a.SimproFlangeId is not null ? "It is in Simpro: deleting it here would leave it there. Deactivate it instead."
            : readings.Contains(a.Id) ? "It has monitoring readings. Deactivate it instead."
            : documents.Contains(a.Id) ? "It has signed documents linked. Unlink them first, or deactivate it."
            : HasTestValues(a.ElectricalTest) ? "It has E7 electrical test values. Deactivate it instead."
            : null)).ToList();
    }

    private static bool HasTestValues(ElectricalTest? t) =>
        t is not null && (t.TestedOn is not null || ElectricalTestSpec.Measures.Any(m => ElectricalTestSpec.Get(t, m.Key) is not null));

    /// <summary>
    /// The counter of each (prefix, unit) goes back to the highest Equipment ID still in use, so the next asset does not
    /// skip the numbers of the deleted ones. It never goes below a number in use (the next ID also skips taken ones).
    /// </summary>
    private async Task RollBackCountersAsync(List<(string Prefix, int Unit)> counters, CancellationToken ct)
    {
        foreach (var (prefix, unit) in counters)
        {
            var seq = await db.EquipmentIdSequences.FirstOrDefaultAsync(s => s.Prefix == prefix && s.UnitNumber == unit, ct);
            if (seq is null) continue;
            var start = $"{prefix}-{EquipmentIdFormat.Middle}-{unit:000}-";
            var highest = (await db.Assets.Where(a => a.Code.StartsWith(start)).Select(a => a.Code).ToListAsync(ct))
                .Select(CodeParts).Where(p => p is not null && p.Value.Prefix == prefix).Select(p => p!.Value.Number).DefaultIfEmpty(0).Max();
            if (highest < seq.LastNumber) seq.LastNumber = highest;
        }
    }

    private static (string Prefix, int Number)? CodeParts(string code)
    {
        var m = CodeRegex().Match(code.Trim());
        return m.Success ? (m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)) : null;
    }

    [GeneratedRegex(@"^([A-Za-z]+)-EMP-\d{3}-(\d+)$")]
    private static partial Regex CodeRegex();
}
