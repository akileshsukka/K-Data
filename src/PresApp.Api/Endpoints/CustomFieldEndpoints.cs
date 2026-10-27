using PresApp.Api.Auth;
using PresApp.Api.CustomFields;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Security;

namespace PresApp.Api.Endpoints;

/// <summary>Custom fields per register (E&amp;I first) and their values on each asset.</summary>
public static class CustomFieldEndpoints
{
    public static void MapCustomFieldEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/catalog/custom-fields", (string? type, bool? all, CustomFieldService s, CancellationToken ct) =>
        {
            AssetType? t = type is null ? null : Enum.TryParse<AssetType>(type, out var x) ? x : throw ApiProblem.Invalid("Unknown asset type.");
            return s.ListAsync(t, activeOnly: all != true, ct);
        }).RequirePermission(Permissions.AssetsView);

        app.MapPost("/api/custom-fields", (SaveCustomFieldRequest r, CustomFieldService s, CancellationToken ct) => s.SaveAsync(null, r, ct))
            .RequirePermission(Permissions.CatalogManage);
        app.MapPut("/api/custom-fields/{id:int}", (int id, SaveCustomFieldRequest r, CustomFieldService s, CancellationToken ct) => s.SaveAsync(id, r, ct))
            .RequirePermission(Permissions.CatalogManage);

        app.MapGet("/api/assets/{id:guid}/custom-fields", (Guid id, CustomFieldService s, CancellationToken ct) => s.ValuesAsync(id, ct))
            .RequirePermission(Permissions.AssetsView);
        app.MapPut("/api/assets/{id:guid}/custom-fields", (Guid id, SaveCustomValuesRequest r, CustomFieldService s, CancellationToken ct) => s.SaveValuesAsync(id, r, ct))
            .RequirePermission(Permissions.AssetsEdit);
    }
}
