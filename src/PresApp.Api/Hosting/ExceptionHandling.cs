using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace PresApp.Api.Hosting;

/// <summary>Turns known failures into clear JSON answers; logs the rest.</summary>
public sealed class PresAppExceptionHandler(ILogger<PresAppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title, detail) = exception switch
        {
            DbUpdateConcurrencyException => (409, "Registro modificado",
                "Otra persona cambió este registro mientras lo editabas. Recarga para ver el valor actual."),
            BadHttpRequestException bad => (bad.StatusCode, "Solicitud no válida", bad.Message),
            _ => (500, "Error inesperado", "Ocurrió un error. Quedó registrado; inténtalo de nuevo o avisa al administrador.")
        };

        if (status >= 500) logger.LogError(exception, "Unhandled error on {Path}", context.Request.Path);

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { status, title, detail }, ct);
        return true;
    }
}
