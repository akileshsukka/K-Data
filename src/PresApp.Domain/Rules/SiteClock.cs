namespace PresApp.Domain.Rules;

/// <summary>Site time (Dubai) for calendar dates such as "Last Field Action Date".</summary>
public static class SiteClock
{
    public static readonly TimeZoneInfo Dubai = Find("Asia/Dubai", "Arabian Standard Time");
    public static readonly TimeZoneInfo London = Find("Europe/London", "GMT Standard Time");

    public static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, Dubai));

    public static DateOnly DateOf(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, Dubai));

    private static TimeZoneInfo Find(params string[] ids)
    {
        foreach (var id in ids)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        throw new InvalidOperationException($"Time zone not found: {string.Join(", ", ids)}");
    }
}
