using System.Text;
using HeavyRain.Core;

internal static class AecInsightChecks
{
    // Shapes copied from AE.log on Anti-Executable 6.11.2112.730; hashes and names are placeholders.
    private const string Hash1 = "C6D4FD535869F3C0CF9B53DDCCD78E687912263F";
    private const string Hash2 = "7E4F330C46B036DB7EFE8F3C6636882375515521";
    private const string DriverPath = @"C:\Program Files (x86)\Faronics\Faronics Cloud\Faronics Cloud Agent\fardisk32.sys";
    private const string AppPath = @"C:\Users\User\Desktop\HeavyRain-1.4.6.exe";

    private static readonly string[] SampleLog =
    [
        "11/12/2026 08:02:26#WinDev#SYSTEM#Protection enabled.#Protection enabled.",
        $"11/12/2026 17:05:48#WinDev#SYSTEM#Unknown File Allowed#Unknown File Allowed( {DriverPath} )",
        $"0#{Hash1}#{DriverPath}#11/12/2026 17:05:48# #.sys#Microsoft Windows Hardware Compatibility Publisher#Deep Freeze#FarDisk.sys#9,00,520,5760#3300000062f4# #",
        "not a log line",
        "11/12/2026 17:15:30#WinDev#SYSTEM#Maintenance mode enabled.#Maintenance mode enabled.",
        @"11/12/2026 17:23:40#WinDev#WINDEV\User#Unknown File Allowed#Unknown File Allowed( " + AppPath + " )",
        $"0#{Hash2}#{AppPath}#11/12/2026 17:23:40# #.exe# #Heavy Rain#HeavyRain.dll#1.4.6.0# # #",
        $"0#{Hash1}#C:\\Other\\elsewhere.exe#11/12/2026 17:23:41# #.exe# #Other#other.exe#1.0# # #",
        "11/12/2026 17:25:00#WinDev#SYSTEM#Configuration changed#Configuration changed"
    ];

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        // ---------------------------------------------------------------- policy
        await check("AE policy strings from the registry become readable settings", () =>
        {
            var policy = AecPolicy.FromValues(new Dictionary<string, string?>
            {
                ["Unknown Files"] = "AllowAndLogViolations", ["Control List"] = "EnforceLogViolation",
                ["Deployment Type"] = "CustomDeployment", ["StealthMode"] = "Off", ["Allow All windows OS files"] = "TRUE"
            });
            Assert(policy is not null && policy.UnknownFilesRun, "Allow-and-log policy not flagged");
            var described = policy!.Describe();
            Assert(described.SequenceEqual(new[]
            {
                ("Unknown files", "Allow and log violations"), ("Control list", "Enforce log violation"), ("Deployment", "Custom deployment"),
                ("Stealth mode", "Off"), ("All Windows OS files allowed", "Yes")
            }), "Policy description changed: " + string.Join("; ", described));
            return Task.CompletedTask;
        });

        await check("AE policy value names are matched without regard to case", () =>
        {
            var policy = AecPolicy.FromValues(new Dictionary<string, string?> { ["unknown files"] = "AllowAndLogViolations" });
            Assert(policy?.UnknownFiles == "AllowAndLogViolations" && policy.Describe().Count == 1, "Lower-case value name missed");
            return Task.CompletedTask;
        });

        await check("Missing or blank AE policy values produce no policy", () =>
        {
            Assert(AecPolicy.FromValues(new Dictionary<string, string?>()) is null, "Empty key produced a policy");
            Assert(AecPolicy.FromValues(new Dictionary<string, string?> { ["Unknown Files"] = "  ", ["Control List"] = null }) is null, "Blank values produced a policy");
            return Task.CompletedTask;
        });

        await check("An unknown-file setting that doesn't start with Allow isn't flagged", () =>
        {
            var policy = AecPolicy.FromValues(new Dictionary<string, string?> { ["Unknown Files"] = "BlockAndLogViolations" });
            Assert(policy is { UnknownFilesRun: false } && policy.Describe()[0].Value == "Block and log violations", "Non-allow setting flagged or garbled");
            return Task.CompletedTask;
        });

        foreach (var raw in new[] { "SOME_VALUE", "12 MB", "v2.1", "Off" })
        await check($"Policy text '{raw}' is shown as written when it isn't plain words", () =>
        {
            Assert(AecPolicy.Humanize(raw) == raw, $"'{raw}' became '{AecPolicy.Humanize(raw)}'");
            return Task.CompletedTask;
        });

