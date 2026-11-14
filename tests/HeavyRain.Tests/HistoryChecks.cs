using System.Text.Json;
using HeavyRain.Core;

/// <summary>
/// Deep Freeze history, Cloud tasks, cached policy details, licence and component checks. Event
/// wording, cache rows and policy shapes are copied from the Windows 11 test VM (Deep Freeze
/// 9.00.520.5760, Cloud agent 3.10.2100.892); secrets are replaced with markers that must never
/// appear in any parsed result.
/// </summary>
internal static class HistoryChecks
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-8);
    private static DateTimeOffset At(int day, int hour, int minute, int second = 0) => new(2026, 11, day, hour, minute, second, Offset);
    private static DfEventRecord Df(DateTimeOffset time, string message) => new(time, "Deep Freeze", 725, message, [message]);
    private static DfEventRecord Restart(DateTimeOffset time, string process, string type = "restart") =>
        new(time, "User32", 1074, "localized text", [$@"{process} (WINDEV2407EVAL)", "WINDEV2407EVAL", "No title for this reason could be found", "0x80020010", type, "", @"WINDEV2407EVAL\User"]);

    // The sequence logged on the VM on 13 November, in order.
    private static readonly DfEventRecord[] VmDay =
    [
        Df(At(13, 18, 3, 18), "Deep Freeze Status = Frozen"),
        Df(At(13, 18, 6, 47), "Deep Freeze status changed to Thawed on Next Boot using the Command Line."),
        Restart(At(13, 18, 7, 37), @"C:\Windows\System32\RuntimeBroker.exe"),
        Df(At(13, 18, 7, 59), "Deep Freeze Status = Thawed"),
        Restart(At(13, 18, 8, 58), @"C:\Windows\System32\RuntimeBroker.exe"),
        Df(At(13, 18, 9, 19), "Deep Freeze Status = Thawed"),
        Restart(At(13, 18, 10, 4), @"C:\Windows\System32\RuntimeBroker.exe"),
        Df(At(13, 18, 10, 20), "Deep Freeze Status = Thawed"),
        Df(At(13, 18, 24, 39), "Deep Freeze status changed to Frozen on Next Boot using the Command Line."),
        Restart(At(13, 18, 24, 39), @"C:\Windows\system32\shutdown.exe"),
        Df(At(13, 18, 25, 0), "Deep Freeze Status = Frozen"),
        Df(At(13, 18, 27, 3), "Deep Freeze status changed to Thawed on Next Boot using the Command Line."),
        Restart(At(13, 18, 27, 3), @"C:\Windows\system32\shutdown.exe"),
        Df(At(13, 18, 27, 28), "Deep Freeze Status = Thawed"),
        Df(At(13, 18, 28, 22), "Deep Freeze status changed to Frozen on Next Boot using the Command Line.")
    ];

    private static List<DfHistoryEntry> Entries(IEnumerable<DfEventRecord> records) => records.Select(DeepFreezeHistory.Classify).ToList();

    private const string Secret = "SECRET-MARKER-7f3a";

    // A trimmed policy in the cached shape, with every secret-bearing section filled with the marker.
    private static string PolicyXml(string amalSched = "", string weekly = "", string fwaMaintenance = "Enabled=\"false\" StartTime=\"12:00 AM\" EndTime=\"01:00 AM\"",
        string basic = "<PersistEventLogs Value=\"1\"/><LicenseExpired Value=\"0\"/>") => $"""
        <Policy xmlns:xsd="http://www.w3.org/2001/XMLSchema" Id="123" Name="DEPLOYMENT" LastModifiedTime="2026-11-13T08:12:12.317">
          <Product ProductCode="FWA" Enabled="true">
            <AgentSettings>
              <GeneralSettings PolicyUpdate="240" EnablePassword="1" Password="{Secret}" EncryptedPassword="{Secret}">
                <Proxy Enabled="false" Pwd="{Secret}"/>
              </GeneralSettings>
              <MaintenanceSettings {fwaMaintenance} WeekDays="0"/>
            </AgentSettings>
          </Product>
          <Product ProductCode="AEE" Enabled="true">
            <ANTIEXECUTABLECONFIG eType="1">
              <CPASSWORDS enc="3"><CAEADMIN enable="1">{Secret}</CAEADMIN><CAETUSER enable="1">{Secret}</CAETUSER></CPASSWORDS>
              <AETRUSTEDUSERS><USER group="0">labtech</USER></AETRUSTEDUSERS>
              <AEADMINS>
                <USER group="0">localadmin</USER>
                <USER group="0">User</USER>
                <USER group="1">TECHLAB\Technical Assistants Group</USER>
              </AEADMINS>
            </ANTIEXECUTABLECONFIG>
          </Product>
          <Product ProductCode="DFE" Enabled="true">
            <custicode tag="210053561">
              <Config>
                <Passwords Size="1" AdminOTPKey="{Secret}">
                  <PasswordData><Passwords ID="1" UserRights="3" Password="{Secret}"/></PasswordData>
                </Passwords>
                {weekly}
                <BasicData><HotKey Value="{Secret}"/>{basic}</BasicData>
                <License LicenseKey="{Secret}" ExpiryDate="{Secret}"/>
                <AmalSched>{amalSched}</AmalSched>
              </Config>
            </custicode>
            <BatchFile BatchFile="@echo off set password={Secret}"/>
          </Product>
        </Policy>
        """;

    private const string VmAmalSched = """
        <Schedule Active="N" EventName="Shutdown#1" EventType="2" DisableInput="N" TimeStart="31:12:1899:21:30:00" TimeStop="31:12:1899:21:30:00" EventDays="10" LeadSeconds="180" HashedID="SECRET-MARKER-7f3a"/>
        <Schedule Active="N" EventName="BatchFile#1" EventType="3" DisableInput="N" TimeStart="31:12:1899:17:35:00" TimeStop="31:12:1899:17:50:00" EventDays="4" LeadSeconds="300"/>
        <Schedule Active="Y" EventName="BatchFile#3" EventType="3" DisableInput="Y" TimeStart="31:12:1899:11:11:00" TimeStop="31:12:1899:11:26:00" EventDays="10" LeadSeconds="60" HashedID="SECRET-MARKER-7f3a"/>
        """;

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        // ---------------------------------------------------------------- classifying events
        await check("History: boot status events become Booted Frozen and Booted Thawed", () =>
        {
            var frozen = DeepFreezeHistory.Classify(Df(At(13, 18, 3), "Deep Freeze Status = Frozen"));
            var thawed = DeepFreezeHistory.Classify(Df(At(13, 18, 7), "Deep Freeze Status = Thawed"));
            Assert(frozen is { Kind: DfEventKind.BootStatus, State: ProtectionState.Frozen, Title: "Booted Frozen" }, "Frozen boot misread");
            Assert(thawed is { Kind: DfEventKind.BootStatus, State: ProtectionState.Thawed, Title: "Booted Thawed" }, "Thawed boot misread");
            return Task.CompletedTask;
        });

        await check("History: next-boot and immediate requests keep their target and source", () =>
        {
            var next = DeepFreezeHistory.Classify(Df(At(13, 18, 28), "Deep Freeze status changed to Frozen on Next Boot using the Command Line."));
            var now = DeepFreezeHistory.Classify(Df(At(13, 17, 58), "Deep Freeze status changed to Thawed using the Command Line."));
            Assert(next is { Kind: DfEventKind.NextBootRequest, State: ProtectionState.Frozen, Title: "Frozen at next restart", Detail: "Requested from the Command Line" }, "Next-boot request misread: " + next);
            Assert(now is { Kind: DfEventKind.ImmediateChange, State: ProtectionState.Thawed, Title: "Thawed, restart requested" }, "Immediate change misread: " + now);
            var other = DeepFreezeHistory.Classify(Df(At(13, 1, 0), "Deep Freeze status changed to Thawed Locked using the Deep Freeze Console"));
            Assert(other is { Kind: DfEventKind.ImmediateChange, State: ProtectionState.Thawed, Detail: "Requested from the Deep Freeze Console" }, "Variant wording lost");
            return Task.CompletedTask;
        });

        await check("History: unrecognized Deep Freeze wording is kept, tidied and capped, never guessed", () =>
        {
            var odd = DeepFreezeHistory.Classify(Df(At(13, 1, 0), "  Deep Freeze   status changed to Lunar using the Moon  "));
            Assert(odd is { Kind: DfEventKind.Other, State: ProtectionState.Unknown } && odd.Detail == "Deep Freeze status changed to Lunar using the Moon", "Odd wording guessed: " + odd);
            var german = DeepFreezeHistory.Classify(Df(At(13, 1, 0), "Deep Freeze-Status = Eingefroren"));
            Assert(german.Kind == DfEventKind.Other && german.State == ProtectionState.Unknown, "Localized wording guessed");
            var huge = DeepFreezeHistory.Classify(Df(At(13, 1, 0), new string('x', 2000)));
            Assert(huge.Detail.Length == 401 && huge.Detail.EndsWith('…'), "Long message not capped");
            var empty = DeepFreezeHistory.Classify(new DfEventRecord(At(13, 1, 0), "Deep Freeze", 999, ""));
            Assert(empty.Detail == "Event 999", "Empty message not labelled");
            return Task.CompletedTask;
        });

        foreach (var (process, expected) in new[]
        {
            (@"C:\Windows\system32\shutdown.exe", "By shutdown.exe"),
            (@"C:\Windows\System32\RuntimeBroker.exe", "From Windows (RuntimeBroker.exe)"),
            (@"C:\Program Files (x86)\Faronics\Deep Freeze\Install C-0\DFServ.exe", "By Deep Freeze (DFServ.exe)"),
            (@"C:\Program Files (x86)\Faronics\Faronics Cloud\Faronics Cloud Agent\FWAService.exe", "By the Faronics Cloud agent (FWAService.exe)"),
            (@"C:\Windows\servicing\TrustedInstaller.exe", "By Windows Update (TrustedInstaller.exe)")
        })
        await check($"History: restart initiator {expected}", () =>
        {
            var entry = DeepFreezeHistory.Classify(Restart(At(13, 18, 0), process));
            Assert(entry is { Kind: DfEventKind.Restart, Title: "Restart requested" } && entry.Detail == expected, $"Got {entry.Detail}");
            return Task.CompletedTask;
        });

        await check("History: restart type and odd 1074 data are handled", () =>
        {
            Assert(DeepFreezeHistory.Classify(Restart(At(13, 18, 0), "shutdown.exe", "power off")).Title == "Shutdown requested", "Power off missed");
            Assert(DeepFreezeHistory.Classify(Restart(At(13, 18, 0), "shutdown.exe", "Neustart")).Title == "Restart or shutdown requested", "Localized type guessed");
            Assert(DeepFreezeHistory.Initiator(null) == "By an unknown process" && DeepFreezeHistory.Initiator("  ") == "By an unknown process", "Missing initiator");
            Assert(DeepFreezeHistory.Initiator(@"C:\Tools\reboot.exe") == "By reboot.exe", "Path without computer name");
            var bare = DeepFreezeHistory.Classify(new DfEventRecord(At(13, 18, 0), "User32", 1074, "x"));
            Assert(bare.Detail == "By an unknown process" && bare.Title == "Restart or shutdown requested", "Event without data");
            var crash = DeepFreezeHistory.Classify(new DfEventRecord(At(13, 18, 0), "EventLog", 6008, "localized"));
            Assert(crash is { Kind: DfEventKind.UnexpectedShutdown, Failed: true }, "Unexpected shutdown missed");
            return Task.CompletedTask;
        });

        // ---------------------------------------------------------------- pending request
        await check("Pending: the VM's 18:28 Frozen request after the 18:27 Thawed boot is pending", () =>
        {
            var pending = DeepFreezeHistory.Pending(Entries(VmDay), At(13, 18, 27, 11));
            Assert(pending is { Target: ProtectionState.Frozen, Source: "Command Line", RestartRequested: false } && pending.RequestedAt == At(13, 18, 28, 22), "Wrong pending: " + pending);
            return Task.CompletedTask;
        });

        await check("Pending: a request already applied by a later boot isn't pending", () =>
        {
            var applied = Entries(VmDay.Take(VmDay.Length - 1));
            Assert(DeepFreezeHistory.Pending(applied, At(13, 18, 27, 11)) is null, "Applied request still pending");
            Assert(DeepFreezeHistory.Pending([], null) is null, "Empty history pending");
            return Task.CompletedTask;
        });

        await check("Pending: the latest of several requests wins, whatever order events arrive in", () =>
        {
            var events = Entries([
                Df(At(13, 9, 0), "Deep Freeze Status = Frozen"),
                Df(At(13, 9, 5), "Deep Freeze status changed to Thawed on Next Boot using the Command Line."),
                Df(At(13, 9, 6), "Deep Freeze status changed to Frozen on Next Boot using the Command Line.")]);
            events.Reverse();
            var pending = DeepFreezeHistory.Pending(events, At(13, 8, 59, 40));
            Assert(pending?.Target == ProtectionState.Frozen && pending.RequestedAt == At(13, 9, 6), "Latest request lost");
            return Task.CompletedTask;
        });

        await check("Pending: without a boot record, Windows' start time separates old requests from new", () =>
        {
            var events = Entries([
                Df(At(13, 9, 5), "Deep Freeze status changed to Thawed on Next Boot using the Command Line."),
                Df(At(13, 11, 0), "Deep Freeze status changed to Frozen using the Command Line.")]);
            Assert(DeepFreezeHistory.Pending(events, At(13, 12, 0)) is null, "Request from before the boot pending");
            var pending = DeepFreezeHistory.Pending(events, At(13, 10, 0));
            Assert(pending is { Target: ProtectionState.Frozen, RestartRequested: true }, "Immediate change after boot not pending (the hung /BOOTFROZEN case)");
            Assert(DeepFreezeHistory.Pending(events, null)?.RequestedAt == At(13, 11, 0), "No boot information at all");
            return Task.CompletedTask;
        });

        await check("Pending: a boot record newer than Windows' reported start still counts", () =>
        {
            var events = Entries([Df(At(13, 9, 0), "Deep Freeze status changed to Thawed on Next Boot using the Command Line."), Df(At(13, 9, 30), "Deep Freeze Status = Thawed")]);
            Assert(DeepFreezeHistory.Pending(events, At(13, 8, 0)) is null, "Older start time revived an applied request");
            return Task.CompletedTask;
        });

        // ---------------------------------------------------------------- thawed run
        await check("Thawed run: the VM's three Thawed boots after the 18:06 thaw", () =>
        {
            var upTo1810 = Entries(VmDay.Take(8));
            var run = DeepFreezeHistory.Thawed(upTo1810, ProtectionState.Thawed, At(13, 18, 10, 11));
            Assert(run is { Boots: 3, AtLeast: false } && run.Since == At(13, 18, 7, 59), "Wrong run: " + run);
            var latest = DeepFreezeHistory.Thawed(Entries(VmDay), ProtectionState.Thawed, At(13, 18, 27, 11));
            Assert(latest is { Boots: 1 } && latest.Since == At(13, 18, 27, 28), "Run after a Frozen boot: " + latest);
            return Task.CompletedTask;
        });

        await check("Thawed run: none while Frozen or Unknown", () =>
        {
            Assert(DeepFreezeHistory.Thawed(Entries(VmDay), ProtectionState.Frozen, At(13, 18, 27, 11)) is null, "Frozen PC has a run");
            Assert(DeepFreezeHistory.Thawed(Entries(VmDay), ProtectionState.Unknown, At(13, 18, 27, 11)) is null, "Unknown state has a run");
            return Task.CompletedTask;
        });

        await check("Thawed run: an unrecorded boot makes it 'at least'", () =>
        {
            var events = Entries(VmDay.Take(8));
            var run = DeepFreezeHistory.Thawed(events, ProtectionState.Thawed, At(13, 20, 0));
            Assert(run is { Boots: 4, AtLeast: true } && run.Since == At(13, 18, 7, 59), "Missed boot: " + run);
            var none = DeepFreezeHistory.Thawed([], ProtectionState.Thawed, At(13, 20, 0));
            Assert(none is { Boots: 1, AtLeast: true } && none.Since == At(13, 20, 0), "No records: " + none);
            var contradicted = DeepFreezeHistory.Thawed(Entries([Df(At(13, 20, 0, 20), "Deep Freeze Status = Frozen")]), ProtectionState.Thawed, At(13, 20, 0));
            Assert(contradicted is null, "Frozen record for this boot ignored");
            var stale = DeepFreezeHistory.Thawed(Entries([Df(At(13, 9, 0), "Deep Freeze Status = Frozen")]), ProtectionState.Thawed, At(13, 20, 0));
            Assert(stale is { AtLeast: true, Boots: 1 } && stale.Since == At(13, 20, 0), "Stale Frozen record: " + stale);
            Assert(DeepFreezeHistory.Thawed([], ProtectionState.Thawed, null) is null, "Run invented without data");
            return Task.CompletedTask;
        });

        await check("Timeline: newest first, capped, and stable for equal times", () =>
        {
            var timeline = DeepFreezeHistory.Timeline(Entries(VmDay), 5);
            Assert(timeline.Count == 5 && timeline[0].Time == At(13, 18, 28, 22), "Order or cap wrong");
            Assert(timeline.Zip(timeline.Skip(1)).All(pair => pair.First.Time >= pair.Second.Time), "Not sorted");
            var tie = DeepFreezeHistory.Timeline(Entries(VmDay));
            var at1824 = tie.Where(entry => entry.Time == At(13, 18, 24, 39)).ToList();
            Assert(at1824.Count == 2 && at1824[0].Kind == DfEventKind.NextBootRequest, "Equal times unstable");
            Assert(DeepFreezeHistory.Timeline(Entries(VmDay), -3).Count == 0, "Negative limit");
            return Task.CompletedTask;
        });

        // ---------------------------------------------------------------- Cloud tasks
        await check("Cloud tasks: rows from the VM cache become tasks with local times", () =>
        {
            var task = CloudTaskHistory.FromRow(Row(("TaskName", "SetLicenses"), ("StartTime", "2026-11-12 01:17:54.140"), ("EndTime", "2026-11-12 01:17:54.391"),
                ("ErrorMsg", "Task Failed"), ("HrResult", "-2147467262"), ("IsInternalTask", "0")));
            Assert(task is { Name: "SetLicenses", HResult: -2147467262, Internal: false } && task.Started!.Value.DateTime == new DateTime(2026, 11, 12, 1, 17, 54, 140), "Row misread: " + task);
            Assert(CloudTaskHistory.IsFailure(task!), "Failure missed");
            Assert(CloudTaskHistory.FromRow(Row(("TaskName", "  "))) is null && CloudTaskHistory.FromRow(Row()) is null, "Nameless task kept");
            var fallback = CloudTaskHistory.FromRow(Row(("TaskName", "AssignPolicy"), ("TimeStamp", "2026-11-13 00:12:13.000000"), ("StatusUpdateTime", "2026-11-13 00:18:26.817")));
            Assert(fallback?.Started is not null && fallback.Ended is not null, "Fallback times missed");
            return Task.CompletedTask;
        });

        await check("Cloud tasks: bad or odd times are dropped, never guessed", () =>
        {
            Assert(CloudTaskHistory.ParseTime("13/11/2026 18:00") is null && CloudTaskHistory.ParseTime("") is null && CloudTaskHistory.ParseTime(null) is null, "Bad time parsed");
            Assert(CloudTaskHistory.ParseTime("2026-11-13 00:12:13.000000")?.DateTime == new DateTime(2026, 11, 13, 0, 12, 13), "Seven-digit fraction");
            return Task.CompletedTask;
        });

        await check("Cloud tasks: routine inventory hidden, console tasks and failures kept", () =>
        {
            CloudTask[] tasks =
            [
                new("UpdateInventory", At(13, 17, 40), At(13, 17, 40, 3), "Task Succeeded", 0, true),
                new("UpdateInventory", At(13, 16, 0), null, "Task Failed", 5, true),
                new("AssignPolicy", At(13, 0, 12, 14), At(13, 0, 18, 26), "Task Succeeded", 0, false),
                new("SetLicenses", At(12, 1, 17, 54), At(12, 1, 17, 54), "Task Failed", -2147467262, false),
                new("ChangeMode", At(11, 20, 38, 48), At(11, 20, 38, 48), "Task Succeeded", 0, false),
                new("AssignPolicy", At(12, 0, 3, 25), At(12, 1, 13, 53), "Forcefully Marked Completed On FWASvc startup", 0, false),
                new("Ghost", null, null, null, null, false)
            ];
            var history = CloudTaskHistory.ToHistory(tasks).ToList();
            Assert(history.Count == 5, $"Expected 5 entries, got {history.Count}");
            Assert(history.All(entry => entry.Kind == DfEventKind.CloudTask), "Wrong kind");
            Assert(history.Single(entry => entry.Title == "Licences updated") is { Failed: true } failed && failed.Detail.Contains("0x80004002"), "Failure code missing");
            Assert(history.Any(entry => entry.Title == "Cloud changed Deep Freeze's mode"), "ChangeMode missing");
            Assert(history.Any(entry => entry.Detail.Contains("Forcefully Marked Completed")), "Unusual result hidden");
            Assert(history.Any(entry => entry.Detail.Contains("took 6 min")), "Duration missing");
            Assert(history.Any(entry => entry.Failed && entry.Title == "Inventory sent to the Cloud"), "Failed internal task hidden");
            Assert(CloudTaskHistory.Title("SomethingNew") == "Cloud task: SomethingNew", "Unknown task name");
            return Task.CompletedTask;
        });

        await check("Cloud tasks: reading a real cache never selects task bodies or IDs", () =>
        {
            using var fixture = new PolicyCacheFixture();
            fixture.Execute("CREATE TABLE Tasks_V1 (TimeStamp TIMESTAMP, TaskId TEXT, TaskName TEXT, TaskState INT, ScheduledTime TEXT, SerializedTaskXml TEXT, IsInternalTask INT, StartTime TEXT, EndTime TEXT, StatusUpdateTime TEXT, ErrorMsg TEXT, HrResult INT, ExtraInfo TEXT)");
            fixture.Execute($"INSERT INTO Tasks_V1 VALUES ('2026-11-13 00:12:13', '{Secret}', 'AssignPolicy', 3, '', '<Task><Password>{Secret}</Password></Task>', 0, '2026-11-13 00:12:14.485', '2026-11-13 00:18:26.817', '2026-11-13 00:18:26.817', 'Task Succeeded', 0, '')");
            fixture.Execute("INSERT INTO Tasks_V1 VALUES ('2026-11-13 17:37:38', 'x', 'UpdateInventory', 3, '', '', 1, '2026-11-13 17:40:31.993', '2026-11-13 17:40:34.519', '', 'Task Succeeded', 0, 'Agent_Auto_Task')");
            var tasks = CloudTaskHistory.Read(fixture.Path);
            Assert(tasks.Count == 2 && tasks[0].Name == "UpdateInventory", "Newest-first order lost");
            Assert(!JsonSerializer.Serialize(tasks).Contains(Secret), "A task body or ID was read");
            Assert(!CloudTaskHistory.Query.Contains("SerializedTaskXml") && !CloudTaskHistory.Query.Contains("TaskId"), "Query selects secrets");
            return Task.CompletedTask;
        });

        await check("Cloud tasks: a missing cache or table is an error the caller sees, not empty success", () =>
        {
            var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
            Assert(Throws<WinSqlite.SqliteReadException>(() => CloudTaskHistory.Read(missing)) && !File.Exists(missing), "Missing cache created or ignored");
            using var fixture = new PolicyCacheFixture();
            Assert(Throws<WinSqlite.SqliteReadException>(() => CloudTaskHistory.Read(fixture.Path)), "Missing table ignored");
            return Task.CompletedTask;
        });

        // ---------------------------------------------------------------- policy details
        await check("Policy: the VM's settings come through and no secret does", () =>
        {
            var details = CloudPolicyParser.Parse(PolicyXml(VmAmalSched));
            Assert(details is not null, "Policy not parsed");
            Assert(details!.CheckInMinutes == 240 && details.CloudMaintenance is null, "Agent settings wrong");
            Assert(details.PersistEventLogs == true && details.LicenseExpired == false, "Deep Freeze flags wrong");
            Assert(details.LastModified == new DateTimeOffset(2026, 11, 13, 8, 12, 12, 317, TimeSpan.Zero), "Modified time wrong");
            Assert(!JsonSerializer.Serialize(details).Contains(Secret), "A secret reached the parsed details");
            return Task.CompletedTask;
        });

        await check("Policy: only the active workstation task is a schedule, with its window and warning", () =>
        {
            var schedule = CloudPolicyParser.Parse(PolicyXml(VmAmalSched))!.Schedules.Single();
            Assert(schedule is { Kind: ScheduleKind.BatchFile, Name: "BatchFile#3", DisableInput: true, LeadSeconds: 60, RestartsThawed: true }, "Task misread: " + schedule);
            Assert(schedule.Start == new TimeSpan(11, 11, 0) && schedule.Stop == new TimeSpan(11, 26, 0), "Window wrong");
            Assert(schedule.Days.SequenceEqual([DayOfWeek.Monday, DayOfWeek.Wednesday]), "Days from mask 10 wrong");
            return Task.CompletedTask;
        });

        await check("Policy: weekday schedules count only when their switch is on", () =>
        {
            const string weekly = """
                <Schedule EnableRestartShutdownSchedule="{0}" EnableMaintenance="{1}">
                  <RestartShutdown><Sunday Enabled="N" Shutdown="N" Time="0:0:0"/><Monday Enabled="Y" Shutdown="N" Time="6:30:0"/><Friday Enabled="Y" Shutdown="Y" Time="22:0:0"/></RestartShutdown>
                  <Maintenance><Tuesday Enabled="Y" StartTime="2:0:0" StopTime="4:0:0" InputDisabled="Y" RunWindowsUpdate="Y" RunBatchFile="N"/><Thursday Enabled="Y" StartTime="1:0:0" StopTime="1:30:0" InputDisabled="N" RunWindowsUpdate="N" RunBatchFile="N"/></Maintenance>
                  <Reserved Value1="0"/>
                </Schedule>
                """;
            var on = CloudPolicyParser.Parse(PolicyXml(weekly: weekly.Replace("{0}", "Y").Replace("{1}", "Y")))!.Schedules;
            Assert(on.Count == 4, $"Expected 4 weekday schedules, got {on.Count}");
            Assert(on.Any(s => s is { Kind: ScheduleKind.Restart, Stop: null } && s.Days.Single() == DayOfWeek.Monday && s.Start == new TimeSpan(6, 30, 0)), "Monday restart missing");
            Assert(on.Any(s => s.Kind == ScheduleKind.Shutdown && s.Days.Single() == DayOfWeek.Friday), "Friday shutdown missing");
            Assert(on.Any(s => s is { Kind: ScheduleKind.WindowsUpdate, DisableInput: true } && s.Stop == new TimeSpan(4, 0, 0)), "Tuesday Windows Update missing");
            Assert(on.Any(s => s.Kind == ScheduleKind.Maintenance && s.Days.Single() == DayOfWeek.Thursday), "Thursday maintenance missing");
            var off = CloudPolicyParser.Parse(PolicyXml(weekly: weekly.Replace("{0}", "N").Replace("{1}", "N")))!.Schedules;
            Assert(off.Count == 0, "Switched-off schedules shown");
            return Task.CompletedTask;
        });

        await check("Policy: the Cloud agent's maintenance window shows only when enabled", () =>
        {
            var details = CloudPolicyParser.Parse(PolicyXml(fwaMaintenance: "Enabled=\"true\" StartTime=\"12:00 AM\" EndTime=\"01:00 AM\""));
            Assert(details?.CloudMaintenance == ("12:00 AM", "01:00 AM"), "Enabled window missing");
            return Task.CompletedTask;
        });

        await check("Policy: missing flags stay unknown rather than defaulting", () =>
        {
            var details = CloudPolicyParser.Parse(PolicyXml(basic: ""));
            Assert(details is { PersistEventLogs: null, LicenseExpired: null }, "Missing flags defaulted");
            var minimal = CloudPolicyParser.Parse("<Policy Name='x'/>");
            Assert(minimal is { CheckInMinutes: null, CloudMaintenance: null, PersistEventLogs: null, LastModified: null } && minimal.Schedules.Count == 0 && !minimal.HasAeLists, "Minimal policy");
            return Task.CompletedTask;
        });

        foreach (var (name, xml) in new[]
        {
            ("wrong root", "<History><Policy Name='x'/></History>"),
            ("DTD", "<!DOCTYPE Policy [<!ENTITY e 'x'>]><Policy Name='&e;'/>"),
            ("broken XML", "<Policy><Product>"),
            ("empty", "  "),
            ("oversized", "<Policy Name='x'>" + new string(' ', CloudPolicyCache.MaximumXmlLength) + "</Policy>")
        })
        await check($"Policy: rejects {name}", () =>
        {
            Assert(CloudPolicyParser.Parse(xml) is null, "Accepted");
            return Task.CompletedTask;
        });

        await check("Policy: Anti-Executable lists and the current account's place in them", () =>
        {
            var details = CloudPolicyParser.Parse(PolicyXml())!;
            Assert(details.AeAdministrators.Select(u => (u.Name, u.IsGroup)).SequenceEqual([("localadmin", false), ("User", false), (@"TECHLAB\Technical Assistants Group", true)]), "Admin list wrong");
            Assert(details.AeTrustedUsers.Single().Name == "labtech", "Trusted list wrong");
            bool NoGroups(string group) => false;
            Assert(details.AeMembershipOf("user", @"WINDEV\user", NoGroups) is (AeMembership.Administrator, { Name: "User" }), "Case-insensitive admin missed");
            Assert(details.AeMembershipOf("labtech", null, NoGroups).Membership == AeMembership.TrustedUser, "Trusted user missed");
            Assert(details.AeMembershipOf("student", @"LAB\student", NoGroups) is (AeMembership.NotListed, null), "Unlisted account matched");
            Assert(details.AeMembershipOf("tech1", @"TECHLAB\tech1", group => group == @"TECHLAB\Technical Assistants Group").Membership == AeMembership.Administrator, "Group membership missed");
            Assert(details.AeMembershipOf("tech1", null, _ => throw new InvalidOperationException("no domain")).Membership == AeMembership.NotListed, "Group lookup failure leaked");
            var qualified = details with { AeTrustedUsers = [new(@"LAB\student", false)] };
            Assert(qualified.AeMembershipOf("student", @"lab\STUDENT", NoGroups).Membership == AeMembership.TrustedUser, "DOMAIN\\name entry missed");
            Assert(qualified.AeMembershipOf("student", @"OTHER\student", NoGroups).Membership == AeMembership.NotListed, "Different domain matched");
            return Task.CompletedTask;
        });

        await check("Schedules: next start, same-day, exact start and running window", () =>
        {
            var task = new DfSchedule(ScheduleKind.BatchFile, "BatchFile#3", [DayOfWeek.Monday, DayOfWeek.Wednesday], new(11, 11, 0), new(11, 26, 0), true, 60);
            var tuesdayNoon = new DateTime(2026, 11, 17, 12, 0, 0);       // a Tuesday
            Assert(task.NextStart(tuesdayNoon) == new DateTime(2026, 11, 18, 11, 11, 0), "Next Wednesday missed");
            var mondayEarly = new DateTime(2026, 11, 16, 9, 0, 0);
            Assert(task.NextStart(mondayEarly) == new DateTime(2026, 11, 16, 11, 11, 0), "Same-day start missed");
            Assert(task.NextStart(new DateTime(2026, 11, 16, 11, 11, 0)) == new DateTime(2026, 11, 16, 11, 11, 0), "Exact start skipped");
            Assert(task.NextStart(new DateTime(2026, 11, 18, 11, 12, 0)) == new DateTime(2026, 11, 23, 11, 11, 0), "Wrap to next week failed");
            Assert(task.IsRunning(new DateTime(2026, 11, 16, 11, 20, 0)) && !task.IsRunning(new DateTime(2026, 11, 16, 11, 26, 0)) && !task.IsRunning(tuesdayNoon), "Running window wrong");
            Assert((task with { Days = [] }).NextStart(tuesdayNoon) is null, "Dayless schedule has a start");
            Assert(!(task with { Stop = null }).IsRunning(new DateTime(2026, 11, 16, 11, 20, 0)), "Restart treated as a window");
            var details = CloudPolicyDetails.Empty with { Schedules = [task, task with { Name = "later", Days = [DayOfWeek.Friday] }, task with { Name = "never", Days = [] }] };
            Assert(details.Upcoming(tuesdayNoon).Select(item => item.Schedule.Name).SequenceEqual(["BatchFile#3", "later", "never"]), "Upcoming order wrong");
            return Task.CompletedTask;
        });

        await check("Schedules: day masks, times and names", () =>
        {
            Assert(CloudPolicyParser.DaysFromMask("0").Count == 0 && CloudPolicyParser.DaysFromMask("-1").Count == 0 && CloudPolicyParser.DaysFromMask("x").Count == 0 && CloudPolicyParser.DaysFromMask(null).Count == 0, "Bad mask decoded");
            Assert(CloudPolicyParser.DaysFromMask("127").Count == 7 && CloudPolicyParser.DaysFromMask("1").Single() == DayOfWeek.Sunday && CloudPolicyParser.DaysFromMask("4").Single() == DayOfWeek.Tuesday, "Mask bits wrong");
            Assert(CloudPolicyParser.DescribeDays(CloudPolicyParser.DaysFromMask("127")) == "Every day" && CloudPolicyParser.DescribeDays(CloudPolicyParser.DaysFromMask("62")) == "Weekdays"
                && CloudPolicyParser.DescribeDays(CloudPolicyParser.DaysFromMask("65")) == "Weekends" && CloudPolicyParser.DescribeDays(CloudPolicyParser.DaysFromMask("10")) == "Mon, Wed"
                && CloudPolicyParser.DescribeDays(CloudPolicyParser.DaysFromMask("67")) == "Mon, Sat, Sun", "Day description wrong");
            Assert(CloudPolicyParser.ParseTime("31:12:1899:21:30:00") == new TimeSpan(21, 30, 0) && CloudPolicyParser.ParseTime("0:0:0") == TimeSpan.Zero, "Time formats");
            Assert(CloudPolicyParser.ParseTime("25:00:00") is null && CloudPolicyParser.ParseTime("12:00") is null && CloudPolicyParser.ParseTime("a:b:c") is null && CloudPolicyParser.ParseTime("-1:00:00") is null, "Bad time accepted");
            Assert(CloudPolicyParser.KindFromName("BatchFile#3") == ScheduleKind.BatchFile && CloudPolicyParser.KindFromName("Shutdown#1") == ScheduleKind.Shutdown
                && CloudPolicyParser.KindFromName("Windows Update#2") == ScheduleKind.WindowsUpdate && CloudPolicyParser.KindFromName("Reboot") == ScheduleKind.Restart
                && CloudPolicyParser.KindFromName("Mystery#1") == ScheduleKind.Other, "Kinds wrong");
            Assert(CloudPolicyParser.Describe(ScheduleKind.BatchFile) == "Batch file" && CloudPolicyParser.Describe(ScheduleKind.Restart) == "Restart", "Kind labels");
            return Task.CompletedTask;
        });

        await check("Policy: read from a cache file, and refused when ambiguous or missing", () =>
        {
            using (var fixture = new PolicyCacheFixture(("CURRENT_PRODUCTS_POLICY", PolicyXml(VmAmalSched))))
                Assert(CloudPolicyCache.ReadDetails(fixture.Path)?.Schedules.Count == 1, "Details not read from cache");
            using (var fixture = new PolicyCacheFixture(("CURRENT_PRODUCTS_POLICY", PolicyXml())))
            {
                fixture.Execute("INSERT INTO CoreInfo(Key,Value) VALUES('CURRENT_PRODUCTS_POLICY', '<Policy Name=\"B\"/>')");
                Assert(CloudPolicyCache.ReadDetails(fixture.Path) is null, "Two policies accepted");
            }
            Assert(CloudPolicyCache.ReadDetails(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat")) is null, "Missing cache");
            return Task.CompletedTask;
        });

        // ---------------------------------------------------------------- licence
        await check("Licence: the VM's DFC output", () =>
        {
            var licence = DfLicense.Parse("License Type: Subscription\r\nExpiry Date: Sunday, January 10, 2027\r\n", "License Status: Licensed\r\nExpiry Date: Sunday, January 10, 2027\r\n");
            Assert(licence is { Type: "Subscription", Status: "Licensed", IsLicensed: true } && licence.Expiry == new DateOnly(2027, 1, 10), "Licence misread: " + licence);
            var oneLine = DfLicense.Parse("License Type: Subscription Expiry Date: Sunday, January 10, 2027", null);
            Assert(oneLine is { Type: "Subscription", Status: null } && oneLine.Expiry == new DateOnly(2027, 1, 10), "One-line output: " + oneLine);
            Assert(DfLicense.Parse(null, null) is null && DfLicense.Parse("garbage", "") is null, "Garbage became a licence");
            Assert(DfLicense.Parse("License Type: Perpetual", null) is { Type: "Perpetual", Expiry: null }, "No expiry");
            Assert(DfLicense.ParseDate("31 Febtober 2027") is null && DfLicense.ParseDate("2027-01-10") == new DateOnly(2027, 1, 10), "Dates");
            return Task.CompletedTask;
        });

        await check("Licence: warnings at 30 days, on expiry and for a bad status", () =>
        {
            var licence = new DfLicense("Subscription", "Licensed", new DateOnly(2027, 1, 10));
            Assert(!licence.NeedsAttention(new DateOnly(2026, 12, 10)) && licence.Describe(new DateOnly(2026, 12, 10)) == "Subscription · until 10 Jan 2027", "31 days out");
            Assert(licence.NeedsAttention(new DateOnly(2026, 12, 11)) && licence.Describe(new DateOnly(2026, 12, 11)) == "Subscription · expires in 30 days", "30 days out");
            Assert(licence.Describe(new DateOnly(2027, 1, 9)) == "Subscription · expires in 1 day" && licence.Describe(new DateOnly(2027, 1, 10)) == "Subscription · expires today", "Last days");
            Assert(licence.Describe(new DateOnly(2027, 2, 1)) == "Subscription · expired 10 Jan 2027" && licence.NeedsAttention(new DateOnly(2027, 2, 1)), "Expired");
            var expired = new DfLicense("Subscription", "Expired", null);
            Assert(expired.NeedsAttention(new DateOnly(2026, 1, 1)) && expired.Describe(new DateOnly(2026, 1, 1)) == "Subscription · Expired", "Bad status");
            Assert(new DfLicense(null, null, null).Describe(new DateOnly(2026, 1, 1)) == "Unavailable", "Empty licence");
            return Task.CompletedTask;
        });

        await check("Licence: DFC is asked with get queries only, and failures read as unknown", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: "License Type: Subscription Expiry Date: Sunday, January 10, 2027"), new ProcessResult(0, Output: "License Status: Licensed"));
            var licence = await new DfcClient(runner).GetLicenseAsync("DFC.exe");
            Assert(runner.Calls.Count == 2 && runner.Calls[0].SequenceEqual(["get", "/LICENSETYPE"]) && runner.Calls[1].SequenceEqual(["get", "/LICENSESTATUS"]), "Wrong commands");
            Assert(licence is { Type: "Subscription", Status: "Licensed" }, "Licence lost");
            var failing = new FakeRunner(new ProcessResult(5, Output: "License Type: Fake"), new ProcessResult(0, TimedOut: true, Output: "License Status: Fake"));
            Assert(await new DfcClient(failing).GetLicenseAsync("DFC.exe") is null, "Failed queries trusted");
        });

        await check("Durations: two units at most, rounded down, never negative", () =>
        {
            var cases = new (TimeSpan Span, string Text)[]
            {
                (TimeSpan.FromSeconds(-5), "under a minute"), (TimeSpan.FromSeconds(59), "under a minute"), (TimeSpan.FromMinutes(1), "1 min"),
                (TimeSpan.FromMinutes(59.9), "59 min"), (TimeSpan.FromHours(1), "1 h"), (new TimeSpan(2, 14, 50), "2 h 14 min"),
                (TimeSpan.FromHours(4), "4 h"), (new TimeSpan(1, 4, 30, 0), "1 d 4 h"), (TimeSpan.FromDays(3), "3 d")
            };
            foreach (var (span, text) in cases) Assert(DurationText.Describe(span) == text, $"{span} gave {DurationText.Describe(span)}");
            return Task.CompletedTask;
        });

        // ---------------------------------------------------------------- components
        await check("Components: all running, one stopped, one missing, product absent", () =>
        {
            List<ComponentStatus> all = ComponentHealth.Expected.Select(e => new ComponentStatus(e.Name, e.Role, e.IsDriver, "Running", "Auto")).ToList();
            Assert(ComponentHealth.Problems(all).Count == 0 && ComponentHealth.Summary(all) == "All Faronics components are running.", "Healthy set flagged");
            var stopped = all.Select(c => c.Name == "DFServ" ? c with { State = "Stopped" } : c).ToList();
            Assert(ComponentHealth.Problems(stopped).Single() == "Deep Freeze service (DFServ) is stopped", "Stopped service missed");
            var missing = all.Where(c => c.Name != "DeepFrz").ToList();
            Assert(ComponentHealth.Problems(missing).Single() == "Deep Freeze driver (DeepFrz) isn't registered", "Missing driver missed");
            var noAe = all.Where(c => c.Name != "AEEngine").ToList();
            Assert(ComponentHealth.Problems(noAe).Count == 0, "Absent product reported as broken");
            Assert(ComponentHealth.Summary([]) == "No Faronics drivers or services are registered.", "Nothing installed");
            var caseless = all.Select(c => c with { Name = c.Name.ToUpperInvariant(), State = "running" }).ToList();
            Assert(ComponentHealth.Problems(caseless).Count == 0, "Case differences flagged");
            return Task.CompletedTask;
        });
    }

    private static Dictionary<string, string?> Row(params (string Key, string? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
