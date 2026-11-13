using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HeavyRain;

public sealed record AecInstallationInfo(string? Path, string FileVersion, string[] Warnings);

public static class AecInstallationProbe
{
    public static AecInstallationInfo Inspect(string? overridePath, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var candidates = new List<string>();
        var warnings = new List<string>();
        // Anti-Executable can deny reads of its own service key, even to administrators. That only
        // matters if AEC.exe then isn't found in the usual folders, so it's reported only in that case.
        string? registrationNote = null;
        try
        {
            using var engine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\AEEngine");
            if (engine?.GetValue("ImagePath") is string imagePath)
            {
                var match = Regex.Match(Environment.ExpandEnvironmentVariables(imagePath), "^(?:\"([^\"]+\\.exe)\"|(.+?\\.exe)(?:\\s|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var executable = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (match.Success && Path.IsPathFullyQualified(executable)) candidates.Add(Path.Combine(Path.GetDirectoryName(executable)!, "AEC.exe"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            registrationNote = "The Anti-Executable service registration could not be read.";
            progress?.Invoke("The Anti-Executable service registration couldn't be read; checking the usual folders.");
        }
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        foreach (var folder in new[] { "AE", "Anti-Executable", "AntiExecutable", "Anti-Executable Enterprise", "Faronics Anti-Executable" })
        {
            var location = Environment.GetFolderPath(root);
            if (location.Length > 0) candidates.Add(Path.Combine(location, "Faronics", folder, "AEC.exe"));
        }
        foreach (var path in overridePath is null ? candidates.Distinct(StringComparer.OrdinalIgnoreCase) : [overridePath])
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke($"Looking for {path}");
            if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals("AEC.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (info.CompanyName?.Contains("Faronics", StringComparison.OrdinalIgnoreCase) != true)
                {
                    warnings.Add("The selected AEC.exe does not identify itself as a Faronics executable.");
                    continue;
                }
                progress?.Invoke($"Found AEC.exe: {path}");
                return new(path, info.FileVersion ?? "Unavailable", warnings.ToArray());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { warnings.Add("AEC.exe version information could not be read."); }
        }
        progress?.Invoke("AEC.exe was not found in the checked locations.");
        if (registrationNote is not null) warnings.Insert(0, registrationNote);
        return new(null, "Unavailable", warnings.ToArray());
    }
}
