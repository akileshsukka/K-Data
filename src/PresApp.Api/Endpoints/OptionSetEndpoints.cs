using Microsoft.EntityFrameworkCore;
using PresApp.Api.Auth;
using PresApp.Api.Hosting;
using PresApp.Domain.Catalog;
using PresApp.Domain.Rules;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Endpoints;

/// <summary>
/// The dropdown lists (Current Activity, Action By, Priority…). Values are added, reordered and deactivated; never renamed
/// or deleted, because assets keep the text they had. The gate status list is fixed: the gate rules depend on it.
/// Lists of custom fields are edited with their field.
/// </summary>
public static class OptionSetEndpoints
{
    public sealed record OptionValueDto(string Value, bool IsActive);
    public sealed record OptionSetAdminDto(string Key, string Label, bool Locked, IReadOnlyList<OptionValueDto> Values);
    public sealed record SaveOptionValue(string Value, bool IsActive);
    public sealed record SaveOptionSetRequest(IReadOnlyList<SaveOptionValue> Values);

    private static readonly HashSet<string> Locked = [OptionSetKeys.GateStatus];

    public static void MapOptionSetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/option-sets").RequirePermission(Permissions.CatalogManage);

        g.MapGet("", async (PresAppDbContext db, CancellationToken ct) =>
        {
            var sets = await db.OptionSets.AsNoTracking().Include(s => s.Values).Where(s => !s.Key.StartsWith("CF.")).OrderBy(s => s.Label).ToListAsync(ct);
            return sets.Select(s => new OptionSetAdminDto(s.Key, s.Label, Locked.Contains(s.Key),
                s.Values.OrderBy(v => v.SortOrder).Select(v => new OptionValueDto(v.Value, v.IsActive)).ToList())).ToList();
        });

        g.MapPut("/{key}", async (string key, SaveOptionSetRequest r, PresAppDbContext db, CancellationToken ct) =>
        {
            if (Locked.Contains(key)) throw ApiProblem.Invalid("The gate status list is fixed: the gate rules depend on it.");
            var set = await db.OptionSets.Include(s => s.Values).FirstOrDefaultAsync(s => s.Key == key && !s.Key.StartsWith("CF."), ct)
                      ?? throw ApiProblem.NotFound("List");
            var incoming = (r.Values ?? []).Select(v => (Value: ValueNormalizer.CleanText(v.Value), v.IsActive)).Where(v => v.Value is not null).ToList();
            if (incoming.Select(v => ValueNormalizer.ListKey(v.Value!)).Distinct().Count() != incoming.Count) throw ApiProblem.Invalid("A value appears twice.");
            if (incoming.Any(v => v.Value!.Length > 200)) throw ApiProblem.Invalid("Each value allows at most 200 characters.");
            if (!incoming.Any(v => v.IsActive)) throw ApiProblem.Invalid("Keep at least one active value.");
            var missing = set.Values.Where(v => !incoming.Any(i => ValueNormalizer.ListKey(i.Value!) == ValueNormalizer.ListKey(v.Value))).Select(v => v.Value).ToList();
            if (missing.Count > 0) throw ApiProblem.Invalid($"Values are deactivated, never removed: {string.Join(", ", missing)}.");

            for (var i = 0; i < incoming.Count; i++)
            {
                var (value, active) = incoming[i];
                var existing = set.Values.FirstOrDefault(v => ValueNormalizer.ListKey(v.Value) == ValueNormalizer.ListKey(value!));
                if (existing is null) set.Values.Add(new OptionValue { Value = value!, SortOrder = i, IsActive = active });
                else { existing.SortOrder = i; existing.IsActive = active; }
            }
            await db.SaveChangesAsync(ct);
            return new OptionSetAdminDto(set.Key, set.Label, false, set.Values.OrderBy(v => v.SortOrder).Select(v => new OptionValueDto(v.Value, v.IsActive)).ToList());
        });
    }
}
