namespace HeavyRain.Core;

public sealed record PolicyRegistryKey(string Path, string View, string Result);
public sealed record PolicyRegistryField(string Path, string View, string ValueName, string Type, string Result);
public sealed record PolicyRegistryCandidate(string Name, string Path, string View, string ValueName, int Priority)
{
    public string Source => $"{Path}\\{ValueName} ({View})";
}

public sealed record PolicyRegistryScan(PolicyRegistryKey[] Keys, PolicyRegistryField[] Fields,
    PolicyRegistryCandidate[] Candidates, string[] Warnings, bool Complete)
{
    public CloudPolicyInfo Resolve()
    {
        CloudPolicyInfo result;
        if (!Complete)
            result = CloudPolicyInfo.Unavailable("The registry search was incomplete. Export a diagnostic report for the locations checked and any access errors.");
        else if (Candidates.Length == 0)
            result = CloudPolicyInfo.Unavailable("No local policy name found. See activity for diagnostics.");
        else
        {
            var priority = Candidates.Min(candidate => candidate.Priority);
            var best = Candidates.Where(candidate => candidate.Priority == priority).ToArray();
            result = best.Select(candidate => candidate.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? new(best[0].Name, best[0].Source, "Read from local registry metadata; the Cloud console may have newer settings.")
                : CloudPolicyInfo.Unavailable("Registry policy names conflict. Export a diagnostic report to see the matching locations.");
        }
        return result with { Registry = this };
    }
}

public static class PolicyRegistryRules
{
    public static bool IsPolicyField(string path, string valueName) =>
        path.Contains("policy", StringComparison.OrdinalIgnoreCase) || path.Contains("policies", StringComparison.OrdinalIgnoreCase) ||
        valueName.Contains("policy", StringComparison.OrdinalIgnoreCase) || valueName.Contains("policies", StringComparison.OrdinalIgnoreCase);

    public static int? NamePriority(string path, string valueName)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();
        var excluded = new[] { "history", "histories", "template", "schedule", "backup", "default", "policies", "pending", "queued", "previous" };
        if (parts.Any(part => excluded.Any(marker => part.Contains(marker, StringComparison.Ordinal)))) return null;
        return Normalize(valueName) switch
        {
            "currentpolicyname" or "appliedpolicyname" or "activepolicyname" => 0,
            "assignedpolicyname" when parts.Any(part => part.Contains("cloud", StringComparison.Ordinal)) => 1,
            "policyname" when parts.Any(part => part.Contains("cloud", StringComparison.Ordinal)) => 2,
            _ => null
        };
    }

    public static string? ReadName(string? value)
    {
        var name = PolicyNameParser.Clean(value);
        return name is not null && !Guid.TryParse(name, out _) ? name : null;
    }

    private static string Normalize(string value) => new(value.Where(character => character is not ' ' and not '_' and not '-').Select(char.ToLowerInvariant).ToArray());
}
