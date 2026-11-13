using System.Text.RegularExpressions;

namespace HeavyRain.Core;

/// <summary>
/// Anti-Executable's local policy settings, read from <c>HKLM\SOFTWARE\Faronics\AntiExecutable</c>.
/// The values are the readable strings the policy writes there, such as
/// <c>Unknown Files = AllowAndLogViolations</c>. They describe what Anti-Executable does while
/// protection is on, which the on/off state alone doesn't show.
/// </summary>
/// <remarks>
/// The <c>PROTECTION</c> value in the same key is deliberately not read. On Anti-Executable
/// 6.11.2112.730 it said <c>MAINTENANCE_MODE</c> whether protection was on, off or in Maintenance,
/// so it can't be trusted for state; the WMI provider is used for that instead.
/// </remarks>
public sealed record AecPolicy(string? UnknownFiles, string? ControlList, string? DeploymentType, string? StealthMode, string? WindowsFiles)
{
    public const string RegistryKey = @"SOFTWARE\Faronics\AntiExecutable";
    public const string UnknownFilesValue = "Unknown Files";
    public const string ControlListValue = "Control List";
    public const string DeploymentTypeValue = "Deployment Type";
    public const string StealthModeValue = "StealthMode";
    public const string WindowsFilesValue = "Allow All windows OS files";
    public static readonly string[] ValueNames = [UnknownFilesValue, ControlListValue, DeploymentTypeValue, StealthModeValue, WindowsFilesValue];

    /// <summary>Builds the policy from registry values, or returns null when none of them are present.</summary>
    public static AecPolicy? FromValues(IReadOnlyDictionary<string, string?> values)
    {
        var lookup = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        string? Get(string name) => lookup.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        var policy = new AecPolicy(Get(UnknownFilesValue), Get(ControlListValue), Get(DeploymentTypeValue), Get(StealthModeValue), Get(WindowsFilesValue));
        return policy is { UnknownFiles: null, ControlList: null, DeploymentType: null, StealthMode: null, WindowsFiles: null } ? null : policy;
    }

    /// <summary>
    /// True when the unknown-file setting starts with "Allow", as in the observed
    /// <c>AllowAndLogViolations</c>: unknown programs run and are only logged.
    /// </summary>
    public bool UnknownFilesRun => UnknownFiles?.StartsWith("Allow", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The settings that are present, as label and readable value pairs, in display order.</summary>
    public IReadOnlyList<(string Label, string Value)> Describe()
    {
        var lines = new List<(string, string)>();
        void Add(string label, string? raw) { if (raw is not null) lines.Add((label, Humanize(raw))); }
        Add("Unknown files", UnknownFiles);
        Add("Control list", ControlList);
        Add("Deployment", DeploymentType);
        Add("Stealth mode", StealthMode);
        Add("All Windows OS files allowed", WindowsFiles);
        return lines;
    }

    /// <summary>
    /// Turns a policy string into plain words without interpreting it: "AllowAndLogViolations"
    /// becomes "Allow and log violations", "TRUE" becomes "Yes". Unrecognized formats are shown as written.
    /// </summary>
    public static string Humanize(string raw)
    {
        var value = raw.Trim();
        if (value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return "Yes";
        if (value.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return "No";
        if (!Regex.IsMatch(value, @"\A[A-Za-z]+\z")) return value;
        var words = Regex.Replace(value, "(?<=[a-z])(?=[A-Z])", " ");
        return words.Length <= 1 ? words : char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant();
    }
}
