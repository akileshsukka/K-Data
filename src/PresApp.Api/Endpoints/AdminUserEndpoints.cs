using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Auth;
using PresApp.Api.Cloudflare;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Endpoints;

/// <summary>User and role administration. Every change keeps Cloudflare's allow-list in step.</summary>
public static class AdminUserEndpoints
{
    public sealed record UserDto(int Id, string Email, string DisplayName, int RoleId, string Role, bool IsActive,
        DateTime CreatedAtUtc, DateTime? LastLoginAtUtc, bool HasEmergencyPassword);
    public sealed record SaveUserRequest(string Email, string DisplayName, int RoleId);
    public sealed record UserChangeResponse(UserDto User, CloudflareResult Cloudflare);
    public sealed record RoleDto(int Id, string Name, string? Description, bool IsSystem, IReadOnlyList<string> Permissions, int UserCount);
    public sealed record SaveRolePermissionsRequest(IReadOnlyList<string> Permissions);
    public sealed record LoginDto(DateTime AtUtc, string Email, bool Success, string Method, string? IpAddress, string? Detail);

    public static void MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").RequirePermission(Permissions.UsersManage);

        group.MapGet("/users", async (PresAppDbContext db, CancellationToken ct) =>
            await db.Users.AsNoTracking().OrderBy(u => u.Email).Select(ToDto).ToListAsync(ct));

        group.MapPost("/users", async (SaveUserRequest request, PresAppDbContext db, HttpContext http,
            CloudflareAccessAdmin cloudflare, UserDirectory directory, TimeProvider clock, CancellationToken ct) =>
        {
            var problem = await ValidateAsync(request, db, null, ct);
            if (problem is not null) return problem;

            var user = new AppUser
            {
                Email = AppUser.NormalizeEmail(request.Email),
                DisplayName = request.DisplayName.Trim(),
                RoleId = request.RoleId,
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
                CreatedBy = http.User.Identity?.Name ?? "",
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            directory.Invalidate(user.Email);
            var cf = await SyncCloudflareAsync(db, cloudflare, ct);
            return Results.Ok(new UserChangeResponse(await LoadAsync(db, user.Id, ct), cf));
        });

        group.MapPut("/users/{id:int}", async (int id, SaveUserRequest request, PresAppDbContext db,
            CloudflareAccessAdmin cloudflare, UserDirectory directory, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([id], ct);
            if (user is null) return Results.NotFound();
            var problem = await ValidateAsync(request, db, id, ct);
            if (problem is not null) return problem;
            if (await WouldRemoveLastAdminAsync(db, user, request.RoleId, stillActive: user.IsActive, ct))
            {
                return Results.Problem("At least one active administrator must remain.", statusCode: 409);
            }

            var oldEmail = user.Email;
            user.Email = AppUser.NormalizeEmail(request.Email);
            user.DisplayName = request.DisplayName.Trim();
            user.RoleId = request.RoleId;
            await db.SaveChangesAsync(ct);
            directory.Invalidate(oldEmail);
            directory.Invalidate(user.Email);

            var cf = new CloudflareResult(cloudflare.IsConfigured, true, "No changes in Cloudflare.");
            if (oldEmail != user.Email)
            {
                cf = await SyncCloudflareAsync(db, cloudflare, ct);
                await cloudflare.RevokeSessionsAsync(oldEmail, ct);
            }
            return Results.Ok(new UserChangeResponse(await LoadAsync(db, user.Id, ct), cf));
        });

        group.MapPost("/users/{id:int}/active", async (int id, bool active, PresAppDbContext db, HttpContext http,
            CloudflareAccessAdmin cloudflare, UserDirectory directory, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([id], ct);
            if (user is null) return Results.NotFound();
            if (!active && string.Equals(user.Email, http.User.Identity?.Name, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("You cannot deactivate your own user.", statusCode: 409);
            }
            if (!active && await WouldRemoveLastAdminAsync(db, user, user.RoleId, stillActive: false, ct))
            {
                return Results.Problem("At least one active administrator must remain.", statusCode: 409);
            }

            user.IsActive = active;
            await db.SaveChangesAsync(ct);
            directory.Invalidate(user.Email);
            var cf = await SyncCloudflareAsync(db, cloudflare, ct);
            if (!active)
            {
                var revoke = await cloudflare.RevokeSessionsAsync(user.Email, ct);
                if (!revoke.Success) cf = revoke;
            }
            return Results.Ok(new UserChangeResponse(await LoadAsync(db, user.Id, ct), cf));
        });

        group.MapPost("/cloudflare/sync", async (PresAppDbContext db, CloudflareAccessAdmin cloudflare, CancellationToken ct) =>
            Results.Ok(await SyncCloudflareAsync(db, cloudflare, ct)));

        group.MapGet("/roles", async (PresAppDbContext db, CancellationToken ct) =>
            await db.Roles.AsNoTracking().OrderBy(r => r.Id)
                .Select(r => new RoleDto(r.Id, r.Name, r.Description, r.IsSystem,
                    r.Permissions.Select(p => p.Permission).OrderBy(p => p).ToList(),
                    db.Users.Count(u => u.RoleId == r.Id && u.IsActive)))
                .ToListAsync(ct));

        group.MapGet("/permissions", () => Permissions.All);

        group.MapPut("/roles/{id:int}/permissions", async (int id, SaveRolePermissionsRequest request, PresAppDbContext db,
            UserDirectory directory, CancellationToken ct) =>
        {
            var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct);
            if (role is null) return Results.NotFound();
            if (role.Name == RoleNames.Admin) return Results.Problem("The Admin role always keeps every permission.", statusCode: 409);
            var unknown = request.Permissions.Where(p => !Permissions.IsKnown(p)).ToList();
            if (unknown.Count > 0) return Results.Problem($"Unknown permissions: {string.Join(", ", unknown)}", statusCode: 400);

            role.Permissions.RemoveAll(p => !request.Permissions.Contains(p.Permission));
            foreach (var p in request.Permissions.Distinct().Where(p => role.Permissions.All(x => x.Permission != p)))
            {
                role.Permissions.Add(new RolePermission { Permission = p });
            }
            await db.SaveChangesAsync(ct);
            directory.Invalidate();
            return Results.NoContent();
        });

        group.MapGet("/logins", async (PresAppDbContext db, int? take, CancellationToken ct) =>
            (await db.LoginAudits.AsNoTracking().OrderByDescending(l => l.AtUtc).Take(Math.Clamp(take ?? 200, 1, 1000))
                .ToListAsync(ct))
            .Select(l => new LoginDto(l.AtUtc, l.Email, l.Success, l.Method.ToString(), l.IpAddress, l.Detail)));
    }

