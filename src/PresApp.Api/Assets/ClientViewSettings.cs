using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Assets;

/// <summary>
/// <paramref name="Grouped"/>: the unit's default view is "As the client". <paramref name="SplitItems"/>: active split items
/// of the unit (client tags with two or more active parts); with none, both views are the same and the switch is not shown.
/// </summary>
public sealed record ClientViewDto(bool Grouped, int SplitItems = 0);

/// <summary>
/// How each unit shows the parts of a split item (P-101A, P-101B of the client's P-101A/B) by default: "By equipment"
/// (each part on its own) or "As the client" (one row, counted once, like the client Excel; the default). Kept as a setting
/// (<see cref="AppSettingKeys.ClientTagsGrouped"/>); the assets never change. The client Excel is always grouped.
/// </summary>
public sealed class ClientViewSettings(PresAppDbContext db)
{
    public async Task<ClientViewDto> GetAsync(int unitId, CancellationToken ct)
    {
        var tags = await db.Assets.AsNoTracking().Where(a => a.UnitId == unitId && a.IsActive && a.ClientTag != null)
            .Select(a => new { a.ClientTag, a.Type }).ToListAsync(ct);
        var split = tags.GroupBy(t => (t.Type, ValueNormalizer.ListKey(t.ClientTag!))).Count(g => g.Count() > 1);
        return new ClientViewDto(await GroupedAsync(unitId, ct), split);
    }

    /// <summary>"As the client" unless the unit was set to "By equipment" (23-nov: as the client is the default).</summary>
    public async Task<bool> GroupedAsync(int unitId, CancellationToken ct) =>
        await db.Settings.AsNoTracking().Where(s => s.Key == AppSettingKeys.ClientTagsGrouped(unitId)).Select(s => s.Value).FirstOrDefaultAsync(ct) != "No";

    /// <summary>The value asked for, or the unit's default when none was given.</summary>
    public async Task<bool> ResolveAsync(int unitId, bool? asked, CancellationToken ct) => asked ?? await GroupedAsync(unitId, ct);

    public async Task<ClientViewDto> SetAsync(int unitId, bool grouped, CancellationToken ct)
    {
        if (!await db.Units.AnyAsync(u => u.Id == unitId, ct)) throw ApiProblem.NotFound("Unit not found.");
        var key = AppSettingKeys.ClientTagsGrouped(unitId);
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) db.Settings.Add(setting = new AppSetting { Key = key, Description = "Parts of a split item shown as the client lists them (one row) by default." });
        setting.Value = grouped ? "Yes" : "No";
        await db.SaveChangesAsync(ct);
        return await GetAsync(unitId, ct);
    }
}
