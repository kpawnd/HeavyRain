using System.Diagnostics;
using System.IO;
using System.Security;
using HeavyRain.Core;
using Microsoft.Win32;

namespace HeavyRain;

public static class PolicyRegistryProbe
{
    public static PolicyRegistryScan Inspect(Action<string>? reportProgress = null, CancellationToken cancellationToken = default)
    {
        var keys = new List<PolicyRegistryKey>();
        var fields = new List<PolicyRegistryField>();
        var candidates = new List<PolicyRegistryCandidate>();
        var warnings = new List<string>();
        var complete = true;
        var budget = Stopwatch.StartNew();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.Elapsed > TimeSpan.FromSeconds(3)) { LimitReached(); break; }
            reportProgress?.Invoke($"Discovering Faronics registry keys ({view})…");
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"SOFTWARE\Faronics", @"SOFTWARE\Policies\Faronics" };
                foreach (var parent in new[] { "SOFTWARE", @"SOFTWARE\Policies" })
                {
                    using var software = machine.OpenSubKey(parent);
                    if (software is null) continue;
                    foreach (var child in software.GetSubKeyNames())
                        if (child.StartsWith("Faronics", StringComparison.OrdinalIgnoreCase)) roots.Add($"{parent}\\{child}");
                }
                var pending = new Queue<(string Path, int Depth)>(roots.Order(StringComparer.OrdinalIgnoreCase).Select(path => (path, 0)));
                var seen = 0;
                while (pending.TryDequeue(out var entry))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++seen > 256 || budget.Elapsed > TimeSpan.FromSeconds(3)) { LimitReached(); break; }
                    var path = $"HKLM\\{entry.Path}";
                    reportProgress?.Invoke($"Checking {path} ({view})");
                    try
                    {
                        using var key = machine.OpenSubKey(entry.Path);
                        if (key is null) { keys.Add(new(path, view.ToString(), "Not found")); continue; }
                        keys.Add(new(path, view.ToString(), "Read"));
                        foreach (var valueName in key.GetValueNames())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!PolicyRegistryRules.IsPolicyField(path, valueName)) continue;
                            if (fields.Count >= 512) { LimitReached(); break; }
                            try
                            {
                                var kind = key.GetValueKind(valueName);
                                var priority = PolicyRegistryRules.NamePriority(path, valueName);
                                var result = "Metadata only; not a recognized current policy name";
                                if (priority is not null && kind == RegistryValueKind.String)
                                {
                                    var name = PolicyRegistryRules.ReadName(key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                                    if (name is not null)
                                    {
                                        candidates.Add(new(name, path, view.ToString(), valueName, priority.Value));
                                        result = "Policy name candidate";
                                        reportProgress?.Invoke($"Policy name candidate: {path}\\{valueName} ({view})");
                                    }
                                    else result = "Empty, invalid or identifier-shaped policy name";
                                }
                                else if (priority is not null) result = "Not used; the policy name is not a string value";
                                fields.Add(new(path, view.ToString(), valueName, kind.ToString(), result));
                            }
                            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
                            {
                                fields.Add(new(path, view.ToString(), valueName, "Unavailable", "Could not read value metadata"));
                                ReadFailed($"{path}\\{valueName}", view);
                            }
                        }
                        var children = key.GetSubKeyNames();
                        if (entry.Depth >= 8)
                        {
                            if (children.Length > 0) LimitReached();
                            continue;
                        }
                        foreach (var child in children.Take(256)) pending.Enqueue(($"{entry.Path}\\{child}", entry.Depth + 1));
                        if (children.Length > 256) LimitReached();
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
                    {
                        keys.Add(new(path, view.ToString(), "Could not read"));
                        ReadFailed(path, view);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            { ReadFailed("HKLM vendor registry keys", view); }
        }
        reportProgress?.Invoke($"Registry discovery finished: {keys.Count} keys checked, {fields.Count} policy fields, {candidates.Count} name candidates.");
        return new(keys.ToArray(), fields.ToArray(), candidates.ToArray(), warnings.Distinct().ToArray(), complete);

        void LimitReached()
        {
            const string message = "Registry discovery reached a scan limit; the report contains a partial result.";
            if (!warnings.Contains(message)) { warnings.Add(message); reportProgress?.Invoke(message); }
            complete = false;
        }

        void ReadFailed(string path, RegistryView view)
        {
            var message = $"Could not read {path} ({view}).";
            warnings.Add(message);
            reportProgress?.Invoke(message);
            complete = false;
        }
    }
}
