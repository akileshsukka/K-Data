using System.Security.Claims;
using Microsoft.Extensions.Options;
using PresApp.Api.Assets;
using PresApp.Api.Auth;
using PresApp.Api.Simpro;
using PresApp.Domain.Simpro;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Endpoints;

public static class SimproEndpoints
{
    public sealed record SaveSimproSettings(bool ScheduleEnabled, string ScheduleTime);

    public static void MapSimproEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/simpro").RequirePermission(Permissions.SimproSync);

        g.MapGet("/settings", (PresAppDbContext db, SimproSyncService s, IOptions<SimproOptions> o, CancellationToken ct) =>
            SimproQueries.SettingsAsync(db, s.IsConfigured, o.Value.SyncUsername, ct));

        g.MapPut("/settings", (SaveSimproSettings r, PresAppDbContext db, SimproSyncService s, IOptions<SimproOptions> o, CancellationToken ct) =>
            SimproQueries.SaveSettingsAsync(db, r.ScheduleEnabled, r.ScheduleTime, s.IsConfigured, o.Value.SyncUsername, ct));

        g.MapGet("/runs", (int? take, PresAppDbContext db, CancellationToken ct) => SimproQueries.RecentAsync(db, Math.Clamp(take ?? 30, 1, 200), ct));

        g.MapGet("/runs/{id:long}", (long id, PresAppDbContext db, CancellationToken ct) => SimproQueries.DetailAsync(db, id, ct));

        g.MapPost("/preview", (ClaimsPrincipal user, SimproSyncService s, CancellationToken ct) =>
            s.RunAsync(SyncMode.Preview, user.Identity?.Name ?? "unknown", ct));

        g.MapPost("/apply", (ClaimsPrincipal user, SimproSyncService s, CancellationToken ct) =>
            s.RunAsync(SyncMode.Apply, user.Identity?.Name ?? "unknown", ct));

        g.MapPost("/runs/{id:long}/undo", (long id, ClaimsPrincipal user, SimproSyncService s, CancellationToken ct) =>
            s.UndoAsync(id, user.Identity?.Name ?? "unknown", ct));

        g.MapPost("/force/{assetId:guid}", async (Guid assetId, bool force, ClaimsPrincipal user, SimproSyncService s, CancellationToken ct) =>
        {
            await s.ForceAppAsync(assetId, force, user.Identity?.Name ?? "unknown", ct);
            return Results.NoContent();
        });

        g.MapPost("/runs/{id:long}/force-held", async (long id, ClaimsPrincipal user, SimproSyncService s, CancellationToken ct) =>
            new { Forced = await s.ForceAllHeldAsync(id, user.Identity?.Name ?? "unknown", ct) });

        g.MapPost("/items/{id:long}/copy-to-app", (long id, SimproSyncService s, AssetQueries q, CancellationToken ct) =>
            s.CopyFromSimproAsync(id, q, ct));
    }
}
