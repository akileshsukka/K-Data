using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Auth;

public sealed record DirectoryUser(int Id, string Email, string DisplayName, string Role, IReadOnlyList<string> Permissions);

/// <summary>
/// Resolves a verified e-mail to an active PresApp user with its permissions.
/// Cached for 30 seconds, so deactivating a user takes effect almost at once.
/// </summary>
public sealed class UserDirectory(IServiceScopeFactory scopes, IMemoryCache cache)
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(30);

    public async Task<DirectoryUser?> FindActiveAsync(string email, CancellationToken ct)
    {
        var normalized = AppUser.NormalizeEmail(email);
        if (cache.TryGetValue(Key(normalized), out DirectoryUser? cached)) return cached;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PresAppDbContext>();
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Email == normalized && u.IsActive)
            .Select(u => new DirectoryUser(u.Id, u.Email, u.DisplayName, u.Role.Name,
                u.Role.Permissions.Select(p => p.Permission).ToList()))
            .FirstOrDefaultAsync(ct);

        cache.Set(Key(normalized), user, CacheTime);
        return user;
    }

    /// <summary>Drops cached entries after an admin changes users or roles.</summary>
    public void Invalidate(string? email = null)
    {
        if (email is null)
        {
            if (cache is MemoryCache mc) mc.Clear();
            return;
        }
        cache.Remove(Key(AppUser.NormalizeEmail(email)));
    }

    public static ClaimsPrincipal ToPrincipal(DirectoryUser user, string scheme)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Email),
            new(AuthConstants.EmailClaim, user.Email),
            new(AuthConstants.UserIdClaim, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(AuthConstants.RoleClaim, user.Role),
            new("name", user.DisplayName)
        };
        claims.AddRange(user.Permissions.Select(p => new Claim(AuthConstants.PermissionClaim, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, ClaimTypes.Name, AuthConstants.RoleClaim));
    }

    private static string Key(string email) => "user:" + email;
}
