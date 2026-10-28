using System.Security.Claims;
using PresApp.Api.Assets;
using PresApp.Api.Auth;
using PresApp.Domain.Assets;
using PresApp.Domain.Security;

namespace PresApp.Api.Endpoints;

public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api").RequirePermission(Permissions.AssetsView);

        view.MapGet("/units/{unitId:int}/assets", (int unitId, string? type, bool? includeInactive, AssetQueries q, CancellationToken ct) =>
            q.RegisterAsync(unitId, ParseType(type) ?? AssetType.Equipment, includeInactive ?? false, ct));

        view.MapGet("/units/{unitId:int}/summary", (int unitId, AssetQueries q, CancellationToken ct) => q.SummaryAsync(unitId, ct));

        view.MapGet("/units/{unitId:int}/history", async (int unitId, DateTime? before, int? take, ClaimsPrincipal user, AssetQueries q, CancellationToken ct) =>
            WithSource(await q.HistoryAsync(null, unitId, before, take ?? 200, ct), user));

        view.MapGet("/assets/search", (string q, int? unitId, string? type, int? take, AssetQueries queries, CancellationToken ct) =>
            queries.SearchAsync(q, unitId, ParseType(type), take ?? 20, ct));

        view.MapGet("/assets/{id:guid}", (Guid id, AssetQueries q, CancellationToken ct) => q.ProfileAsync(id, ct));

        view.MapGet("/assets/{id:guid}/history", async (Guid id, DateTime? before, int? take, ClaimsPrincipal user, AssetQueries q, CancellationToken ct) =>
            WithSource(await q.HistoryAsync(id, null, before, take ?? 300, ct), user));

        view.MapGet("/assets/{id:guid}/readings", (Guid id, AssetEditor e, CancellationToken ct) => e.ReadingsAsync(id, ct));

        // Field-level permissions are checked inside (gate fields vs other fields).
        view.MapPatch("/assets/{id:guid}", (Guid id, PatchRequest request, ClaimsPrincipal user, AssetEditor e, CancellationToken ct) =>
            e.PatchAsync(id, request, user, ct));

        app.MapPut("/api/assets/{id:guid}/dashboard-category", (Guid id, SetCategoryRequest request, ClaimsPrincipal user, AssetEditor e, CancellationToken ct) =>
            e.SetCategoryAsync(id, request, user, ct)).RequirePermission(Permissions.AssetsEdit);

        app.MapPost("/api/assets/{id:guid}/current-gate", (Guid id, SetCurrentGateRequest request, AssetEditor e, CancellationToken ct) =>
            e.SetCurrentGateAsync(id, request, ct)).RequirePermission(Permissions.GatesEdit);

        app.MapPost("/api/assets/{id:guid}/preserved", (Guid id, MarkPreservedRequest request, ClaimsPrincipal user, AssetEditor e, CancellationToken ct) =>
            e.MarkPreservedAsync(id, request.PreservedOn, user, ct)).RequirePermission(Permissions.AssetsMarkPreserved);

        app.MapPut("/api/assets/{id:guid}/gates/{gate:int}/documentation", (Guid id, int gate, GateDocRequest request, AssetEditor e, CancellationToken ct) =>
            e.SetGateDocumentAsync(id, gate, request, ct)).RequirePermission(Permissions.AssetsEdit);

        app.MapPost("/api/assets/{id:guid}/readings", (Guid id, AddReadingsRequest request, ClaimsPrincipal user, AssetEditor e, CancellationToken ct) =>
            e.AddReadingsAsync(id, request, user, ct)).RequirePermission(Permissions.ReadingsWrite);

        app.MapPost("/api/assets", async (CreateAssetRequest request, AssetEditor e, CancellationToken ct) =>
        {
            var created = await e.CreateAsync(request, ct);
            return Results.Created($"/api/assets/{created.Id}", created);
        }).RequirePermission(Permissions.AssetsManage);

        app.MapPost("/api/assets/{id:guid}/active", (Guid id, bool active, AssetEditor e, CancellationToken ct) =>
            e.SetActiveAsync(id, active, ct)).RequirePermission(Permissions.AssetsManage);

        app.MapPost("/api/bulk/preview", (BulkRequest request, ClaimsPrincipal user, AssetEditor e, CancellationToken ct) =>
            e.BulkAsync(request, apply: false, user, ct)).RequirePermission(Permissions.BulkEdit);

        app.MapPost("/api/bulk/apply", (BulkRequest request, ClaimsPrincipal user, AssetEditor e, CancellationToken ct) =>
            e.BulkAsync(request, apply: true, user, ct)).RequirePermission(Permissions.BulkEdit);

        app.MapPost("/api/units/{id:int}/lines/bulk", (int id, bool? apply, BulkLinesRequest request, AssetEditor e, CancellationToken ct) =>
            e.BulkLinesAsync(id, request.Text, apply == true, ct)).RequirePermission(Permissions.AssetsManage);
    }

    private static AssetType? ParseType(string? type) =>
        Enum.TryParse<AssetType>(type, true, out var t) ? t : null;

    /// <summary>Where a change came from (App, Excel, Bulk edit, Simpro…) is shown only to roles with history.source.</summary>
    private static List<HistoryEntry> WithSource(List<HistoryEntry> entries, ClaimsPrincipal user) =>
        user.HasPermission(Permissions.HistorySource) ? entries : entries.Select(e => e with { Source = null }).ToList();
}
