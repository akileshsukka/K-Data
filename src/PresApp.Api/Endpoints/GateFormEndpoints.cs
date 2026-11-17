using System.Security.Claims;
using PresApp.Api.Assets;
using PresApp.Api.Auth;
using PresApp.Api.GateForms;
using PresApp.Domain.Security;

namespace PresApp.Api.Endpoints;

public static class GateFormEndpoints
{
    public static void MapGateFormEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------------------------------------------------------------- forms of the assets chosen in a register
        // Values saved before for each asset, the defaults and the fields of the template in use.
        app.MapPost("/api/units/{unitId:int}/gate-forms/prepare", (int unitId, GateFormPrepareRequest request, ClaimsPrincipal user, GateFormService s, CancellationToken ct) =>
            s.PrepareAsync(unitId, request, user.HasPermission(Permissions.AssetsEdit), ct)).RequirePermission(Permissions.ExcelExport);

        // The Word file, one page per asset. The values typed are saved with the assets when the role may edit them.
        app.MapPost("/api/units/{unitId:int}/gate-forms/generate", async (int unitId, GateFormGenerateRequest request, ClaimsPrincipal user, GateFormService s, CancellationToken ct) =>
        {
            var (name, content) = await s.GenerateAsync(unitId, request, user.HasPermission(Permissions.AssetsEdit), ct);
            return Results.File(content, GateFormService.DocxType, name);
        }).RequirePermission(Permissions.ExcelExport);

        // ---------------------------------------------------------------- administration
        var admin = app.MapGroup("/api/gate-forms").RequirePermission(Permissions.CatalogManage);

        admin.MapGet("/settings", (GateFormService s, CancellationToken ct) => s.SettingsAsync(ct));
        admin.MapPut("/settings/{unitId:int}", (int unitId, SaveGateFormUnitSetting request, GateFormService s, CancellationToken ct) =>
            s.SaveSettingAsync(unitId, request, ct));

        admin.MapGet("/templates", (GateFormService s, CancellationToken ct) => s.TemplatesAsync(ct));

        admin.MapPost("/templates/{plantId:int}/{kind}", async (int plantId, string kind, IFormFile file, ClaimsPrincipal user, GateFormService s, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await s.UploadAsync(plantId, GateFormService.ParseKind(kind), stream, file.FileName, user.Identity?.Name ?? "unknown", ct);
        }).DisableAntiforgery();

        admin.MapGet("/templates/{plantId:int}/{kind}/file", async (int plantId, string kind, int? version, GateFormService s, CancellationToken ct) =>
        {
            var (name, content) = await s.TemplateFileAsync(plantId, GateFormService.ParseKind(kind), version, ct);
            return Results.File(content, GateFormService.DocxType, name);
        });

        admin.MapGet("/templates/{plantId:int}/{kind}/sample", async (int plantId, string kind, int? version, GateFormService s, CancellationToken ct) =>
        {
            var (name, content) = await s.SampleAsync(plantId, GateFormService.ParseKind(kind), version, ct);
            return Results.File(content, GateFormService.DocxType, name);
        });

        admin.MapPost("/templates/{plantId:int}/{kind}/versions/{id:int}/activate", async (int plantId, string kind, int id, GateFormService s, CancellationToken ct) =>
        {
            await s.ActivateAsync(plantId, GateFormService.ParseKind(kind), id, ct);
            return Results.NoContent();
        });

        admin.MapDelete("/templates/{plantId:int}/{kind}/versions/{id:int}", async (int plantId, string kind, int id, GateFormService s, CancellationToken ct) =>
        {
            await s.DeleteVersionAsync(plantId, GateFormService.ParseKind(kind), id, ct);
            return Results.NoContent();
        });

        admin.MapPost("/templates/{plantId:int}/{kind}/built-in", async (int plantId, string kind, GateFormService s, CancellationToken ct) =>
        {
            await s.UseBuiltInAsync(plantId, GateFormService.ParseKind(kind), ct);
            return Results.NoContent();
        });
    }
}
