using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HeavyRain.Core;
using Microsoft.Win32;

namespace HeavyRain;

public static class CloudPolicyProbe
{
    public static CloudPolicyInfo Inspect()
    {
        var displayed = ReadCloudWindow();
        if (displayed is not null) return displayed;
        var candidates = new List<(string Name, string Source, int Rank)>();
        bool accessLimited = false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                var pending = new Queue<(string Path, int Depth)>();
                pending.Enqueue((@"SOFTWARE\Faronics", 0));
                var seen = 0;
                while (pending.TryDequeue(out var entry) && ++seen <= 256)
                {
                    try
                    {
                        using var key = machine.OpenSubKey(entry.Path);
                        if (key is null) continue;
                        var excluded = new[] { "history", "template", "schedule", "backup", "default", "policies" }
                            .Any(part => entry.Path.Contains(part, StringComparison.OrdinalIgnoreCase));
                        if (excluded) continue;
                        foreach (var valueName in key.GetValueNames())
                        {
                            bool explicitCurrent = valueName.Equals("CurrentPolicyName", StringComparison.OrdinalIgnoreCase) || valueName.Equals("AppliedPolicyName", StringComparison.OrdinalIgnoreCase);
                            bool cloudAgentPolicy = valueName.Equals("PolicyName", StringComparison.OrdinalIgnoreCase)
                                && entry.Path.Contains("cloud", StringComparison.OrdinalIgnoreCase)
                                && entry.Path.Contains("agent", StringComparison.OrdinalIgnoreCase);
                            if (!explicitCurrent && !cloudAgentPolicy) continue;
                            if (key.GetValueKind(valueName) != RegistryValueKind.String) continue;
                            var name = PolicyNameParser.Clean(key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                            if (name is not null) candidates.Add((name, $"HKLM\\{entry.Path}\\{valueName} ({view})", explicitCurrent ? 0 : 1));
                        }
                        if (entry.Depth < 6)
                            foreach (var subkey in key.GetSubKeyNames().Take(256)) pending.Enqueue(($"{entry.Path}\\{subkey}", entry.Depth + 1));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                    { accessLimited = true; }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { accessLimited = true; }
        }
        if (candidates.Count > 0)
        {
            var best = candidates.Where(x => x.Rank == candidates.Min(y => y.Rank)).ToArray();
            if (best.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                return new(best[0].Name, best[0].Source, "Local Cloud metadata; may differ from the latest Cloud console policy.");
            return CloudPolicyInfo.Unavailable("Conflicting local policy values were found. Open the Deep Freeze Cloud window and refresh to read its current-policy label.");
        }
        return CloudPolicyInfo.Unavailable(accessLimited
            ? "Some local policy metadata could not be read. Open Deep Freeze Cloud and refresh."
            : "No policy name is exposed locally. Open the Deep Freeze Cloud window and refresh, or check the Cloud console.");
    }

    private static CloudPolicyInfo? ReadCloudWindow()
    {
        CloudPolicyInfo? result = null;
        var budget = Stopwatch.StartNew();
        EnumWindows((window, _) =>
        {
            if (budget.ElapsedMilliseconds > 1500) return false;
            if (!IsWindowVisible(window)) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (!title.ToString().Equals("Deep Freeze Cloud", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                GetWindowThreadProcessId(window, out var processId);
                using var process = Process.GetProcessById((int)processId);
                var binary = process.MainModule?.FileName;
                if (binary is null || FileVersionInfo.GetVersionInfo(binary).CompanyName?.Contains("Faronics", StringComparison.OrdinalIgnoreCase) != true) return true;
                var labels = new List<string>();
                EnumChildWindows(window, (child, _) =>
                {
                    if (labels.Count >= 64 || budget.ElapsedMilliseconds > 1500) return false;
                    var className = new StringBuilder(64);
                    GetClassName(child, className, className.Capacity);
                    if (!className.ToString().Equals("Static", StringComparison.OrdinalIgnoreCase)) return true;
                    var text = new StringBuilder(512);
                    if (SendMessageTimeout(child, 0x000D, (nuint)text.Capacity, text, 0x0002, 75, out var messageResult) != IntPtr.Zero)
                        labels.Add(text.ToString());
                    return true;
                }, IntPtr.Zero);
                if (PolicyNameParser.FromWindowLabels(labels) is { } name)
                { result = new(name, "Deep Freeze Cloud window", "Read from the local agent's Current Policy label."); return false; }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or IOException or System.Security.SecurityException) { }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, nuint wParam, StringBuilder lParam, uint flags, uint timeout, out nuint result);
}
