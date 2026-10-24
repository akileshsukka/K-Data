using System.Security.Claims;
using PresApp.Api.Auth;

namespace PresApp.Api.Endpoints;

public static class MeEndpoints
{
    public sealed record MeResponse(string Email, string DisplayName, string Role, IReadOnlyList<string> Permissions, string AuthMethod);

    public static void MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", (ClaimsPrincipal user) => new MeResponse(
                user.FindFirstValue(AuthConstants.EmailClaim) ?? "",
                user.FindFirstValue("name") ?? "",
                user.FindFirstValue(AuthConstants.RoleClaim) ?? "",
                user.FindAll(AuthConstants.PermissionClaim).Select(c => c.Value).Order().ToList(),
                user.Identity?.AuthenticationType ?? ""))
            .RequireAuthorization();
    }
}
