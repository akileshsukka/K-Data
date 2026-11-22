using System.Security.Cryptography;
using System.Text;
using PresApp.Api.Auth;
using PresApp.Api.Reporting;
using PresApp.Domain.Security;

namespace PresApp.Api.Endpoints;

/// <summary>
/// Daily report: the preservation summary of every unit for one site day and the unit highlights. The same summary is
/// served to EPCS (same server) on <c>/api/integration/daily-summary</c>, which EPCS uses to build the general report.
/// </summary>
public static class ReportEndpoints
{
    /// <summary>Header carrying the key shared with EPCS (setting <c>Integration:ApiKey</c>).</summary>
    public const string KeyHeader = "X-KData-Key";

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        // date = yyyy-MM-dd (site day); not given = yesterday. unitId: only that unit.
        app.MapGet("/api/reports/daily", (DateOnly? date, int? unitId, DailySummary s, CancellationToken ct) => s.GetAsync(date, ct, unitId))
            .RequirePermission(Permissions.AssetsView);

        // One unit's daily card (the same card as the general daily report of EPCS), with the unit's activities from EPCS.
        app.MapGet("/api/units/{unitId:int}/daily-card", async (int unitId, DateOnly? date, DailySummary s, EpcsClient epcs, PresApp.Infrastructure.Data.PresAppDbContext db, CancellationToken ct) =>
        {
            var summary = await s.GetAsync(date, ct, unitId);
            var unit = summary.Units.FirstOrDefault() ?? throw Hosting.ApiProblem.NotFound("Unit");
            var (activities, error) = await epcs.UnitActivitiesAsync(unit.UnitNumber, summary.Date, ct);
            return new DailyCardDto(summary.Date, unit, activities ?? [], error);
        }).RequirePermission(Permissions.AssetsView);

        app.MapGet("/api/units/{unitId:int}/highlights", (int unitId, DateOnly date, UnitHighlightService s, CancellationToken ct) => s.ListAsync(unitId, date, ct))
            .RequirePermission(Permissions.AssetsView);
        app.MapPost("/api/units/{unitId:int}/highlights", (int unitId, AddHighlightRequest r, UnitHighlightService s, CancellationToken ct) => s.AddAsync(unitId, r, ct))
            .RequirePermission(Permissions.ReportHighlights);
        app.MapPut("/api/units/{unitId:int}/highlights/{id:int}", (int unitId, int id, SaveHighlightRequest r, UnitHighlightService s, CancellationToken ct) =>
            s.SaveAsync(unitId, id, r, ct)).RequirePermission(Permissions.ReportHighlights);
        app.MapDelete("/api/units/{unitId:int}/highlights/{id:int}", (int unitId, int id, UnitHighlightService s, CancellationToken ct) => s.RemoveAsync(unitId, id, ct))
            .RequirePermission(Permissions.ReportHighlights);

        // EPCS, on this server only: no user, the shared key instead. Off (404) while no key is configured; a request that
        // came through the Cloudflare tunnel is refused even with the key.
        app.MapGet("/api/integration/daily-summary", async (DateOnly? date, HttpContext http, IConfiguration config, DailySummary s, CancellationToken ct) =>
        {
            var key = config["Integration:ApiKey"];
            if (string.IsNullOrWhiteSpace(key)) return Results.NotFound();
            var remote = http.Connection.RemoteIpAddress;
            if (http.Request.Headers.ContainsKey("Cf-Connecting-Ip") || (remote is not null && !System.Net.IPAddress.IsLoopback(remote)))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var sent = http.Request.Headers[KeyHeader].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(key)))
                return Results.Unauthorized();
            return Results.Ok(await s.GetAsync(date, ct));
        }).AllowAnonymous();
    }
}
