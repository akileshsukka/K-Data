using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Catalog;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Piping;

public sealed record PipingViewDto(bool Grouped);

/// <summary>
/// How each unit shows its lines by default: individual or grouped by size (<see cref="Domain.Assets.LineNumber.GroupKey"/>).
/// Kept as a setting (<see cref="AppSettingKeys.PipingGrouped"/>); the lines themselves never change.
/// </summary>
public sealed class PipingViewSettings(PresAppDbContext db)
{
    public async Task<bool> GroupedAsync(int unitId, CancellationToken ct) =>
        await db.Settings.AsNoTracking().Where(s => s.Key == AppSettingKeys.PipingGrouped(unitId)).Select(s => s.Value).FirstOrDefaultAsync(ct) == "Yes";

    /// <summary>The value asked for, or the unit's default when none was given.</summary>
    public async Task<bool> ResolveAsync(int unitId, bool? asked, CancellationToken ct) => asked ?? await GroupedAsync(unitId, ct);

    public async Task<PipingViewDto> SetAsync(int unitId, bool grouped, CancellationToken ct)
    {
        if (!await db.Units.AnyAsync(u => u.Id == unitId, ct)) throw ApiProblem.NotFound("Unit not found.");
        var key = AppSettingKeys.PipingGrouped(unitId);
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) db.Settings.Add(setting = new AppSetting { Key = key, Description = "Lines of the unit shown grouped by size by default." });
        setting.Value = grouped ? "Yes" : "No";
        await db.SaveChangesAsync(ct);
        return new PipingViewDto(grouped);
    }
}