        // ---------------------------------------------------------------- log parsing
        await check("AE log events parse with their file details attached", () =>
        {
            var entries = AeLog.Parse(SampleLog);
            Assert(entries.Count == 5, $"Expected 5 events, got {entries.Count}");
            Assert(entries[0] is { Kind: AeLogKind.ProtectionChange, FilePath: null, Event: "Protection enabled.", User: "SYSTEM" }, "Protection event wrong");
            Assert(entries[1] is { Kind: AeLogKind.Allowed, FilePath: DriverPath, Hash: Hash1, Product: "Deep Freeze", Version: "9,00,520,5760", Publisher: "Microsoft Windows Hardware Compatibility Publisher" },
                "Path with (x86) or its detail line was not parsed");
            Assert(entries[1].FileName == "fardisk32.sys", "File name wrong");
            Assert(entries[2].Kind == AeLogKind.ProtectionChange && entries[2].Event == "Maintenance mode enabled.", "Maintenance event wrong");
            Assert(entries[3] is { FilePath: AppPath, Hash: Hash2, Publisher: null, Product: "Heavy Rain", Version: "1.4.6.0", User: @"WINDEV\User" }, "App event wrong");
            Assert(entries[4] is { Kind: AeLogKind.Other, FilePath: null, Event: "Configuration changed" }, "Other event wrong");
            Assert(entries[0].Time == "11/12/2026 08:02:26", "Timestamp was not kept exactly as written");
            return Task.CompletedTask;
        });

        await check("An AE detail line for a different file is never attached", () =>
        {
            var entries = AeLog.Parse(SampleLog);
            Assert(entries[3].Hash == Hash2 && entries.All(entry => entry.FilePath != @"C:\Other\elsewhere.exe"), "Mismatched detail line attached or invented an event");
            return Task.CompletedTask;
        });

        await check("AE log message text containing # is kept whole", () =>
        {
            var entries = AeLog.Parse(["11/12/2026 09:00:00#PC#SYSTEM#Note#Part one # part two"]);
            Assert(entries.Single().Message == "Part one # part two", "Message split on #");
            return Task.CompletedTask;
        });

        foreach (var (line, kind) in new[]
        {
            ("11/12/2026 09:00:00#PC#PC\\User#Unknown File Blocked#Unknown File Blocked( C:\\Temp\\tool.exe )", AeLogKind.Blocked),
            ("11/12/2026 09:00:00#PC#SYSTEM#Protection disabled.#Protection disabled.", AeLogKind.ProtectionChange),
            ("11/12/2026 09:00:00#PC#SYSTEM#Maintenancemode disabled#Maintenancemode disabled", AeLogKind.ProtectionChange),
            ("11/12/2026 09:00:00#PC#SYSTEM#Configuration Backup Taken#Backup Config Taken", AeLogKind.Other)
        })
        await check($"AE log event is classified as {kind}", () =>
        {
            Assert(AeLog.Parse([line]).Single().Kind == kind, "Wrong classification");
            return Task.CompletedTask;
        });

        foreach (var message in new[] { "Something happened( not a path )", "Configuration changed", "Odd( relative\\file.exe )" })
        await check("Text in brackets that isn't a full path is not treated as a file", () =>
        {
            Assert(AeLog.Parse([$"11/12/2026 09:00:00#PC#SYSTEM#Event#{message}"]).Single().FilePath is null, "Non-path became a file");
            return Task.CompletedTask;
        });

        await check("Newest AE log entries come first and are limited", () =>
        {
            var entries = AeLog.Parse(SampleLog);
            var newest = AeLog.Newest(entries, 2);
            Assert(newest.Count == 2 && newest[0].Event == "Configuration changed" && newest[1].FilePath == AppPath, "Wrong order or limit");
            Assert(AeLog.Newest(entries, 50).Count == entries.Count, "Limit above count dropped entries");
            return Task.CompletedTask;
        });

        // ---------------------------------------------------------------- reading the file
        await check("Short AE log files are read whole", () => WithTempFile(path =>
        {
            File.WriteAllText(path, string.Join("\r\n", SampleLog) + "\r\n", new UTF8Encoding(false));
            Assert(AeLog.Parse(AeLog.ReadTailLines(path)).Count == 5, "Short file not read whole");
        }));

        await check("Reading the end of a long AE log drops the cut-off first line", () => WithTempFile(path =>
        {
            var filler = Enumerable.Range(0, 400).Select(i => $"11/12/2026 08:{i / 60 % 60:00}:{i % 60:00}#PC#SYSTEM#Filler {i}#Filler {i} with some padding text");
            File.WriteAllText(path, string.Join("\r\n", filler.Concat(SampleLog)) + "\r\n", new UTF8Encoding(false));
            var lines = AeLog.ReadTailLines(path, 2048);
            var entries = AeLog.Parse(lines);
            Assert(entries.Count > 5 && entries.Count < 60, $"Unexpected entry count {entries.Count}");
            Assert(entries[^1].Event == "Configuration changed", "Last entry missing");
            var unexpected = lines.Where(line => line.Length > 0).FirstOrDefault(line => !(line.StartsWith("11/12/2026") || line.StartsWith("0#") || line.TrimEnd('\r') == "not a log line"));
            Assert(unexpected is null, $"A partial first line was kept: [{unexpected}]");
        }));

