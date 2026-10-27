namespace PresApp.Domain.Security;

/// <summary>
/// A person allowed into PresApp. Identity comes from the e-mail verified by Cloudflare Access;
/// there is no password except for the local emergency admin.
/// </summary>
public class AppUser
{
    public int Id { get; set; }

    /// <summary>Lower-case e-mail, unique.</summary>
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime? LastLoginAtUtc { get; set; }

    /// <summary>Only for the emergency admin, who signs in on the server itself.</summary>
    public string? EmergencyPasswordHash { get; set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>System roles cannot be deleted.</summary>
    public bool IsSystem { get; set; }
    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission
{
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public string Permission { get; set; } = "";
}

public static class RoleNames
{
    public const string Viewer = "Viewer";
    public const string FieldTechnician = "Field Technician";
    public const string Engineer = "Engineer";
    public const string Admin = "Admin";
}

public enum LoginMethod
{
    Cloudflare = 0,
    Emergency = 1,
    Development = 2
}

public class LoginAudit
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public int? UserId { get; set; }
    public DateTime AtUtc { get; set; }
    public string? IpAddress { get; set; }
    public bool Success { get; set; }
    public LoginMethod Method { get; set; }
    public string? Detail { get; set; }
}