    private static readonly System.Linq.Expressions.Expression<Func<AppUser, UserDto>> ToDto = u =>
        new UserDto(u.Id, u.Email, u.DisplayName, u.RoleId, u.Role.Name, u.IsActive, u.CreatedAtUtc, u.LastLoginAtUtc,
            u.EmergencyPasswordHash != null);

    private static Task<UserDto> LoadAsync(PresAppDbContext db, int id, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(u => u.Id == id).Select(ToDto).FirstAsync(ct);

    private static async Task<IResult?> ValidateAsync(SaveUserRequest request, PresAppDbContext db, int? id, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email) || !MailAddress.TryCreate(request.Email.Trim(), out _))
        {
            errors["email"] = ["Enter a valid e-mail."];
        }
        else
        {
            var email = AppUser.NormalizeEmail(request.Email);
            if (await db.Users.AnyAsync(u => u.Email == email && u.Id != id, ct)) errors["email"] = ["That e-mail is already registered."];
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName)) errors["displayName"] = ["Enter the name."];
        if (!await db.Roles.AnyAsync(r => r.Id == request.RoleId, ct)) errors["roleId"] = ["Choose a valid role."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static async Task<bool> WouldRemoveLastAdminAsync(PresAppDbContext db, AppUser user, int newRoleId, bool stillActive,
        CancellationToken ct)
    {
        var adminRoleId = await db.Roles.Where(r => r.Name == RoleNames.Admin).Select(r => r.Id).FirstAsync(ct);
        var isAdminNow = user.RoleId == adminRoleId && user.IsActive;
        var staysAdmin = newRoleId == adminRoleId && stillActive;
        if (!isAdminNow || staysAdmin) return false;
        return !await db.Users.AnyAsync(u => u.Id != user.Id && u.RoleId == adminRoleId && u.IsActive, ct);
    }

    private static async Task<CloudflareResult> SyncCloudflareAsync(PresAppDbContext db, CloudflareAccessAdmin cloudflare, CancellationToken ct)
    {
        var emails = await db.Users.Where(u => u.IsActive).Select(u => u.Email).ToListAsync(ct);
        return await cloudflare.SyncAllowedEmailsAsync(emails, ct);
    }
}
