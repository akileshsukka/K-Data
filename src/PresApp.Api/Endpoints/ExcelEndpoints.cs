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
    public sealed record ElectricalReportRequest(IReadOnlyList<Guid>? AssetIds);

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

        // Piping line list (Line List + Summary), imported back through the same import as the unit file.
        // grouped=true: one row per size group (same line number except the size); not given: the unit's default view.
        app.MapGet("/api/units/{unitId:int}/excel/piping/export", async (int unitId, bool? grouped, ClaimsPrincipal user, PipingWorkbookExporter exporter,
            Piping.PipingViewSettings view, CancellationToken ct) =>
        {
            var result = await exporter.ExportAsync(unitId, user.Identity?.Name ?? "unknown", ct, await view.ResolveAsync(unitId, grouped, ct));
            return Results.File(result.Content, XlsxType, result.FileName);
        }).RequirePermission(Permissions.ExcelExport);

        // Figures of the piping Summary for the app (Unit overview, Pipeline Register, PDF): grouped or not as asked, else the unit's default.
        app.MapGet("/api/units/{unitId:int}/piping-summary", async (int unitId, bool? grouped, Piping.PipingSummaryService s, Piping.PipingViewSettings view, CancellationToken ct) =>
                await s.GetAsync(unitId, ct, await view.ResolveAsync(unitId, grouped, ct)))
            .RequirePermission(Permissions.AssetsView);

        // How the unit shows its lines by default (individual or grouped by size).
        app.MapGet("/api/units/{unitId:int}/piping-view", async (int unitId, Piping.PipingViewSettings view, CancellationToken ct) =>
            new Piping.PipingViewDto(await view.GroupedAsync(unitId, ct))).RequirePermission(Permissions.AssetsView);
        app.MapPut("/api/units/{unitId:int}/piping-view", (int unitId, Piping.PipingViewDto request, Piping.PipingViewSettings view, CancellationToken ct) =>
            view.SetAsync(unitId, request.Grouped, ct)).RequirePermission(Permissions.AssetsManage);

        // Notes under the Summary of the piping line list.
        app.MapGet("/api/units/{unitId:int}/piping-notes", (int unitId, Piping.PipingNoteService s, CancellationToken ct) => s.ListAsync(unitId, ct))
            .RequirePermission(Permissions.AssetsView);
        app.MapPut("/api/units/{unitId:int}/piping-notes", (int unitId, Piping.SavePipingNoteRequest r, Piping.PipingNoteService s, CancellationToken ct) =>
            s.SaveAsync(unitId, r, ct)).RequirePermission(Permissions.AssetsEdit);

        app.MapPost("/api/units/{unitId:int}/excel/import", async (int unitId, IFormFile file, ClaimsPrincipal user, ExcelImportService service, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await service.PreviewAsync(stream, file.FileName, unitId, user.Identity?.Name ?? "unknown", ct);
        }).RequirePermission(Permissions.ExcelImport).DisableAntiforgery();

        // E7 electrical test sheet: report of the unit and upload of the filled sheet.
        app.MapGet("/api/units/{unitId:int}/electrical-tests/export", async (int unitId, ElectricalTestWorkbook workbook, CancellationToken ct) =>
        {
            var result = await workbook.ExportAsync(unitId, ct);
            return Results.File(result.Content, XlsxType, result.FileName);
        }).RequirePermission(Permissions.ExcelExport);

        // The same report for the equipment chosen in the register.
        app.MapPost("/api/units/{unitId:int}/electrical-tests/export", async (int unitId, ElectricalReportRequest request, ElectricalTestWorkbook workbook, CancellationToken ct) =>
        {
            var result = await workbook.ExportAsync(unitId, ct, request.AssetIds ?? []);
            return Results.File(result.Content, XlsxType, result.FileName);
        }).RequirePermission(Permissions.ExcelExport);

        app.MapPost("/api/units/{unitId:int}/electrical-tests/import", async (int unitId, IFormFile file, ClaimsPrincipal user, ElectricalTestWorkbook workbook, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.GatesEdit)) throw ApiProblem.Forbidden("Your role cannot load electrical tests.");
            await using var stream = file.OpenReadStream();
            return await workbook.PreviewAsync(stream, file.FileName, unitId, user.Identity?.Name ?? "unknown", ct);
        }).RequirePermission(Permissions.ExcelImport).DisableAntiforgery();

        app.MapPost("/api/units/{unitId:int}/excel/onboarding", async (int unitId, bool? checkLineFormat, IFormFile file, ClaimsPrincipal user, OnboardingService service, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.AssetsManage)) throw ApiProblem.Forbidden("Only an administrator can load a client list (it creates equipment).");
            if (checkLineFormat == false && !user.HasPermission(Permissions.LinesNonStandard)) throw ApiProblem.Forbidden("Your role cannot turn off the line number format check.");
            await using var stream = file.OpenReadStream();
            return await service.PreviewAsync(stream, file.FileName, unitId, user.Identity?.Name ?? "unknown", ct, checkLineFormat != false);
        }).RequirePermission(Permissions.ExcelImport).DisableAntiforgery();

        // ---------------------------------------------------------------- import runs
        var imports = app.MapGroup("/api/imports").RequirePermission(Permissions.ExcelImport);

        imports.MapGet("/{id:long}", (long id, PresAppDbContext db, CancellationToken ct) => ImportQueries.DetailAsync(db, id, ct));

        imports.MapPost("/{id:long}/apply", async (long id, HttpRequest http, ClaimsPrincipal user, PresAppDbContext db, ExcelImportService excel, OnboardingService onboarding,
            ElectricalTestWorkbook electrical, ReadingsWorkbook readings, CancellationToken ct) =>
        {
            // Optional body: the choices made in the preview for lines that look like existing ones.
            var request = http.ContentLength > 0 ? await http.ReadFromJsonAsync<ApplyImportRequest>(ct) : null;
            var kind = await db.ImportRuns.Where(r => r.Id == id).Select(r => (ImportKind?)r.Kind).FirstOrDefaultAsync(ct)
                       ?? throw ApiProblem.NotFound("Import not found.");
            var by = user.Identity?.Name ?? "unknown";
            if (kind == ImportKind.Onboarding)
            {
                if (!user.HasPermission(Permissions.AssetsManage)) throw ApiProblem.Forbidden("Only an administrator can apply a client list.");
                if (await db.ImportRuns.AnyAsync(r => r.Id == id && r.LineFormatCheckOff, ct) && !user.HasPermission(Permissions.LinesNonStandard))
                    throw ApiProblem.Forbidden("This line list was checked with the line number format check off; your role cannot apply it.");
                return await onboarding.ApplyAsync(id, by, ct, request?.Decisions);
            }
            if (kind == ImportKind.ElectricalTest)
            {
                if (!user.HasPermission(Permissions.GatesEdit)) throw ApiProblem.Forbidden("Your role cannot load electrical tests.");
                return await electrical.ApplyAsync(id, by, ct, request?.Decisions);
            }
            if (kind == ImportKind.Readings)
            {
                if (!user.HasPermission(Permissions.ReadingsWrite)) throw ApiProblem.Forbidden("Your role cannot record monitoring readings.");
                return await readings.ApplyAsync(id, user, ct);
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

        // Piping line list template: one for the whole project.
        templates.MapGet("/piping", (ExcelTemplateService s, CancellationToken ct) => s.PipingAsync(ct));
        templates.MapPut("/piping", async (IFormFile file, ClaimsPrincipal user, ExcelTemplateService s, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await s.UploadPipingAsync(stream, file.FileName, user.Identity?.Name ?? "unknown", ct);
        }).DisableAntiforgery();
        templates.MapGet("/piping/file", async (ExcelTemplateService s, CancellationToken ct) =>
        {
            var (name, content) = await s.DownloadPipingAsync(ct);
            return Results.File(content, XlsxType, name);
        });
        templates.MapDelete("/piping", (ExcelTemplateService s, CancellationToken ct) => s.ResetPipingAsync(ct));

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

        app.MapGet("/api/units/{unitId:int}/dashboard/cards/{card}", (int unitId, string card, UnitDashboard d, CancellationToken ct) => d.CardAsync(unitId, card, ct))
            .RequirePermission(Permissions.AssetsView);

        app.MapGet("/api/units/{unitId:int}/categories", (int unitId, UnitDashboard d, CancellationToken ct) => d.CategoriesAsync(unitId, ct))
            .RequirePermission(Permissions.AssetsView);

        app.MapPut("/api/units/{unitId:int}/categories", (int unitId, List<SaveCategory> request, UnitDashboard d, CancellationToken ct) =>
            d.SaveCategoriesAsync(unitId, request, ct)).RequirePermission(Permissions.CategoriesManage);

        app.MapGet("/api/units/{unitId:int}/category-options", (int unitId, UnitDashboard d, CancellationToken ct) => d.OptionsAsync(unitId, ct))
            .RequirePermission(Permissions.AssetsView);
    }
}

/// <summary>Choices made in the preview for lines that look like existing ones (row number → "update", "rename" or "create").</summary>
public sealed record ApplyImportRequest(Dictionary<int, string>? Decisions);
