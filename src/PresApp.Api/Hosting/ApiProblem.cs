namespace PresApp.Api.Hosting;

/// <summary>A failure with a clear message for the user (404, 400, 403, 409…), turned into JSON by the exception handler.</summary>
public sealed class ApiProblem(int status, string title, string detail, object? extra = null) : Exception(detail)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
    public object? Extra { get; } = extra;

    public static ApiProblem NotFound(string what) => new(404, "Not found", $"{what} was not found.");
    public static ApiProblem Invalid(string detail, object? extra = null) => new(400, "Invalid value", detail, extra);
    public static ApiProblem Forbidden(string detail) => new(403, "Forbidden", detail);
    public static ApiProblem Conflict(string detail, object? extra = null) => new(409, "Changed by someone else", detail, extra);
}
