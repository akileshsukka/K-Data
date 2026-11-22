namespace PresApp.Domain.Rules;

/// <summary>Site time (Dubai) for calendar dates such as "Last Field Action Date".</summary>
public static class SiteClock
{
    public static readonly TimeZoneInfo Dubai = Find("Asia/Dubai", "Arabian Standard Time");
    public static readonly TimeZoneInfo London = Find("Europe/London", "GMT Standard Time");

    public static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, Dubai));

    public static DateOnly DateOf(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, Dubai));

    /// <summary>Midnight at the start of a site day, in UTC (a site day ends at the start of the next one).</summary>
    public static DateTime StartOfDayUtc(DateOnly date) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), Dubai);

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
