using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using HeavyRain.Core;

namespace HeavyRain;

/// <summary>Everything Heavy Rain reads about Deep Freeze besides its state, gathered in one pass.</summary>
/// <param name="Entries">Deep Freeze's events, Windows restarts and Cloud tasks, in no particular order.</param>
/// <param name="LastBoot">When Windows last started.</param>
public sealed record DeepFreezeInsights(
    IReadOnlyList<DfHistoryEntry> Entries,
    string? EventProblem,
    string? CloudProblem,
    CloudPolicyDetails? Policy,
    DateTimeOffset LastBoot,
    IReadOnlyList<ComponentStatus> Components,
    string? ComponentProblem);

/// <summary>
/// Reads Deep Freeze's history and surroundings, read-only: the Application and System event logs
/// for the last 30 days, the Cloud agent's task table and policy settings, and the state of the
/// Faronics drivers and services. Each part fails on its own, with a short reason.
/// </summary>
public static class DeepFreezeInsightsProbe
{
    public const int HistoryDays = 30;
    private const int MaxEvents = 400;

    public static DeepFreezeInsights Read(CloudPolicyInfo policy, CancellationToken cancellationToken)
    {
        var entries = new List<DfHistoryEntry>();
        string? eventProblem = null, cloudProblem = null, componentProblem = null;
        try
        {
            var span = (long)TimeSpan.FromDays(HistoryDays).TotalMilliseconds;
            var records = ReadLog("Application", $"*[System[Provider[@Name='{DeepFreezeHistory.Provider}'] and TimeCreated[timediff(@SystemTime) <= {span}]]]", cancellationToken)
                .Concat(ReadLog("System", $"*[System[((Provider[@Name='User32'] and EventID=1074) or (Provider[@Name='EventLog'] and EventID=6008)) and TimeCreated[timediff(@SystemTime) <= {span}]]]", cancellationToken));
            entries.AddRange(records.Select(DeepFreezeHistory.Classify));
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        { eventProblem = $"Windows' event logs couldn't be read ({ex.GetType().Name})."; }

        CloudPolicyDetails? details = null;
        var database = CachePath(policy);
        if (database is null) cloudProblem = "The Cloud agent's cache wasn't found.";
        else
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.AddRange(CloudTaskHistory.ToHistory(CloudTaskHistory.Read(database, cancellationToken)));
            }
            catch (WinSqlite.SqliteReadException ex) { cloudProblem = $"Cloud tasks couldn't be read: {ex.Message}"; }
            details = CloudPolicyCache.ReadDetails(database, cancellationToken);
        }

