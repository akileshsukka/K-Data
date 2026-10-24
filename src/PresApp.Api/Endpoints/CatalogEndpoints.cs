using Microsoft.EntityFrameworkCore;
using PresApp.Api.Auth;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Endpoints;

/// <summary>Read-only catalogs the front end loads once at start (plants, units, lists, disciplines, measurements).</summary>
public static class CatalogEndpoints
{
    public sealed record UnitDto(int Id, string Name, int Number);
    public sealed record PlantDto(int Id, string Name, IReadOnlyList<UnitDto> Units);
    public sealed record OptionSetDto(string Key, string Label, IReadOnlyList<string> Values);
    public sealed record DisciplineDto(int Id, string Name, string AssetType, string? Prefix);
    public sealed record MeasurementDto(int Id, string Key, string Label, string? UnitOfMeasure, int? Gate, int? EvidenceForGate,
        decimal? MinValue, decimal? MaxValue);

    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog").RequirePermission(Permissions.AssetsView);

        group.MapGet("/plants", async (PresAppDbContext db, CancellationToken ct) =>
            await db.Plants.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name)
                .Select(p => new PlantDto(p.Id, p.Name,
                    p.Units.Where(u => u.IsActive).OrderBy(u => u.Number).Select(u => new UnitDto(u.Id, u.Name, u.Number)).ToList()))
                .ToListAsync(ct));

        group.MapGet("/options", async (PresAppDbContext db, CancellationToken ct) =>
            await db.OptionSets.AsNoTracking().OrderBy(s => s.Key)
                .Select(s => new OptionSetDto(s.Key, s.Label,
                    s.Values.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => v.Value).ToList()))
                .ToListAsync(ct));

        group.MapGet("/disciplines", async (PresAppDbContext db, CancellationToken ct) =>
            (await db.Disciplines.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.SortOrder).ToListAsync(ct))
            .Select(d => new DisciplineDto(d.Id, d.Name, d.AssetType.ToString(), d.Prefix)));

        group.MapGet("/measurements", async (PresAppDbContext db, CancellationToken ct) =>
            await db.MeasurementTypes.AsNoTracking().Where(m => m.IsActive).OrderBy(m => m.SortOrder)
                .Select(m => new MeasurementDto(m.Id, m.Key, m.Label, m.UnitOfMeasure, m.Gate, m.EvidenceForGate, m.MinValue, m.MaxValue))
                .ToListAsync(ct));
    }
}
