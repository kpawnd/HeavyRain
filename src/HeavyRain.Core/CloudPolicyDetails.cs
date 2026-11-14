using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace HeavyRain.Core;

public enum ScheduleKind { Restart, Shutdown, Maintenance, BatchFile, WindowsUpdate, Other }

/// <summary>A recurring Deep Freeze event from the policy: when it runs and what it does.</summary>
/// <param name="Name">The policy's own name for it, such as "BatchFile#3", or the weekday schedule it came from.</param>
/// <param name="Stop">When a maintenance-style event ends; null for a restart or shutdown.</param>
/// <param name="LeadSeconds">How long Deep Freeze warns before it starts, when the policy says.</param>
public sealed record DfSchedule(ScheduleKind Kind, string Name, IReadOnlyList<DayOfWeek> Days, TimeSpan Start, TimeSpan? Stop,
    bool DisableInput, int? LeadSeconds)
{
    /// <summary>Maintenance, batch-file and Windows Update events restart the PC Thawed to make their changes.</summary>
    public bool RestartsThawed => Kind is ScheduleKind.Maintenance or ScheduleKind.BatchFile or ScheduleKind.WindowsUpdate;

    /// <summary>The next start at or after <paramref name="now"/>, in the same local time, or null without days.</summary>
    public DateTime? NextStart(DateTime now)
    {
        for (var day = 0; day <= 7; day++)
        {
            var date = now.Date.AddDays(day);
            if (!Days.Contains(date.DayOfWeek)) continue;
            var start = date + Start;
            if (start >= now) return start;
        }
        return null;
    }

    /// <summary>True when <paramref name="now"/> falls inside today's window of a maintenance-style event.</summary>
    public bool IsRunning(DateTime now) =>
        Stop is { } stop && Days.Contains(now.DayOfWeek) && now.TimeOfDay >= Start && now.TimeOfDay < stop;
}

/// <summary>A user or group listed in the Anti-Executable part of the policy.</summary>
public sealed record AePolicyUser(string Name, bool IsGroup);

public enum AeMembership { NotListed, TrustedUser, Administrator }

/// <param name="CheckInMinutes">How often the Cloud agent asks for policy changes.</param>
/// <param name="CloudMaintenance">The Cloud agent's own maintenance window, when enabled, as the policy writes it ("12:00 AM").</param>
/// <param name="PersistEventLogs">Whether Deep Freeze keeps the Windows event logs across Frozen restarts.</param>
/// <param name="LastModified">When the policy was last changed in the Cloud console.</param>
public sealed record CloudPolicyDetails(
    int? CheckInMinutes,
    (string Start, string End)? CloudMaintenance,
    IReadOnlyList<DfSchedule> Schedules,
    IReadOnlyList<AePolicyUser> AeAdministrators,
    IReadOnlyList<AePolicyUser> AeTrustedUsers,
    bool? PersistEventLogs,
    bool? LicenseExpired,
    DateTimeOffset? LastModified)
{
    public static CloudPolicyDetails Empty { get; } = new(null, null, [], [], [], null, null, null);

    public bool HasAeLists => AeAdministrators.Count > 0 || AeTrustedUsers.Count > 0;

    /// <summary>The schedules in the order they next run.</summary>
    public IReadOnlyList<(DfSchedule Schedule, DateTime? Next)> Upcoming(DateTime now) =>
        Schedules.Select(schedule => (schedule, schedule.NextStart(now))).OrderBy(item => item.Item2 ?? DateTime.MaxValue).ToList();

    /// <summary>
    /// Where the current Windows account appears in the policy's Anti-Executable lists. Named
    /// entries match the account's bare name or DOMAIN\name; group entries are checked with
    /// <paramref name="isInGroup"/>. Administrator wins over trusted user.
    /// </summary>
    public (AeMembership Membership, AePolicyUser? Entry) AeMembershipOf(string userName, string? qualifiedName, Func<string, bool> isInGroup)
    {
        bool Matches(AePolicyUser user)
        {
            if (user.IsGroup) { try { return isInGroup(user.Name); } catch (Exception ex) when (ex is not OutOfMemoryException) { return false; } }
            return user.Name.Contains('\\')
                ? qualifiedName is not null && user.Name.Equals(qualifiedName, StringComparison.OrdinalIgnoreCase)
                : user.Name.Equals(userName, StringComparison.OrdinalIgnoreCase);
        }
        if (AeAdministrators.FirstOrDefault(Matches) is { } admin) return (AeMembership.Administrator, admin);
        if (AeTrustedUsers.FirstOrDefault(Matches) is { } trusted) return (AeMembership.TrustedUser, trusted);
        return (AeMembership.NotListed, null);
    }
}

