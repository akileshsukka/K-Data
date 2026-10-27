using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PresApp.Api.Cloudflare;

/// <summary>Settings of the "Cloudflare" section. Empty = user list is kept in Cloudflare by hand.</summary>
public sealed class CloudflareOptions
{
    public const string Section = "Cloudflare";
    public string AccountId { get; set; } = "";

    /// <summary>API token with permission "Access: Organizations, Identity Providers, and Groups: Edit".</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>Access group whose e-mail list is the list of allowed users.</summary>
    public string AccessGroupId { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(ApiToken) && !string.IsNullOrWhiteSpace(AccessGroupId);
}

public sealed record CloudflareResult(bool Configured, bool Success, string Message);

/// <summary>
/// Keeps the Cloudflare Access allow-list equal to PresApp's active users, so users are managed in
/// one place. Also revokes a user's Cloudflare session when the user is deactivated.
/// </summary>
public sealed class CloudflareAccessAdmin(HttpClient http, IOptions<CloudflareOptions> options, ILogger<CloudflareAccessAdmin> logger)
{
    private const string Api = "https://api.cloudflare.com/client/v4";

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<CloudflareResult> SyncAllowedEmailsAsync(IEnumerable<string> emails, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured) return NotConfigured();

        try
        {
            var url = $"{Api}/accounts/{o.AccountId}/access/groups/{o.AccessGroupId}";
            using var get = Request(HttpMethod.Get, url);
            using var current = await http.SendAsync(get, ct);
            var body = await ReadAsync(current, ct);
            var name = body?["result"]?["name"]?.GetValue<string>() ?? "PresApp users";

            var payload = new JsonObject
            {
                ["name"] = name,
                ["include"] = new JsonArray(emails.Distinct(StringComparer.OrdinalIgnoreCase).Order()
                    .Select(e => (JsonNode)new JsonObject { ["email"] = new JsonObject { ["email"] = e } }).ToArray())
            };
            using var put = Request(HttpMethod.Put, url);
            put.Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(put, ct);
            await ReadAsync(response, ct);
            return new CloudflareResult(true, true, "Cloudflare allow-list updated.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cloudflare allow-list sync failed");
            return new CloudflareResult(true, false, "Could not update Cloudflare: " + ex.Message);
        }
    }

    public async Task<CloudflareResult> RevokeSessionsAsync(string email, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured) return NotConfigured();
        try
        {
            using var post = Request(HttpMethod.Post, $"{Api}/accounts/{o.AccountId}/access/organizations/revoke_user");
            post.Content = new StringContent(JsonSerializer.Serialize(new { email }), System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(post, ct);
            await ReadAsync(response, ct);
            return new CloudflareResult(true, true, "Cloudflare sessions revoked.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cloudflare session revoke failed for {Email}", email);
            return new CloudflareResult(true, false, "Could not revoke the Cloudflare sessions: " + ex.Message);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiToken);
        return request;
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        var node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        if (!response.IsSuccessStatusCode || node?["success"]?.GetValue<bool>() == false)
        {
            var errors = node?["errors"]?.ToJsonString() ?? text;
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {errors}");
        }
        return node;
    }

    private static CloudflareResult NotConfigured() =>
        new(false, false, "Cloudflare is not configured: add or remove the e-mail by hand in Cloudflare Access.");
}
