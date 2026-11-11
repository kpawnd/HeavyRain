namespace HeavyRain.Core;

public sealed record CloudPolicyInfo(string? Name, string Source, string Detail)
{
    public PolicyRegistryScan? Registry { get; init; }
    public static CloudPolicyInfo Unavailable(string detail) => new(null, "Unavailable", detail);
}

public static class PolicyNameParser
{
    public static string? FromWindowLabels(IEnumerable<string> labels)
    {
        var lines = labels.Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            const string prefix = "Current Policy:";
            if (!lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var value = lines[i][prefix.Length..].Trim();
            if (value.Length == 0 && i + 1 < lines.Length && !lines[i + 1].Contains(':')) value = lines[i + 1];
            return Clean(value);
        }
        return null;
    }

    public static string? Clean(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 256 || value.Any(char.IsControl) ? null : value;
    }
}