        await check("UTF-16 AE logs are read, including from the middle", () => WithTempFile(path =>
        {
            var filler = Enumerable.Range(0, 200).Select(i => $"11/12/2026 08:00:{i % 60:00}#PC#SYSTEM#Filler {i}#Filler {i}");
            File.WriteAllText(path, string.Join("\r\n", filler.Concat(SampleLog)) + "\r\n", Encoding.Unicode);
            Assert(AeLog.Parse(AeLog.ReadTailLines(path)).Count == 205, "Whole UTF-16 file not read");
            var tail = AeLog.Parse(AeLog.ReadTailLines(path, 3001));
            Assert(tail.Count > 5 && tail[^1].Event == "Configuration changed" && tail.All(entry => entry.Time.StartsWith("11/12/2026")), "UTF-16 tail misread");
        }));

        await check("The AE log can be read while another program has it open for writing", () => WithTempFile(path =>
        {
            File.WriteAllText(path, string.Join("\r\n", SampleLog) + "\r\n");
            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Assert(AeLog.Parse(AeLog.ReadTailLines(path)).Count == 5, "Shared read failed");
        }));

        await check("An empty AE log reads as no entries", () => WithTempFile(path =>
        {
            File.WriteAllBytes(path, []);
            Assert(AeLog.Parse(AeLog.ReadTailLines(path)).Count == 0, "Empty file produced entries");
        }));

        // ---------------------------------------------------------------- allow command
        await check("Allow sends the documented AEC allow command once with the password", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: "Done"));
            var result = await new AecClient(runner).AllowAsync("AEC.exe", "pw", AppPath);
            Assert(result.Outcome == ChangeOutcome.Accepted && result.Detail.Contains("isn't independently verified"), "Allow not accepted or overclaimed");
            Assert(runner.Calls.Count == 1 && runner.Calls[0].SequenceEqual(new[] { "allow", AppPath, "/pw=pw" }), "Wrong allow command");
        });

        await check("Allow without a password sends no /pw argument", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0));
            await new AecClient(runner).AllowAsync("AEC.exe", "", DriverPath);
            Assert(runner.Calls.Single().SequenceEqual(new[] { "allow", DriverPath }), "Unexpected arguments");
        });

        foreach (var path in new[] { @"\\server\share\tools\app.exe", @"C:\Program Files (x86)\Tool\app.exe" })
        await check("Allow accepts full local and UNC paths", () =>
        {
            Assert(AecClient.IsAllowablePath(path), $"{path} was refused");
            return Task.CompletedTask;
        });

        foreach (var path in new[] { "", "app.exe", @"Tools\app.exe", @"C:\Tools\..\Windows\app.exe", @"C:\Tools\", "C:\\Tools\\a\"b.exe", "C:\\Tools\\a\nb.exe", @"C:\", @"\\server\share" })
        await check("Allow refuses a path that isn't a plain full file path without running AEC", async () =>
        {
            var runner = new FakeRunner();
            var result = await new AecClient(runner).AllowAsync("AEC.exe", "pw", path);
            Assert(result.Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 0, $"'{path}' was sent to AEC");
        });

        await check("Allow reports a non-zero AEC exit as rejected", async () =>
        {
            var result = await new AecClient(new FakeRunner(new ProcessResult(3))).AllowAsync("AEC.exe", "pw", AppPath);
            Assert(result.Outcome == ChangeOutcome.Rejected && result.ExitCode == 3, "Failure not reported");
        });

        foreach (var (password, diagnostic) in new[] { ("", AecDiagnostic.PasswordRequired), ("wrong", AecDiagnostic.PasswordRejected) })
        await check($"Allow reports AEC's password prompt as {diagnostic}", async () =>
        {
            var prompt = new ProcessResult(0, Output: "Enter AE Administrator Password : \nInvalid AE Administrator Password");
            var result = await new AecClient(new FakeRunner(prompt)).AllowAsync("AEC.exe", password, AppPath);
            Assert(result.Outcome == ChangeOutcome.Rejected && result.Status?.Diagnostic == diagnostic, "Password problem not reported");
        });

        await check("Allow with an unreadable AEC result is uncertain, not accepted", async () =>
        {
            var result = await new AecClient(new FakeRunner(new ProcessResult(null, TimedOut: true))).AllowAsync("AEC.exe", "pw", AppPath);
            Assert(result.Outcome == ChangeOutcome.Uncertain, "Timeout treated as a definite result");
        });
    }

    private static Task WithTempFile(Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), "HeavyRain-AeLog-" + Guid.NewGuid() + ".log");
        try { body(path); }
        finally { try { File.Delete(path); } catch (IOException) { } }
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
