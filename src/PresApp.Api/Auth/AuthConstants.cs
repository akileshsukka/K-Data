namespace PresApp.Api.Auth;

public static class AuthConstants
{
    /// <summary>Policy scheme that picks the real scheme per request.</summary>
    public const string Scheme = "PresApp";
    public const string CloudflareScheme = "Cloudflare";
    public const string EmergencyScheme = "Emergency";
    public const string DevelopmentScheme = "Development";

    public const string PermissionClaim = "perm";
    public const string RoleClaim = "role";
    public const string EmailClaim = "email";
    public const string UserIdClaim = "uid";

    public const string PolicyPrefix = "perm:";
    public static string Policy(string permission) => PolicyPrefix + permission;
}
