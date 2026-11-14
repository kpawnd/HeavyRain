namespace HeavyRain.Core;

public static class DurationText
{
    /// <summary>"under a minute", "42 min", "2 h 14 min", "1 d 4 h": two units at most, rounded down.</summary>
    public static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes < 1 ? "under a minute"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h{(span.Minutes > 0 ? $" {span.Minutes} min" : "")}"
            : $"{(int)span.TotalDays} d{(span.Hours > 0 ? $" {span.Hours} h" : "")}";
    }
}
