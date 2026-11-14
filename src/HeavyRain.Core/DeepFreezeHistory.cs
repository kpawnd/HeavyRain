using System.Text.RegularExpressions;

namespace HeavyRain.Core;

public enum DfEventKind { BootStatus, NextBootRequest, ImmediateChange, Restart, UnexpectedShutdown, CloudTask, Other }

/// <summary>One raw event as read from a Windows event log.</summary>
/// <param name="Message">For Deep Freeze's events, the event's first data field, which holds the whole message.</param>
/// <param name="Data">The event's data fields, used for Windows' own events whose messages are localized.</param>
public sealed record DfEventRecord(DateTimeOffset Time, string Provider, int Id, string Message, IReadOnlyList<string>? Data = null);

/// <summary>One line of the Deep Freeze history, from Deep Freeze's events, Windows restarts or Cloud tasks.</summary>
/// <param name="State">The boot state the entry reports or requests; Unknown when it isn't about one.</param>
public sealed record DfHistoryEntry(DateTimeOffset Time, DfEventKind Kind, ProtectionState State, string Title, string Detail, bool Failed = false);

/// <summary>A boot-state change Deep Freeze recorded after the latest boot, so it applies at the next restart.</summary>
/// <param name="RestartRequested">True when the request was one that also restarts the PC.</param>
public sealed record PendingBoot(ProtectionState Target, DateTimeOffset RequestedAt, string? Source, bool RestartRequested);

/// <summary>How long the PC has been Thawed, from the run of Thawed boots Deep Freeze recorded.</summary>
/// <param name="Boots">Thawed boots in the run, including the one that thawed it.</param>
/// <param name="AtLeast">True when the latest boot has no Deep Freeze record, so the run may be longer.</param>
public sealed record ThawedRun(DateTimeOffset Since, int Boots, bool AtLeast);

/// <summary>
/// Reads Deep Freeze's history from the events it writes to the Application log (source "Deep Freeze",
/// event 725). The wording parsed here was seen on Deep Freeze 9.00.520.5760:
/// "Deep Freeze Status = Thawed" at each boot, and "Deep Freeze status changed to Frozen on Next Boot
/// using the Command Line." for each request. Messages in any other wording are kept as they are.
/// </summary>
public static partial class DeepFreezeHistory
{
    public const string Provider = "Deep Freeze";
    public const int StatusEventId = 725;

    [GeneratedRegex(@"\ADeep Freeze Status\s*=\s*(?<state>\w+)\s*\.?\z", RegexOptions.IgnoreCase)]
    private static partial Regex BootStatusPattern();

    [GeneratedRegex(@"\ADeep Freeze status changed to (?<state>.+?)(?<next> on Next Boot)? using the (?<source>.+?)\.?\z", RegexOptions.IgnoreCase)]
    private static partial Regex ChangePattern();

    [GeneratedRegex(@"\A(?<process>.+?)\s*\([^()]*\)\s*\z")]
    private static partial Regex ProcessPattern();

    public static DfHistoryEntry Classify(DfEventRecord record)
    {
        var message = Normalize(record.Message);
        if (record.Provider.Equals(Provider, StringComparison.OrdinalIgnoreCase))
        {
            if (BootStatusPattern().Match(message) is { Success: true } status)
            {
                var state = StateOf(status.Groups["state"].Value);
                if (state != ProtectionState.Unknown)
                    return new(record.Time, DfEventKind.BootStatus, state, $"Booted {state}", "Recorded by Deep Freeze at startup");
            }
            if (ChangePattern().Match(message) is { Success: true } change)
            {
                var state = StateOf(change.Groups["state"].Value);
                var source = change.Groups["source"].Value.Trim();
                if (state != ProtectionState.Unknown)
                {
                    var nextBoot = change.Groups["next"].Success;
                    return new(record.Time, nextBoot ? DfEventKind.NextBootRequest : DfEventKind.ImmediateChange, state,
                        nextBoot ? $"{state} at next restart" : $"{state}, restart requested",
                        $"Requested from the {source}");
                }
            }
            return new(record.Time, DfEventKind.Other, ProtectionState.Unknown, "Deep Freeze event", message.Length > 0 ? message : $"Event {record.Id}");
        }
        if (record.Id == 1074)
        {
            var initiator = Initiator(record.Data is { Count: > 0 } data ? data[0] : null);
            var type = record.Data is { Count: > 4 } fields ? fields[4].Trim() : "";
            var title = type.Equals("power off", StringComparison.OrdinalIgnoreCase) ? "Shutdown requested"
                : type.Equals("restart", StringComparison.OrdinalIgnoreCase) ? "Restart requested" : "Restart or shutdown requested";
            return new(record.Time, DfEventKind.Restart, ProtectionState.Unknown, title, initiator);
        }
        if (record.Id == 6008)
            return new(record.Time, DfEventKind.UnexpectedShutdown, ProtectionState.Unknown, "Unexpected shutdown", "Windows didn't shut down cleanly before this point", Failed: true);
        return new(record.Time, DfEventKind.Other, ProtectionState.Unknown, $"{record.Provider} event {record.Id}", message);
    }

