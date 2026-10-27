namespace PresApp.Api.Hosting;

/// <summary>
/// Cross-site request protection: every API call that changes something must carry the
/// <c>X-Requested-With: K-Data</c> header. Browsers only let the app's own pages add it (another site cannot
/// without a CORS preflight, which is never allowed), so a forged form or link cannot use the user's session.
/// </summary>
public sealed class CsrfHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "K-Data";

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method)
            || !request.Path.StartsWithSegments("/api")
            || string.Equals(request.Headers[HeaderName], HeaderValue, StringComparison.Ordinal))
        {
            return next(context);
        }
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(new { status = 400, title = "Missing request header", detail = "Reload the page and try again." });
    }
}
