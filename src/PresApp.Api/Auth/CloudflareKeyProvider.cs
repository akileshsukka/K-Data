using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace PresApp.Api.Auth;

/// <summary>
/// Downloads and caches the public keys Cloudflare Access signs its tokens with
/// (https://&lt;team&gt;/cdn-cgi/access/certs). Refreshes hourly, or early when a token uses an unknown key.
/// </summary>
public sealed class CloudflareKeyProvider(HttpClient http, IOptions<PresAppAuthOptions> options, TimeProvider clock,
    ILogger<CloudflareKeyProvider> logger)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<SecurityKey> keys = [];
    private DateTimeOffset loadedAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(string? kid, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var unknownKid = kid is not null && keys.All(k => k.KeyId != kid);
        var stale = now - loadedAt > MaxAge;
        if (!stale && !(unknownKid && now - loadedAt > MinRefreshInterval)) return keys;

        await gate.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() - loadedAt > MinRefreshInterval || keys.Count == 0)
            {
                var url = $"https://{options.Value.CloudflareTeamDomain.Trim().TrimEnd('/')}/cdn-cgi/access/certs";
                var json = await http.GetStringAsync(url, ct);
                keys = new JsonWebKeySet(json).GetSigningKeys().ToList();
                loadedAt = clock.GetUtcNow();
                logger.LogInformation("Loaded {Count} Cloudflare Access signing keys", keys.Count);
            }
        }
        catch (Exception ex) when (keys.Count > 0)
        {
            logger.LogWarning(ex, "Could not refresh Cloudflare keys; using the cached ones");
        }
        finally
        {
            gate.Release();
        }
        return keys;
    }
}
