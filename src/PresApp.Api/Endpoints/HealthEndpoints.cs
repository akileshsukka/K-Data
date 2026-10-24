using System.Reflection;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (PresAppDbContext db, CancellationToken ct) =>
        {
            var dbOk = await db.Database.CanConnectAsync(ct);
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return Results.Json(new { status = dbOk ? "ok" : "degraded", database = dbOk, version },
                statusCode: dbOk ? 200 : 503);
        }).AllowAnonymous();
    }
}
