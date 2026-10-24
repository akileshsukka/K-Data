using PresApp.Domain.Common;
using PresApp.Infrastructure.Auditing;

namespace PresApp.Api.Hosting;

/// <summary>Fills the audit context (who, source, batch) for every API request.</summary>
public sealed class RequestAuditMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, AuditContext audit)
    {
        audit.UserName = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name ?? "unknown" : "anonymous";
        audit.Source = ChangeSource.App;
        audit.BatchId = Guid.NewGuid();
        return next(context);
    }
}
