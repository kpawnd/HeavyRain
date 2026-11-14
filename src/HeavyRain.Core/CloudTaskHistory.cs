using System.Globalization;

namespace HeavyRain.Core;

/// <summary>A task the Faronics Cloud agent ran, as recorded in its cache.</summary>
public sealed record CloudTask(string Name, DateTimeOffset? Started, DateTimeOffset? Ended, string? Result, int? HResult, bool Internal);

/// <summary>
/// The Cloud agent's task history, from the <c>Tasks_V1</c> table of its cache (<c>Tasks.dat</c>).
/// Only names, times and results are read. The task bodies (<c>SerializedTaskXml</c>) and task IDs
/// are never selected, since they can carry the policy, licence keys and other secrets.
/// </summary>
public static class CloudTaskHistory
{
    public const string Query =
        "SELECT TaskName, StartTime, EndTime, StatusUpdateTime, TimeStamp, ErrorMsg, HrResult, IsInternalTask FROM Tasks_V1 ORDER BY rowid DESC LIMIT 150";

    public static IReadOnlyList<CloudTask> Read(string databasePath, CancellationToken cancellationToken = default) =>
        WinSqlite.Query(databasePath, Query, 150, 512, cancellationToken).Select(FromRow).Where(task => task is not null).Select(task => task!).ToList();

    public static CloudTask? FromRow(IReadOnlyDictionary<string, string?> row)
    {
        string? Get(string name) => row.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        var name = Get("TaskName");
        if (name is null) return null;
        var started = ParseTime(Get("StartTime")) ?? ParseTime(Get("TimeStamp"));
        var ended = ParseTime(Get("EndTime")) ?? ParseTime(Get("StatusUpdateTime"));
        var hresult = int.TryParse(Get("HrResult"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : (int?)null;
        return new(name, started, ended, Get("ErrorMsg"), hresult, Get("IsInternalTask") == "1");
    }

    /// <summary>
    /// The cache stores local times as "yyyy-MM-dd HH:mm:ss.fff". On the test VM these matched the
    /// Cloud agent's local-time logs, and an AssignPolicy at 00:12 local matched the policy's
    /// 08:12 UTC timestamp, so they are read as local time.
    /// </summary>
    public static DateTimeOffset? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] formats = ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"];
        return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)
            ? new DateTimeOffset(time) : null;
    }

    public static bool IsFailure(CloudTask task) =>
        task.HResult is { } code && code != 0 || task.Result?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Routine tasks the agent schedules for itself (inventory updates) are left out; everything the
    /// Cloud console sent stays, including failures.
    /// </summary>
    public static IEnumerable<DfHistoryEntry> ToHistory(IEnumerable<CloudTask> tasks) =>
        tasks.Where(task => !(task.Internal && task.Name.Equals("UpdateInventory", StringComparison.OrdinalIgnoreCase) && !IsFailure(task)))
             .Where(task => task.Started is not null || task.Ended is not null)
             .Select(task =>
             {
                 var failed = IsFailure(task);
                 var result = failed ? $"Failed{(task.HResult is { } code and not 0 ? $" (0x{code:X8})" : "")}"
                     : task.Result is { } text && !text.Equals("Task Succeeded", StringComparison.OrdinalIgnoreCase) ? text : "Succeeded";
                 var duration = task.Started is { } start && task.Ended is { } end && end >= start ? $" · took {Describe(end - start)}" : "";
                 return new DfHistoryEntry(task.Started ?? task.Ended!.Value, DfEventKind.CloudTask, ProtectionState.Unknown,
                     Title(task.Name), $"Cloud task {task.Name} · {result}{duration}", failed);
             });

    public static string Title(string name) => name switch
    {
        "ChangeMode" => "Cloud changed Deep Freeze's mode",
        "AssignPolicy" => "Cloud policy applied",
        "SetLicenses" => "Licences updated",
        "SetMaintenance" => "Cloud maintenance scheduled",
        "UpdateInventory" => "Inventory sent to the Cloud",
        "RebootThawed" or "BootThawed" => "Cloud restart into Thawed",
        "RebootFrozen" or "BootFrozen" => "Cloud restart into Frozen",
        "RunWindowsUpdate" => "Cloud Windows Update",
        _ => $"Cloud task: {name}"
    };

    private static string Describe(TimeSpan span) =>
        span.TotalSeconds < 1 ? "under a second" : span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds} s"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min" : $"{span.TotalHours:0.#} h";
}
