using System.IO;
using HeavyRain.Core;
using Microsoft.Win32;

namespace HeavyRain;

public sealed record AecPolicyInfo(AecPolicy? Policy, string LogPath, string? Warning);

/// <summary>Reads Anti-Executable's policy strings and log location from the registry, read-only.</summary>
public static class AecPolicyProbe
{
    private const string LogLocationValue = "AELogLocation";
    private const string LogFileNameValue = "AELogFileName";

    public static string DefaultLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Faronics", "StorageSpace", "AEE", "AE.log");

    public static AecPolicyInfo Read()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(AecPolicy.RegistryKey);
            if (key is null) return new(null, DefaultLogPath, null);
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in AecPolicy.ValueNames) values[name] = key.GetValue(name) as string;
            return new(AecPolicy.FromValues(values), LogPath(key.GetValue(LogLocationValue) as string, key.GetValue(LogFileNameValue) as string), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(null, DefaultLogPath, "Anti-Executable's policy settings could not be read.");
        }
    }

    // Uses the published location only when it is a plain folder plus a bare file name.
    private static string LogPath(string? folder, string? fileName) =>
        folder is not null && fileName is not null && Path.IsPathFullyQualified(folder)
        && fileName.Length > 0 && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            ? Path.Combine(folder, fileName)
            : DefaultLogPath;
}

/// <summary>Sample policy and log lines for <c>--demo</c>. Nothing is read from the PC.</summary>
internal static class AecDemoInsights
{
    public static AecPolicy Policy { get; } = new("AllowAndLogViolations", "EnforceLogViolation", "CustomDeployment", "Off", "TRUE");

    public static IReadOnlyList<string> LogLines { get; } =
    [
        "11/12/2026 08:02:26#LAB-PC-07#SYSTEM#Protection enabled.#Protection enabled.",
        @"11/12/2026 09:14:05#LAB-PC-07#LAB-PC-07\Student#Unknown File Allowed#Unknown File Allowed( C:\Users\Student\Downloads\ChartMaker-setup.exe )",
        @"0#3F2A9C41D07E5B6A8C19E2F44B0D7A6C5E3B1F20#C:\Users\Student\Downloads\ChartMaker-setup.exe#11/12/2026 09:14:05# #.exe#Example Software Ltd#ChartMaker#ChartMaker-setup.exe#4.2.0.0# # #",
        "11/12/2026 10:30:00#LAB-PC-07#SYSTEM#Maintenance mode enabled.#Maintenance mode enabled.",
        "11/12/2026 10:42:18#LAB-PC-07#SYSTEM#Maintenancemode disabled#Maintenancemode disabled",
        "11/12/2026 10:42:18#LAB-PC-07#SYSTEM#Protection enabled.#Protection enabled.",
        @"11/12/2026 11:05:51#LAB-PC-07#LAB-PC-07\Student#Unknown File Allowed#Unknown File Allowed( C:\Program Files (x86)\Example Tools\Viewer\viewer.exe )",
        @"0#9B7E1C2D4F6A8B0C1D3E5F7A9B1C3D5E7F9A1B3C#C:\Program Files (x86)\Example Tools\Viewer\viewer.exe#11/12/2026 11:05:51# #.exe# #Example Viewer#viewer.exe#2.1.7.0# # #"
    ];
}
