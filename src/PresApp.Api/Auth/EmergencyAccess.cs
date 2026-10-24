using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Auth;

/// <summary>
/// Break-glass login for when internet or Cloudflare is down. Works only on the local emergency
/// port (Auth:EmergencyPort) from the server itself; that port is never published through the tunnel.
/// </summary>
public static class EmergencyAccess
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, (int Count, DateTime Until)> Failures = new();

    public static bool IsEmergencyRequest(HttpContext context, PresAppAuthOptions options) =>
        context.Connection.LocalPort == options.EmergencyPort
        && context.Connection.RemoteIpAddress is { } ip
        && IPAddress.IsLoopback(ip);

    public sealed record LoginRequest(string Email, string Password);

    public static void MapEmergencyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/emergency").AllowAnonymous();

        group.MapPost("/login", async (LoginRequest request, HttpContext context, PresAppDbContext db,
            IOptions<PresAppAuthOptions> options, UserDirectory directory, TimeProvider clock) =>
        {
            if (!IsEmergencyRequest(context, options.Value)) return Results.NotFound();

            var email = AppUser.NormalizeEmail(request.Email ?? "");
            var now = clock.GetUtcNow().UtcDateTime;
            if (Failures.TryGetValue(email, out var f) && f.Count >= MaxFailures && f.Until > now)
            {
                return Results.Problem("Demasiados intentos. Espera 15 minutos.", statusCode: 429);
            }

            var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Email == email && u.IsActive);
            var ok = user?.EmergencyPasswordHash is not null
                     && user.Role.Name == RoleNames.Admin
                     && new PasswordHasher<AppUser>().VerifyHashedPassword(user, user.EmergencyPasswordHash, request.Password ?? "")
                         != PasswordVerificationResult.Failed;

            db.LoginAudits.Add(new LoginAudit
            {
                Email = email, UserId = user?.Id, AtUtc = now, Success = ok, Method = LoginMethod.Emergency,
                IpAddress = context.Connection.RemoteIpAddress?.ToString()
            });
            await db.SaveChangesAsync();

            if (!ok)
            {
                Failures.AddOrUpdate(email, _ => (1, now + LockTime), (_, old) => (old.Count + 1, now + LockTime));
                return Results.Problem("Correo o contraseña incorrectos.", statusCode: 401);
            }

            Failures.TryRemove(email, out _);
            var directoryUser = await directory.FindActiveAsync(email, context.RequestAborted);
            await context.SignInAsync(AuthConstants.EmergencyScheme,
                UserDirectory.ToPrincipal(directoryUser!, AuthConstants.EmergencyScheme),
                new AuthenticationProperties { IsPersistent = false, ExpiresUtc = clock.GetUtcNow().AddHours(8) });
            return Results.NoContent();
        });

        group.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(AuthConstants.EmergencyScheme);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Command line: <c>PresApp.Api set-emergency-password admin@company.com</c>, then type the password.
    /// The user must already exist and be an admin.
    /// </summary>
    public static async Task<int> SetPasswordFromConsoleAsync(IServiceProvider services, string email)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PresAppDbContext>();
        var normalized = AppUser.NormalizeEmail(email);
        var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Email == normalized);
        if (user is null || user.Role.Name != RoleNames.Admin)
        {
            Console.Error.WriteLine($"{normalized} no existe o no es Admin.");
            return 1;
        }

        Console.Write("Nueva contraseña de emergencia (mínimo 14 caracteres): ");
        var password = ReadHidden();
        if (password.Length < 14)
        {
            Console.Error.WriteLine("Demasiado corta.");
            return 1;
        }
        user.EmergencyPasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
        await db.SaveChangesAsync();
        Console.WriteLine("Contraseña de emergencia guardada.");
        return 0;
    }

    private static string ReadHidden()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }
}