/// <summary>
/// Pulls a handful of non-secret settings out of the cached Cloud policy XML. Only the elements
/// named here are read: the Cloud agent's check-in and maintenance settings, Deep Freeze's
/// schedules and two of its flags, and Anti-Executable's user lists. Password, licence and OTP
/// sections are never visited, and nothing else from the XML is kept.
/// </summary>
public static class CloudPolicyParser
{
    public static CloudPolicyDetails? Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > CloudPolicyCache.MaximumXmlLength) return null;
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true,
                MaxCharactersInDocument = CloudPolicyCache.MaximumXmlLength
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException) { return null; }
        var root = document.Root;
        if (root is null || root.Name.LocalName != "Policy") return null;

        XElement? Product(string code) => root.Elements("Product").FirstOrDefault(p => (string?)p.Attribute("ProductCode") == code);
        var agent = Product("FWA");
        var general = agent?.Descendants("GeneralSettings").FirstOrDefault();
        var checkIn = int.TryParse((string?)general?.Attribute("PolicyUpdate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0 ? minutes : (int?)null;
        var maintenance = agent?.Descendants("MaintenanceSettings").FirstOrDefault();
        (string, string)? cloudWindow = IsTrue((string?)maintenance?.Attribute("Enabled"))
            ? ((string?)maintenance!.Attribute("StartTime") ?? "?", (string?)maintenance.Attribute("EndTime") ?? "?") : null;

        var deepFreeze = Product("DFE");
        var schedules = deepFreeze is null ? [] : DeepFreezeSchedules(deepFreeze);
        var basic = deepFreeze?.Descendants("BasicData").FirstOrDefault();
        bool? Flag(string name) => basic?.Element(name)?.Attribute("Value")?.Value is { } value ? value.Trim() != "0" : null;

        var antiExecutable = Product("AEE");
        IReadOnlyList<AePolicyUser> Users(string list) => antiExecutable?.Descendants(list).FirstOrDefault()?.Elements("USER")
            .Select(user => new AePolicyUser(user.Value.Trim(), (string?)user.Attribute("group") == "1"))
            .Where(user => user.Name.Length is > 0 and <= 256).ToList() ?? [];

        DateTimeOffset? modified = DateTimeOffset.TryParse((string?)root.Attribute("LastModifiedTime"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var stamp) ? stamp : null;
        return new(checkIn, cloudWindow, schedules, Users("AEADMINS"), Users("AETRUSTEDUSERS"), Flag("PersistEventLogs"), Flag("LicenseExpired"), modified);
    }

    private static List<DfSchedule> DeepFreezeSchedules(XElement deepFreeze)
    {
        var result = new List<DfSchedule>();
        // Workstation tasks: active entries only.
        foreach (var task in deepFreeze.Descendants("AmalSched").Elements("Schedule"))
        {
            if (!IsYes((string?)task.Attribute("Active"))) continue;
            var name = ((string?)task.Attribute("EventName"))?.Trim() is { Length: > 0 } eventName ? eventName : "Workstation task";
            var start = ParseTime((string?)task.Attribute("TimeStart"));
            if (start is null) continue;
            var kind = KindFromName(name);
            var stop = ParseTime((string?)task.Attribute("TimeStop"));
            var lead = int.TryParse((string?)task.Attribute("LeadSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? seconds : (int?)null;
            result.Add(new(kind, name, DaysFromMask((string?)task.Attribute("EventDays")), start.Value,
                kind is ScheduleKind.Restart or ScheduleKind.Shutdown || stop == start ? null : stop,
                IsYes((string?)task.Attribute("DisableInput")), lead));
        }
        // The older weekday schedule: a restart or shutdown per day, and maintenance windows per day.
        var weekly = deepFreeze.Descendants("Schedule").FirstOrDefault(s => s.Element("RestartShutdown") is not null || s.Element("Maintenance") is not null);
        if (weekly is not null)
        {
            if (IsYes((string?)weekly.Attribute("EnableRestartShutdownSchedule")))
                foreach (var day in weekly.Element("RestartShutdown")?.Elements() ?? [])
                    if (IsYes((string?)day.Attribute("Enabled")) && Enum.TryParse<DayOfWeek>(day.Name.LocalName, out var dayOfWeek) && ParseTime((string?)day.Attribute("Time")) is { } time)
                    {
                        var shutdown = IsYes((string?)day.Attribute("Shutdown"));
                        result.Add(new(shutdown ? ScheduleKind.Shutdown : ScheduleKind.Restart, $"{(shutdown ? "Shutdown" : "Restart")} schedule", [dayOfWeek], time, null, false, null));
                    }
            if (IsYes((string?)weekly.Attribute("EnableMaintenance")))
                foreach (var day in weekly.Element("Maintenance")?.Elements() ?? [])
                    if (IsYes((string?)day.Attribute("Enabled")) && Enum.TryParse<DayOfWeek>(day.Name.LocalName, out var dayOfWeek) && ParseTime((string?)day.Attribute("StartTime")) is { } start)
                    {
                        var kind = IsYes((string?)day.Attribute("RunWindowsUpdate")) ? ScheduleKind.WindowsUpdate
                            : IsYes((string?)day.Attribute("RunBatchFile")) ? ScheduleKind.BatchFile : ScheduleKind.Maintenance;
                        result.Add(new(kind, "Maintenance schedule", [dayOfWeek], start, ParseTime((string?)day.Attribute("StopTime")),
                            IsYes((string?)day.Attribute("InputDisabled")), null));
                    }
        }
        return result;
    }

    /// <summary>
    /// Days from a workstation task's <c>EventDays</c> bit mask, read as bit 0 = Sunday through
    /// bit 6 = Saturday, Windows' own day order. This mapping hasn't been confirmed against the Cloud
    /// console yet; it is kept in this one place so it can be corrected.
    /// </summary>
    public static IReadOnlyList<DayOfWeek> DaysFromMask(string? mask)
    {
        if (!int.TryParse(mask, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) || bits <= 0) return [];
        return Enumerable.Range(0, 7).Where(day => (bits & (1 << day)) != 0).Select(day => (DayOfWeek)day).ToList();
    }

    /// <summary>A time of day from "dd:MM:yyyy:HH:mm:ss" (workstation tasks) or "H:m:s" (weekday schedules).</summary>
    public static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length < 3) return null;
        var tail = parts[^3..];
        if (!int.TryParse(tail[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(tail[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(tail[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        if (hours > 23 || minutes > 59 || seconds > 59) return null;
        return new TimeSpan(hours, minutes, seconds);
    }

    public static ScheduleKind KindFromName(string name)
    {
        var prefix = name.Split('#')[0].Replace(" ", "").ToLowerInvariant();
        return prefix switch
        {
            "restart" or "reboot" => ScheduleKind.Restart,
            "shutdown" => ScheduleKind.Shutdown,
            "batchfile" => ScheduleKind.BatchFile,
            "windowsupdate" or "wu" => ScheduleKind.WindowsUpdate,
            "maintenance" => ScheduleKind.Maintenance,
            _ => ScheduleKind.Other
        };
    }

    public static string Describe(ScheduleKind kind) => kind switch
    {
        ScheduleKind.BatchFile => "Batch file",
        ScheduleKind.WindowsUpdate => "Windows Update",
        _ => kind.ToString()
    };

    public static string DescribeDays(IReadOnlyList<DayOfWeek> days)
    {
        if (days.Count == 7) return "Every day";
        if (days.Count == 5 && !days.Contains(DayOfWeek.Saturday) && !days.Contains(DayOfWeek.Sunday)) return "Weekdays";
        if (days.Count == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday)) return "Weekends";
        // Monday first, the way schedules are usually read.
        return string.Join(", ", days.OrderBy(day => ((int)day + 6) % 7).Select(day => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day)));
    }

    private static bool IsYes(string? value) => value?.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsTrue(string? value) => value?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
}
