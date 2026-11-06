using System.Security.Claims;
using PresApp.Api.Assets;
using PresApp.Api.Auth;
using PresApp.Api.Excel;
using PresApp.Api.Hosting;
using PresApp.Api.Groups;
using PresApp.Api.Monitoring;
using PresApp.Domain.Security;

namespace PresApp.Api.Endpoints;

/// <summary>N2 blanket monitoring (DURING / POST), monitoring rounds, and internal groups (workpacks, circuits, systems).</summary>
public static class MonitoringEndpoints
{
    public static void MapMonitoringEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api").RequirePermission(Permissions.AssetsView);

        // ---------------------------------------------------------------- monitoring
        view.MapGet("/monitoring", (int? unitId, int? groupId, MonitoringService s, CancellationToken ct) => s.ViewAsync(unitId, groupId, ct));
        view.MapGet("/settings/monitoring", (MonitoringService s, CancellationToken ct) => s.SettingsAsync(ct));
        app.MapPut("/api/settings/monitoring", (MonitoringSettingsDto r, MonitoringService s, CancellationToken ct) => s.SaveSettingsAsync(r, ct))
            .RequirePermission(Permissions.CatalogManage);
        app.MapPost("/api/monitoring/rounds", (RoundRequest r, ClaimsPrincipal user, MonitoringService s, CancellationToken ct) => s.SaveRoundAsync(r, user, ct))
            .RequirePermission(Permissions.ReadingsWrite);

        // Monitoring report to read and share: until preservation (N2 by ISO week), post-preservation (Pressure by ISO week), all readings.
        app.MapGet("/api/units/{unitId:int}/monitoring/report", async (int unitId, int? groupId, DateOnly? from, DateOnly? to, MonitoringReportWorkbook workbook, CancellationToken ct) =>
        {
            var result = await workbook.ExportAsync(unitId, groupId, from, to, ct);
            return Results.File(result.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result.FileName);
        }).RequirePermission(Permissions.ExcelExport);

        // Readings in bulk from Excel: the empty template (downloaded once, reused) and the upload (preview, then apply in /api/imports).
        app.MapGet("/api/units/{unitId:int}/monitoring/readings-template", async (int unitId, ReadingsWorkbook workbook, CancellationToken ct) =>
        {
            var result = await workbook.TemplateAsync(unitId, ct);
            return Results.File(result.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result.FileName);
        }).RequirePermission(Permissions.ReadingsWrite);
        app.MapPost("/api/units/{unitId:int}/monitoring/readings-import", async (int unitId, IFormFile file, ClaimsPrincipal user, ReadingsWorkbook workbook, CancellationToken ct) =>
        {
            if (!user.HasPermission(Permissions.ExcelImport)) throw ApiProblem.Forbidden("Your role cannot upload Excel files.");
            await using var stream = file.OpenReadStream();
            return await workbook.PreviewAsync(stream, file.FileName, unitId, user, ct);
        }).RequirePermission(Permissions.ReadingsWrite).DisableAntiforgery();

        // ---------------------------------------------------------------- groups
        // The list stays on assets.view: Monitoring uses it for its group filter. The workpack pages need groups.view.
        view.MapGet("/group-types", (GroupService s, CancellationToken ct) => s.TypesAsync(ct));
        view.MapGet("/groups", (int? unitId, bool? includeInactive, GroupService s, CancellationToken ct) => s.ListAsync(unitId, includeInactive == true, ct));
        view.MapGet("/groups/{id:int}", (int id, GroupService s, CancellationToken ct) => s.DetailAsync(id, ct)).RequirePermission(Permissions.GroupsView);

        var manage = app.MapGroup("/api/groups").RequirePermission(Permissions.GroupsManage);
        manage.MapPost("", (SaveGroupRequest r, GroupService s, CancellationToken ct) => s.CreateAsync(r, ct));
        manage.MapPut("/{id:int}", (int id, SaveGroupRequest r, GroupService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        manage.MapPost("/{id:int}/members", (int id, MembersRequest r, ClaimsPrincipal user, GroupService s, CancellationToken ct) =>
            s.AddMembersAsync(id, r.AssetIds, user, ct));
        manage.MapPost("/{id:int}/active", (int id, bool active, GroupService s, CancellationToken ct) => s.SetActiveAsync(id, active, ct));
        manage.MapDelete("/{id:int}", async (int id, GroupService s, CancellationToken ct) => { await s.DeleteAsync(id, ct); return Results.NoContent(); });
        manage.MapPost("/{id:int}/members/paste", (int id, PasteMembersRequest r, ClaimsPrincipal user, GroupService s, CancellationToken ct) =>
            s.PasteMembersAsync(id, r, user, ct));
        manage.MapPost("/{id:int}/members/remove", (int id, MembersRequest r, GroupService s, CancellationToken ct) =>
            s.RemoveMembersAsync(id, r.AssetIds, ct));
    }
}