    /// <summary>Who asked Windows to restart, from event 1074's first data field, "path (computer)".</summary>
    public static string Initiator(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return "By an unknown process";
        var match = ProcessPattern().Match(field.Trim());
        var path = match.Success ? match.Groups["process"].Value : field.Trim();
        var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.ToLowerInvariant() switch
        {
            "runtimebroker.exe" or "explorer.exe" or "startmenuexperiencehost.exe" or "winlogon.exe" => $"From Windows ({name})",
            "dfserv.exe" or "frzstate2k.exe" or "dfc.exe" => $"By Deep Freeze ({name})",
            "fwaservice.exe" or "fwa_ui_agent.exe" => $"By the Faronics Cloud agent ({name})",
            "trustedinstaller.exe" or "musnotification.exe" or "mousocoreworker.exe" or "usoclient.exe" or "wuauclt.exe" => $"By Windows Update ({name})",
            "" => "By an unknown process",
            _ => $"By {name}"
        };
    }

    /// <summary>
    /// The latest boot-state request made after the PC last started, or null when there is none.
    /// A request made before the last boot has already been applied by that boot.
    /// </summary>
    /// <param name="lastBoot">When Windows last started; covers a boot Deep Freeze didn't record.</param>
    public static PendingBoot? Pending(IEnumerable<DfHistoryEntry> entries, DateTimeOffset? lastBoot)
    {
        var ordered = entries.OrderBy(entry => entry.Time).ToList();
        var bootedAt = ordered.LastOrDefault(entry => entry.Kind == DfEventKind.BootStatus)?.Time;
        var since = Latest(bootedAt, lastBoot);
        var request = ordered.LastOrDefault(entry =>
            entry.Kind is DfEventKind.NextBootRequest or DfEventKind.ImmediateChange && (since is null || entry.Time > since));
        if (request is null) return null;
        var source = request.Detail.StartsWith("Requested from the ", StringComparison.Ordinal) ? request.Detail["Requested from the ".Length..] : null;
        return new(request.State, request.Time, source, request.Kind == DfEventKind.ImmediateChange);
    }

    /// <summary>
    /// When the current Thawed run began: the earliest of the consecutive Thawed boots ending with the
    /// latest one. Null unless the PC is Thawed now and Deep Freeze recorded at least one Thawed boot.
    /// </summary>
    public static ThawedRun? Thawed(IEnumerable<DfHistoryEntry> entries, ProtectionState current, DateTimeOffset? lastBoot)
    {
        if (current != ProtectionState.Thawed) return null;
        var boots = entries.Where(entry => entry.Kind == DfEventKind.BootStatus).OrderByDescending(entry => entry.Time).ToList();
        if (boots.Count == 0 || boots[0].State != ProtectionState.Thawed)
            return lastBoot is { } bootTime && (boots.Count == 0 || boots[0].Time < bootTime) ? new(bootTime, 1, AtLeast: true) : null;
        var since = boots[0].Time;
        var count = 0;
        foreach (var boot in boots)
        {
            if (boot.State != ProtectionState.Thawed) break;
            since = boot.Time;
            count++;
        }
        // Deep Freeze records its status shortly after Windows starts. A latest record from before the
        // last start means this boot wasn't recorded, so the run is at least what the records show.
        var missedBoot = lastBoot is { } started && boots[0].Time < started - TimeSpan.FromMinutes(2);
        return new(since, count + (missedBoot ? 1 : 0), missedBoot);
    }

    /// <summary>All entries, newest first, capped to <paramref name="limit"/>.</summary>
    public static IReadOnlyList<DfHistoryEntry> Timeline(IEnumerable<DfHistoryEntry> entries, int limit = 200) =>
        entries.OrderByDescending(entry => entry.Time).ThenBy(entry => entry.Kind).Take(Math.Max(0, limit)).ToList();

    private static ProtectionState StateOf(string text)
    {
        var value = text.Trim();
        if (value.StartsWith("Frozen", StringComparison.OrdinalIgnoreCase)) return ProtectionState.Frozen;
        if (value.StartsWith("Thawed", StringComparison.OrdinalIgnoreCase)) return ProtectionState.Thawed;
        return ProtectionState.Unknown;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private static string Normalize(string? text)
    {
        var value = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return value.Length > 400 ? value[..400] + "…" : value;
    }
}
