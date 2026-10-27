using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Excel;
using PresApp.Api.Auth;
using PresApp.Api.Hosting;
using PresApp.Api.Reporting;
using PresApp.Domain.Catalog;
using PresApp.Domain.Imports;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Endpoints;

public static class ExcelEndpoints
{
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public sealed record ExcelSettingsDto(int MaxAgeHours);

    public static void MapExcelEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------------------------------------------------------------- unit Excel page
        app.MapGet("/api/units/{unitId:int}/excel", async (int unitId, PresAppDbContext db, CancellationToken ct) =>
        {
            var template = await db.ExcelTemplates.AsNoTracking().Where(t => t.UnitId == unitId)
                .Select(t => new { t.FileName, t.UploadedAtUtc }).FirstOrDefaultAsync(ct);
            var sessions = await db.ExcelSessions.AsNoTracking().Where(s => s.UnitId == unitId)
                .OrderByDescending(s => s.ExportedAtUtc).Take(15)
                .Select(s => new ExcelSessionDto(s.Id, s.FileName, s.ExportedAtUtc, s.ExportedBy, s.Status.ToString(), s.RowCount, s.ProcessedAtUtc, s.FormatVersion < WorkbookFormat.FormatVersion))
                .ToListAsync(ct);
            return new UnitExcelOverview(template is not null, template?.FileName, template?.UploadedAtUtc,
                await ExcelImportService.MaxAgeHoursAsync(db, ct), sessions, await ImportQueries.RecentAsync(db, unitId, 25, ct));
        }).RequirePermission(Permissions.ExcelPage);

        app.MapGet("/api/units/{unitId:int}/excel/export", async (int unitId, ClaimsPrincipal user, UnitWorkbookExporter exporter, CancellationToken ct) =>
        {
            var result = await exporter.ExportAsync(unitId, user.Identity?.Name ?? "unknown", ct);
            return Results.File(result.Content, XlsxType, result.FileName);
        }).RequirePermission(Permissions.ExcelExport);

        app.MapPost("/api/units/{unitId:int}/excel/import", async (int unitId, IFormFile file, ClaimsPrincipal user, ExcelImportService service, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await service.PreviewAsync(stream, file.FileName, unitId, user.Identity?.Name ?? "unknown", ct);
        }).RequirePermission(Permissions.ExcelImport).DisableAntiforgery();

