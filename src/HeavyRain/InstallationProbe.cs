using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace HeavyRain;

public sealed record InstallationInfo(string? DfcPath, string DfcFileVersion, string[] Products, string[] Evidence, string[] Warnings);

public static class InstallationProbe
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static InstallationInfo Inspect(string? overridePath, Action<string>? reportProgress = null, CancellationToken cancellationToken = default)
    {
        var products = new List<string>();
        var evidence = new List<string>();
        var warnings = new List<string>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new List<string>
        {
            Path.Combine(windows, "SysWOW64", "DFC.exe"),
            Path.Combine(windows, "System32", "DFC.exe")
        };
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportProgress?.Invoke($"Reading {view} installation records…");
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is not null)
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var entry = uninstall.OpenSubKey(name);
                    var displayName = entry?.GetValue("DisplayName") as string;
                    if (displayName is null || !(displayName.Contains("Faronics", StringComparison.OrdinalIgnoreCase) ||
                        displayName.Contains("Deep Freeze", StringComparison.OrdinalIgnoreCase))) continue;
                    var product = $"{displayName} {entry?.GetValue("DisplayVersion")}".Trim();
                    products.Add(product);
                    reportProgress?.Invoke($"Registered: {product}");
                    if (entry?.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location))
                        candidates.Add(Path.Combine(location, "DFC.exe"));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { warnings.Add($"Some {view} installation records could not be read."); }
        }
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        foreach (var product in new[] { "Deep Freeze", "Deep Freeze Enterprise", "Deep Freeze Cloud", "Deep Freeze Install C-0" })
            candidates.Add(Path.Combine(Environment.GetFolderPath(root), "Faronics", product, "DFC.exe"));

        cancellationToken.ThrowIfCancellationRequested();
        reportProgress?.Invoke("Checking Deep Freeze service and driver registrations…");
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is not null)
            foreach (var name in services.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var service = services.OpenSubKey(name);
                var display = service?.GetValue("DisplayName") as string ?? "";
                if (name.Equals("DFServ", StringComparison.OrdinalIgnoreCase) || name.Equals("DeepFrz", StringComparison.OrdinalIgnoreCase) ||
                    display.Contains("Deep Freeze", StringComparison.OrdinalIgnoreCase) || display.Contains("Faronics", StringComparison.OrdinalIgnoreCase))
                    evidence.Add($"{name} — {display}".Trim(' ', '—'));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { warnings.Add("Some service or driver registration records could not be read."); }

        reportProgress?.Invoke($"Found {evidence.Count} matching service or driver registrations.");
        string? path = null;
        foreach (var candidate in overridePath is not null ? [overridePath] : candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportProgress?.Invoke($"Looking for {candidate}");
            if (!File.Exists(candidate)) continue;
            path = candidate;
            reportProgress?.Invoke($"Found DFC.exe: {candidate}");
            break;
        }
        if (path is null) reportProgress?.Invoke("DFC.exe was not found in the checked locations.");
        foreach (var warning in warnings) reportProgress?.Invoke(warning);
        var version = "Unavailable";
        if (path is not null && File.Exists(path))
        {
            try { version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "Not supplied"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new(path, version, products.Distinct().Order().ToArray(), evidence.Distinct().Order().ToArray(), warnings.ToArray());
    }
}
