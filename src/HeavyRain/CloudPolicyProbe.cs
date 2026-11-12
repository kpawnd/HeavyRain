using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HeavyRain.Core;

namespace HeavyRain;

public static class CloudPolicyProbe
{
    public static CloudPolicyInfo Inspect(Action<string>? reportProgress = null, CancellationToken cancellationToken = default)
    {
        reportProgress?.Invoke("Locating the Cloud agent's policy cache…");
        var cached = CloudPolicyCacheProbe.Inspect(reportProgress, cancellationToken);
        if (cached.Name is not null) return cached;
        var scan = PolicyRegistryProbe.Inspect(reportProgress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var registry = scan.Resolve() with { Cache = cached.Cache };
        if (registry.Name is not null) return registry;
        reportProgress?.Invoke("No unambiguous registry policy name; checking the Cloud window…");
        var displayed = ReadCloudWindow(cancellationToken);
        return displayed is not null ? displayed with { Registry = scan, Cache = cached.Cache } : registry;
    }

    private static CloudPolicyInfo? ReadCloudWindow(CancellationToken cancellationToken)
    {
        CloudPolicyInfo? result = null;
        var budget = Stopwatch.StartNew();
        EnumWindows((window, _) =>
        {
            if (cancellationToken.IsCancellationRequested || budget.ElapsedMilliseconds > 1500) return false;
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
                    if (cancellationToken.IsCancellationRequested || labels.Count >= 64 || budget.ElapsedMilliseconds > 1500) return false;
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
        cancellationToken.ThrowIfCancellationRequested();
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
