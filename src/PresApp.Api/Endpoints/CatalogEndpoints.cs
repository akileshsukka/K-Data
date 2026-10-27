using Microsoft.EntityFrameworkCore;
using PresApp.Api.Auth;
using PresApp.Api.Hosting;
using PresApp.Api.Piping;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
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
    public sealed record FieldDto(string Key, string Label, string Section, string Kind, string? OptionSet, int MaxLength,
        string Permission, int? Gate, bool BulkEditable, string? OnlyFor);
    public sealed record LineCodeDto(int Id, string Kind, string Code, string Description, bool IsActive);
    public sealed record SaveLineCodeRequest(string Kind, string Code, string Description, bool IsActive = true);
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

        group.MapGet("/fields", () => PreservationFields.All.Select(f =>
            new FieldDto(f.Key, f.Label, f.Section, f.Kind.ToString(), f.OptionSet, f.MaxLength, f.Permission, f.Gate, f.BulkEditable && f.IsEditable, f.OnlyFor?.ToString())));

        group.MapGet("/line-codes", async (PresAppDbContext db, CancellationToken ct) =>
            (await db.LineCodes.AsNoTracking().OrderBy(c => c.Kind).ThenBy(c => c.Code).ToListAsync(ct))
            .Select(c => new LineCodeDto(c.Id, c.Kind.ToString(), c.Code, c.Description, c.IsActive)));

        // Administrators keep the meanings of the line number codes (new codes, wording, deactivate).
        app.MapPost("/api/line-codes", async (SaveLineCodeRequest r, PresAppDbContext db, CancellationToken ct) =>
        {
            var c = new LineCode();
            Fill(c, r);
            if (await db.LineCodes.AnyAsync(x => x.Kind == c.Kind && x.Code == c.Code, ct)) throw ApiProblem.Conflict($"{c.Code} is already in the list.");
            db.LineCodes.Add(c);
            await db.SaveChangesAsync(ct);
            return new LineCodeDto(c.Id, c.Kind.ToString(), c.Code, c.Description, c.IsActive);
        }).RequirePermission(Permissions.CatalogManage);

        app.MapPut("/api/line-codes/{id:int}", async (int id, SaveLineCodeRequest r, PresAppDbContext db, CancellationToken ct) =>
        {
            var c = await db.LineCodes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiProblem.NotFound("Code not found.");
            Fill(c, r);
            if (await db.LineCodes.AnyAsync(x => x.Id != id && x.Kind == c.Kind && x.Code == c.Code, ct)) throw ApiProblem.Conflict($"{c.Code} is already in the list.");
            await db.SaveChangesAsync(ct);
            return new LineCodeDto(c.Id, c.Kind.ToString(), c.Code, c.Description, c.IsActive);
        }).RequirePermission(Permissions.CatalogManage);

        // Pipe specs: class and flange type of the lines come from here.
        group.MapGet("/pipe-specs", (PipeSpecService s, CancellationToken ct) => s.ListAsync(ct));
        app.MapPost("/api/pipe-specs", (SavePipeSpecRequest r, PipeSpecService s, CancellationToken ct) => s.SaveAsync(null, r, ct))
            .RequirePermission(Permissions.CatalogManage);
        app.MapPut("/api/pipe-specs/{id:int}", (int id, SavePipeSpecRequest r, PipeSpecService s, CancellationToken ct) => s.SaveAsync(id, r, ct))
            .RequirePermission(Permissions.CatalogManage);
        app.MapPost("/api/pipe-specs/import", async (bool? apply, IFormFile file, PipeSpecService s, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await s.ImportAsync(stream, apply == true, ct);
        }).RequirePermission(Permissions.CatalogManage).DisableAntiforgery();

        group.MapGet("/measurements", async (PresAppDbContext db, CancellationToken ct) =>
            await db.MeasurementTypes.AsNoTracking().Where(m => m.IsActive).OrderBy(m => m.SortOrder)
                .Select(m => new MeasurementDto(m.Id, m.Key, m.Label, m.UnitOfMeasure, m.Gate, m.EvidenceForGate, m.MinValue, m.MaxValue))
                .ToListAsync(ct));
    }

    private static void Fill(LineCode c, SaveLineCodeRequest r)
    {
        if (!Enum.TryParse<LineCodeKind>(r.Kind, out var kind)) throw ApiProblem.Invalid("Unknown kind of code.");
        var code = (r.Code ?? "").Trim().ToUpperInvariant();
        var description = (r.Description ?? "").Trim();
        if (code.Length == 0 || code.Length > LineCode.CodeLength || !code.All(char.IsLetterOrDigit)) throw ApiProblem.Invalid("The code is letters and digits, at most 10.");
        if (description.Length == 0 || description.Length > LineCode.DescriptionLength) throw ApiProblem.Invalid("Enter what the code means (at most 120 characters).");
        c.Kind = kind;
        c.Code = code;
        c.Description = description;
        c.IsActive = r.IsActive;
    }
}
