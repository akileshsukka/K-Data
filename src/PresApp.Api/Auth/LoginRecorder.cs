using Microsoft.Extensions.Caching.Memory;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Auth;

/// <summary>
/// Writes one LoginAudit row per new Cloudflare session (not per request) and updates LastLoginAtUtc.
/// Failures are recorded too, at most once per e-mail every 10 minutes.
/// </summary>
public sealed class LoginRecorder(IServiceScopeFactory scopes, IMemoryCache cache, TimeProvider clock, ILogger<LoginRecorder> logger)
{
    public async Task RecordAsync(string sessionKey, string email, int? userId, bool success, LoginMethod method,
        string? ip, string? detail, CancellationToken ct)
    {
        var key = "login:" + sessionKey;
        if (cache.TryGetValue(key, out _)) return;
        cache.Set(key, true, success ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(10));

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PresAppDbContext>();
            var now = clock.GetUtcNow().UtcDateTime;
            db.LoginAudits.Add(new LoginAudit
            {
                Email = email, UserId = userId, AtUtc = now, IpAddress = ip, Success = success, Method = method, Detail = detail
            });
            if (success && userId is not null)
            {
                var user = await db.Users.FindAsync([userId.Value], ct);
                if (user is not null) user.LastLoginAtUtc = now;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Never block a sign-in because the audit write failed.
            logger.LogWarning(ex, "Could not record login for {Email}", email);
        }
    }
}
