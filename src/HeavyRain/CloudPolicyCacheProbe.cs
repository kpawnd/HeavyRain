using System.IO;
using System.Security;
using HeavyRain.Core;
using Microsoft.Win32;

namespace HeavyRain;

public static class CloudPolicyCacheProbe
{
    private const string StorageKey = @"SOFTWARE\Faronics\Faronics Core 3\Storage Spaces\Spaces\FWA";

    public static CloudPolicyInfo Inspect(Action<string>? reportProgress, CancellationToken cancellationToken)
    {
        var checks = new List<PolicyCacheCheck>();
        var results = new Dictionary<string, PolicyCacheResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = $"HKLM\\{StorageKey}\\Path ({view})";
            string? databasePath = null;
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey(StorageKey);
                var root = key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (string.IsNullOrWhiteSpace(root)) { checks.Add(new(source, null, new(null, "Cloud storage path is not registered."))); continue; }
                if (key!.GetValueKind("Path") == RegistryValueKind.ExpandString) root = Environment.ExpandEnvironmentVariables(root);
                if (!Path.IsPathFullyQualified(root) || root.Length < 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':')
                { checks.Add(new(source, null, new(null, "Cloud storage is not a local absolute path."))); continue; }
                databasePath = Path.Combine(Path.GetFullPath(root), "Tasks.dat");
                if (!results.TryGetValue(databasePath, out var result))
                {
                    reportProgress?.Invoke($"Reading cached Cloud policy: {databasePath}");
                    result = CloudPolicyCache.Read(databasePath, cancellationToken);
                    results.Add(databasePath, result);
                }
                checks.Add(new(source, databasePath, result));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException)
            { checks.Add(new(source, databasePath, new(null, "Could not read the registered Cloud storage location."))); }
        }
        var matches = checks.Where(check => check.Result.Name is not null).ToArray();
        var names = matches.Select(check => check.Result.Name!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var info = names.Length == 1
            ? new CloudPolicyInfo(names[0], $"{matches[0].DatabasePath} → CoreInfo/CURRENT_PRODUCTS_POLICY → Policy/@Name", matches[0].Result.Detail)
            : CloudPolicyInfo.Unavailable(names.Length > 1 ? "Registered Cloud caches contain conflicting policy names." : "No readable cached Cloud policy was found.");
        return info with { Cache = checks.ToArray() };
    }
}
