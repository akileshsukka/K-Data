using PresApp.Domain.Common;

namespace PresApp.Infrastructure.Auditing;

/// <summary>Who is saving and why. The API fills it per request; jobs set it explicitly.</summary>
public interface IAuditContext
{
    string UserName { get; }
    ChangeSource Source { get; }
    Guid BatchId { get; }
}

/// <summary>Mutable implementation used by the API (scoped per request) and by background jobs.</summary>
public sealed class AuditContext : IAuditContext
{
    public string UserName { get; set; } = "system";
    public ChangeSource Source { get; set; } = ChangeSource.App;
    public Guid BatchId { get; set; } = Guid.NewGuid();
}
