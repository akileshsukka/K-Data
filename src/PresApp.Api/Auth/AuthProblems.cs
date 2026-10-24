namespace PresApp.Api.Auth;

/// <summary>Uniform JSON body for 401/403 answers, read by the front end.</summary>
public static class AuthProblems
{
    public static Task WriteAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new
        {
            status,
            code,
            title = status == 401 ? "Sin acceso" : "Prohibido",
            detail
        });
    }
}
