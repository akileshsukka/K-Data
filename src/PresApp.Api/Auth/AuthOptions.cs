namespace PresApp.Api.Auth;

/// <summary>Settings of the "Auth" section in appsettings.</summary>
public sealed class PresAppAuthOptions
{
    public const string Section = "Auth";

    /// <summary>Cloudflare Access team domain, e.g. "enermech.cloudflareaccess.com".</summary>
    public string CloudflareTeamDomain { get; set; } = "";

    /// <summary>Application Audience (AUD) tag of the Access application.</summary>
    public string CloudflareAudience { get; set; } = "";

    /// <summary>Local-only port for the emergency admin login. Never published through the tunnel.</summary>
    public int EmergencyPort { get; set; } = 5081;

    /// <summary>Development only: sign in as this e-mail without Cloudflare.</summary>
    public string? DevelopmentUserEmail { get; set; }

    /// <summary>Created as admin on first start when no admin exists.</summary>
    public string? BootstrapAdminEmail { get; set; }
}