        app.MapPost("/api/units/{unitId:int}/excel/onboarding", async (int unitId, IFormFile file, ClaimsPrincipal user, OnboardingService service, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.AssetsManage)) throw ApiProblem.Forbidden("Only an administrator can load a client list (it creates equipment).");
            await using var stream = file.OpenReadStream();
            return await service.PreviewAsync(stream, file.FileName, unitId, user.Identity?.Name ?? "unknown", ct);
        }).RequirePermission(Permissions.ExcelImport).DisableAntiforgery();

        // ---------------------------------------------------------------- import runs
        var imports = app.MapGroup("/api/imports").RequirePermission(Permissions.ExcelImport);

        imports.MapGet("/{id:long}", (long id, PresAppDbContext db, CancellationToken ct) => ImportQueries.DetailAsync(db, id, ct));

        imports.MapPost("/{id:long}/apply", async (long id, HttpRequest http, ClaimsPrincipal user, PresAppDbContext db, ExcelImportService excel, OnboardingService onboarding, CancellationToken ct) =>
        {
            // Optional body: the choices made in the preview for lines that look like existing ones.
            var request = http.ContentLength > 0 ? await http.ReadFromJsonAsync<ApplyImportRequest>(ct) : null;
            var kind = await db.ImportRuns.Where(r => r.Id == id).Select(r => (ImportKind?)r.Kind).FirstOrDefaultAsync(ct)
                       ?? throw ApiProblem.NotFound("Import not found.");
            var by = user.Identity?.Name ?? "unknown";
            if (kind == ImportKind.Onboarding)
            {
                if (!user.HasPermission(Permissions.AssetsManage)) throw ApiProblem.Forbidden("Only an administrator can apply a client list.");
                return await onboarding.ApplyAsync(id, by, ct, request?.Decisions);
            }
            return await excel.ApplyAsync(id, by, ct);
        });

        imports.MapPost("/{id:long}/cancel", (long id, ExcelImportService service, CancellationToken ct) => service.CancelAsync(id, ct));

        imports.MapGet("/{id:long}/file", async (long id, PresAppDbContext db, CancellationToken ct) =>
        {
            var file = await db.ImportFiles.AsNoTracking().Where(f => f.ImportRunId == id)
                .Select(f => new { f.Content, f.ImportRun.FileName }).FirstOrDefaultAsync(ct) ?? throw ApiProblem.NotFound("The file is not available.");
            return Results.File(file.Content, XlsxType, file.FileName);
        });

        // ---------------------------------------------------------------- issues
        var issues = app.MapGroup("/api/import-issues").RequirePermission(Permissions.ExcelImport);

        issues.MapGet("", (bool? resolved, int? unitId, string? q, int? take, PresAppDbContext db, CancellationToken ct) =>
            ImportQueries.IssuesAsync(db, resolved ?? false, unitId, q, Math.Clamp(take ?? 300, 1, 1000), ct));

        issues.MapGet("/count", async (PresAppDbContext db, CancellationToken ct) => new { open = await ImportQueries.OpenIssueCountAsync(db, ct) });

        issues.MapPost("/{id:long}", (long id, ResolveIssueRequest request, ClaimsPrincipal user, PresAppDbContext db, TimeProvider clock, CancellationToken ct) =>
            ImportQueries.ResolveAsync(db, id, request, user.Identity?.Name ?? "unknown", clock.GetUtcNow().UtcDateTime, ct));

        // ---------------------------------------------------------------- templates and settings
        var templates = app.MapGroup("/api/excel-templates").RequirePermission(Permissions.CatalogManage);

        templates.MapGet("", (ExcelTemplateService s, CancellationToken ct) => s.ListAsync(ct));

        templates.MapPut("/{unitId:int}", async (int unitId, IFormFile file, ClaimsPrincipal user, ExcelTemplateService s, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await s.UploadAsync(unitId, stream, file.FileName, user.Identity?.Name ?? "unknown", ct);
        }).DisableAntiforgery();

        templates.MapGet("/{unitId:int}/file", async (int unitId, ExcelTemplateService s, CancellationToken ct) =>
        {
            var (name, content) = await s.DownloadAsync(unitId, ct);
            return Results.File(content, XlsxType, name);
        });

        app.MapGet("/api/settings/excel", async (PresAppDbContext db, CancellationToken ct) =>
            new ExcelSettingsDto(await ExcelImportService.MaxAgeHoursAsync(db, ct))).RequirePermission(Permissions.ExcelExport);

        app.MapPut("/api/settings/excel", async (ExcelSettingsDto request, PresAppDbContext db, CancellationToken ct) =>
        {
            if (request.MaxAgeHours is < 1 or > 720) throw ApiProblem.Invalid("The age limit must be between 1 and 720 hours.");
            var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == AppSettingKeys.ExcelMaxAgeHours, ct);
            if (setting is null) db.Settings.Add(setting = new AppSetting { Key = AppSettingKeys.ExcelMaxAgeHours });
            setting.Value = request.MaxAgeHours.ToString(CultureInfo.InvariantCulture);
            await db.SaveChangesAsync(ct);
            return new ExcelSettingsDto(request.MaxAgeHours);
        }).RequirePermission(Permissions.CatalogManage);

        // ---------------------------------------------------------------- dashboard and categories
        app.MapGet("/api/units/{unitId:int}/dashboard", (int unitId, UnitDashboard d, CancellationToken ct) => d.GetAsync(unitId, ct))
            .RequirePermission(Permissions.AssetsView);

        app.MapGet("/api/units/{unitId:int}/categories", (int unitId, UnitDashboard d, CancellationToken ct) => d.CategoriesAsync(unitId, ct))
            .RequirePermission(Permissions.AssetsView);

        app.MapPut("/api/units/{unitId:int}/categories", (int unitId, List<SaveCategory> request, UnitDashboard d, CancellationToken ct) =>
            d.SaveCategoriesAsync(unitId, request, ct)).RequirePermission(Permissions.CategoriesManage);
    }
}

/// <summary>Choices made in the preview for lines that look like existing ones (row number → "update", "rename" or "create").</summary>
public sealed record ApplyImportRequest(Dictionary<int, string>? Decisions);
