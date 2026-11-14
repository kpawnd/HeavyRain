using System.Globalization;
using System.Text.RegularExpressions;

namespace HeavyRain.Core;

/// <summary>
/// Deep Freeze's licence as DFC reports it. On 9.00.520.5760, <c>get /LICENSETYPE</c> printed
/// "License Type: Subscription" and <c>get /LICENSESTATUS</c> "License Status: Licensed", each
/// followed by "Expiry Date: Sunday, January 10, 2027".
/// </summary>
public sealed partial record DfLicense(string? Type, string? Status, DateOnly? Expiry)
{
    public const int WarningDays = 30;

    [GeneratedRegex(@"License Type:\s*(?<value>.+?)\s*(?=Expiry Date:|\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TypePattern();
    [GeneratedRegex(@"License Status:\s*(?<value>.+?)\s*(?=Expiry Date:|\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StatusPattern();
    [GeneratedRegex(@"Expiry Date:\s*(?<value>[^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExpiryPattern();

    public static DfLicense? Parse(string? typeOutput, string? statusOutput)
    {
        static string? Field(Regex pattern, string? text) =>
            text is not null && pattern.Match(text) is { Success: true } match && Regex.Replace(match.Groups["value"].Value, @"\s+", " ").Trim() is { Length: > 0 and <= 80 } value ? value : null;
        var type = Field(TypePattern(), typeOutput);
        var status = Field(StatusPattern(), statusOutput);
        var expiry = ParseDate(Field(ExpiryPattern(), typeOutput) ?? Field(ExpiryPattern(), statusOutput));
        return type is null && status is null && expiry is null ? null : new(type, status, expiry);
    }

    public static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim().TrimEnd('.');
        string[] formats = ["dddd, MMMM d, yyyy", "MMMM d, yyyy", "yyyy-MM-dd", "M/d/yyyy", "d MMMM yyyy"];
        return DateTime.TryParseExact(value, formats, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var date)
            ? DateOnly.FromDateTime(date) : null;
    }

    public int? DaysLeft(DateOnly today) => Expiry is { } expiry ? expiry.DayNumber - today.DayNumber : null;

    public bool IsLicensed => Status?.Equals("Licensed", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>True when the licence is expired, expires within <see cref="WarningDays"/>, or DFC reports a status other than Licensed.</summary>
    public bool NeedsAttention(DateOnly today) => Status is not null && !IsLicensed || DaysLeft(today) is <= WarningDays;

    /// <summary>"Subscription · until 10 Jan 2027", with a warning in place of the date when it's close or past.</summary>
    public string Describe(DateOnly today)
    {
        var parts = new List<string>();
        if (Type is not null) parts.Add(Type);
        if (Status is not null && !IsLicensed) parts.Add(Status);
        if (Expiry is { } expiry)
        {
            var left = DaysLeft(today)!.Value;
            parts.Add(left < 0 ? $"expired {expiry.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
                : left == 0 ? "expires today"
                : left <= WarningDays ? $"expires in {left} day{(left == 1 ? "" : "s")}"
                : $"until {expiry.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}");
        }
        return parts.Count == 0 ? "Unavailable" : string.Join(" · ", parts);
    }
}