        IReadOnlyList<ComponentStatus> components = [];
        try { components = ReadComponents(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { componentProblem = $"Windows' driver and service list couldn't be read ({ex.GetType().Name})."; }

        return new(entries, eventProblem, cloudProblem, details, LastBoot(), components, componentProblem);
    }

    public static DateTimeOffset LastBoot() => DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>The cache the policy name came from, or else any registered cache that exists.</summary>
    private static string? CachePath(CloudPolicyInfo policy)
    {
        var paths = policy.Cache.Where(check => check.DatabasePath is not null)
            .OrderByDescending(check => check.Result.Name is not null)
            .Select(check => check.DatabasePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return paths.FirstOrDefault(File.Exists);
    }

    private static List<DfEventRecord> ReadLog(string log, string xpath, CancellationToken cancellationToken)
    {
        var records = new List<DfEventRecord>();
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
        while (records.Count < MaxEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var record = reader.ReadEvent();
            if (record is null) break;
            if (record.TimeCreated is not { } created) continue;
            var data = record.Properties.Select(property => property.Value?.ToString() ?? "").ToList();
            var message = record.ProviderName == DeepFreezeHistory.Provider ? data.FirstOrDefault(value => value.Length > 0) ?? Describe(record) : "";
            records.Add(new(new DateTimeOffset(created), record.ProviderName, record.Id, message, data));
        }
        return records;
    }

    private static string Describe(EventRecord record)
    {
        try { return record.FormatDescription() ?? ""; }
        catch (EventLogException) { return ""; }
    }

    // The service manager reports drivers and services alike; the start type comes from the
    // service's registry key. Both are opened for query only.
    private static IReadOnlyList<ComponentStatus> ReadComponents()
    {
        var manager = OpenSCManagerW(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (manager == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            return ComponentHealth.Expected.Select(expected =>
            {
                var service = OpenServiceW(manager, expected.Name, 0x0004 /* SERVICE_QUERY_STATUS */);
                if (service == IntPtr.Zero)
                    return Marshal.GetLastWin32Error() == 1060 /* ERROR_SERVICE_DOES_NOT_EXIST */
                        ? new ComponentStatus(expected.Name, expected.Role, expected.IsDriver, null, null)
                        : new ComponentStatus(expected.Name, expected.Role, expected.IsDriver, "Unreadable", null);
                try
                {
                    var state = QueryServiceStatus(service, out var status) ? StateName(status.CurrentState) : "Unreadable";
                    using var key = services?.OpenSubKey(expected.Name);
                    var start = key?.GetValue("Start") is int value ? StartName(value) : null;
                    return new ComponentStatus(expected.Name, expected.Role, expected.IsDriver, state, start);
                }
                finally { CloseServiceHandle(service); }
            }).ToList();
        }
        finally { CloseServiceHandle(manager); }
    }

    private static string StateName(uint state) => state switch
    {
        1 => "Stopped", 2 => "Starting", 3 => "Stopping", 4 => "Running", 5 => "Resuming", 6 => "Pausing", 7 => "Paused", _ => $"State {state}"
    };

    private static string StartName(int start) => start switch
    {
        0 => "Boot", 1 => "System", 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => $"Start {start}"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>Where the signed-in account appears in the policy's Anti-Executable lists.</summary>
    public static (AeMembership Membership, AePolicyUser? Entry) CurrentAeMembership(CloudPolicyDetails details)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return details.AeMembershipOf(Environment.UserName, identity.Name, principal.IsInRole);
    }
}

/// <summary>Sample history and policy for <c>--demo</c>. Nothing is read from the PC.</summary>
internal static class DeepFreezeDemoInsights
{
    public static DeepFreezeInsights Create()
    {
        var now = DateTimeOffset.Now;
        var boot = now.AddMinutes(-42);
        DfEventRecord Df(DateTimeOffset at, string message) => new(at, DeepFreezeHistory.Provider, DeepFreezeHistory.StatusEventId, message, [message]);
        DfEventRecord Restart(DateTimeOffset at, string process) => new(at, "User32", 1074, "", [$@"{process} (LAB-PC-07)", "LAB-PC-07", "", "", "restart"]);
        var records = new[]
        {
            Df(now.AddDays(-2).AddHours(-3), "Deep Freeze Status = Frozen"),
            Df(now.AddDays(-1).AddHours(-5), "Deep Freeze status changed to Thawed on Next Boot using the Command Line."),
            Restart(now.AddDays(-1).AddHours(-5).AddMinutes(1), @"C:\Windows\system32\shutdown.exe"),
            Df(now.AddDays(-1).AddHours(-5).AddMinutes(2), "Deep Freeze Status = Thawed"),
            Restart(boot.AddSeconds(-20), @"C:\Windows\System32\RuntimeBroker.exe"),
            Df(boot.AddSeconds(15), "Deep Freeze Status = Thawed"),
            Df(now.AddMinutes(-6), "Deep Freeze status changed to Frozen on Next Boot using the Command Line.")
        };
        CloudTask[] tasks =
        [
            new("AssignPolicy", now.AddDays(-2).AddHours(-6), now.AddDays(-2).AddHours(-6).AddMinutes(4), "Task Succeeded", 0, false),
            new("SetLicenses", now.AddDays(-3), now.AddDays(-3).AddSeconds(1), "Task Failed", -2147467262, false),
            new("ChangeMode", now.AddDays(-4), now.AddDays(-4), "Task Succeeded", 0, false)
        ];
        var entries = records.Select(DeepFreezeHistory.Classify).Concat(CloudTaskHistory.ToHistory(tasks)).ToList();
        var policy = CloudPolicyDetails.Empty with
        {
            CheckInMinutes = 240,
            Schedules =
            [
                new(ScheduleKind.BatchFile, "BatchFile#3", [DayOfWeek.Monday, DayOfWeek.Wednesday], new(11, 11, 0), new(11, 26, 0), true, 60),
                new(ScheduleKind.WindowsUpdate, "Maintenance schedule", [DayOfWeek.Saturday], new(2, 0, 0), new(4, 0, 0), true, null)
            ],
            AeAdministrators = [new("localadmin", false), new(Environment.UserName, false)],
            PersistEventLogs = true, LicenseExpired = false, LastModified = now.AddDays(-2)
        };
        IReadOnlyList<ComponentStatus> components = ComponentHealth.Expected.Select(e => new ComponentStatus(e.Name, e.Role, e.IsDriver, "Running", e.IsDriver ? "Boot" : "Automatic")).ToList();
        return new(entries, null, null, policy, boot, components, null);
    }
}
