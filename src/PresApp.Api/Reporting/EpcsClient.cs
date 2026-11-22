using System.Net.Http.Json;
using System.Text.Json;

namespace PresApp.Api.Reporting;

/// <summary>An activity of a unit on one day, as EPCS sends it (people come from its attendance).</summary>
public sealed record EpcsActivity(int Id, int LocationId, string Name, string Status, int People, string? Comment, bool IsHighlight);

public sealed record EpcsUnitActivities(DateOnly Date, string? Unit, List<EpcsActivity> Activities);

/// <summary>
/// EPCS on the same server: the site activities of one plant unit on a day, for the one-unit daily card. Settings
/// <c>Epcs:BaseUrl</c> (default http://127.0.0.1:8080) and the key shared with EPCS, <c>Integration:ApiKey</c> (the same value
/// as EPCS's <c>KData:ApiKey</c>). Never throws: a failure comes back as the reason and the card goes out without activities.
/// </summary>
public sealed class EpcsClient(HttpClient http, IConfiguration config, ILogger<EpcsClient> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(List<EpcsActivity>? Activities, string? Error)> UnitActivitiesAsync(int unitNumber, DateOnly date, CancellationToken ct)
    {
        var key = config["Integration:ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) return (null, "EPCS is not connected (setting Integration:ApiKey): activities are missing.");
        var baseUrl = (config["Epcs:BaseUrl"] is { Length: > 0 } b ? b : "http://127.0.0.1:8080").TrimEnd('/');
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/integration/unit-activities?date={date:yyyy-MM-dd}&unit={unitNumber}");
            request.Headers.Add(Endpoints.ReportEndpoints.KeyHeader, key);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("EPCS unit activities answered {Status}", (int)response.StatusCode);
                return (null, $"EPCS did not answer the activities ({(int)response.StatusCode}).");
            }
            var body = await response.Content.ReadFromJsonAsync<EpcsUnitActivities>(Json, ct);
            return (body?.Activities ?? [], null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(e, "EPCS unit activities not reachable");
            return (null, "EPCS is not reachable: activities are missing.");
        }
    }
}
