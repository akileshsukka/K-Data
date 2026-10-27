using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace PresApp.Api.Hosting;

/// <summary>Turns known failures into clear JSON answers; logs the rest.</summary>
public sealed class PresAppExceptionHandler(ILogger<PresAppExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Non-standard status (nginx convention) for requests the client abandoned.</summary>
    public const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        // The browser left (page change, refresh, query cancelled): nobody is waiting for the answer.
        if (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        if (exception is ApiProblem problem)
        {
            context.Response.StatusCode = problem.Status;
            await context.Response.WriteAsJsonAsync(new { status = problem.Status, title = problem.Title, detail = problem.Message, extra = problem.Extra }, ct);
            return true;
        }

        var (status, title, detail) = exception switch
        {
            DbUpdateConcurrencyException => (409, "Record changed",
                "Someone else changed this record while you were editing it. Reload to see the current value."),
            BadHttpRequestException bad => (bad.StatusCode, "Invalid request", bad.Message),
            _ => (500, "Unexpected error", "Something went wrong. It was logged; try again or tell the administrator.")
        };

        if (status >= 500) logger.LogError(exception, "Unhandled error on {Path}", context.Request.Path);

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { status, title, detail }, ct);
        return true;
    }
}
