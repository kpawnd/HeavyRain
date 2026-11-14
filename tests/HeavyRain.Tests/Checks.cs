using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HeavyRain.Core;

if (args.SequenceEqual(new[] { "status" })) return await AecDiagnosticChecks.RunFixtureAsync();

if (args.FirstOrDefault() == "--fixture")
{
    switch (args[1])
    {
        case "echo": Console.Write(JsonSerializer.Serialize(args.Skip(2))); break;
        case "flood":
            for (int i = 0; i < 100; i++) { Console.Write(new string('x', 16384)); Console.Error.Write(new string('y', 16384)); }
            break;
        case "hang": await Task.Delay(30000); break;
        case "stdin": Console.Write(Console.ReadLine() is null ? "closed" : "open"); break;
        case "exit": return int.Parse(args[2]);
    }
    return 0;
}

int passed = 0, failed = 0;
async Task Check(string name, Func<Task> action)
{
    try { await action(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); failed++; }
}
static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

foreach (var (code, expected) in new[] { (0, ProtectionState.Thawed), (1, ProtectionState.Frozen), (2, ProtectionState.Unknown), (3, ProtectionState.Unknown), (4, ProtectionState.Unknown), (5, ProtectionState.Unknown), (19, ProtectionState.Unknown), (-1, ProtectionState.Unknown) })
await Check($"Query maps exit {code} to {expected}", async () =>
{
    var runner = new FakeRunner(new ProcessResult(code));
    var result = await new DfcClient(runner).QueryAsync("DFC.exe");
    Assert(result.State == expected, "Incorrect protection state");
    Assert(runner.Calls.Single().SequenceEqual(new[] { "get", "/ISFROZEN" }), "Wrong status command");
});

foreach (var processResult in new[] { new ProcessResult(0, TimedOut: true), new ProcessResult(1, Error: "Denied"), new ProcessResult(null) })
await Check("Uncertain status never reports frozen or thawed", async () =>
    Assert((await new DfcClient(new FakeRunner(processResult)).QueryAsync("DFC.exe")).State == ProtectionState.Unknown, "False status"));

foreach (var (target, restart, command, initial) in new[] {
    (BootTarget.Frozen, false, "/FREEZENEXTBOOT", 0), (BootTarget.Thawed, false, "/THAWNEXTBOOT", 1) })
await Check($"Correct transition {command} with fresh preflight", async () =>
{
    var runner = new FakeRunner(new(initial), new(0));
    var result = await new DfcClient(runner, "shutdown.exe").ChangeAsync("DFC.exe", "example secret", target, restart);
    Assert(result.Outcome == ChangeOutcome.Accepted, "Change not accepted");
    Assert(runner.Calls.Count == 2 && runner.Calls[0][1] == "/ISFROZEN", "Preflight missing");
    Assert(runner.Calls[1].SequenceEqual(new[] { "example secret", command }), "Wrong arguments");
    Assert(!result.Detail.Contains("example secret"), "Password leaked");
});

foreach (var (target, restart, initial) in new[] { (BootTarget.Frozen, false, 0), (BootTarget.Frozen, true, 0), (BootTarget.Thawed, false, 1), (BootTarget.Thawed, true, 1) })
await Check($"{target}{(restart ? " with restart" : "")} never uses DFC's restart commands", async () =>
{
    var runner = new FakeRunner(new(initial), new(0), new(0));
    await new DfcClient(runner, "shutdown.exe").ChangeAsync("DFC.exe", "example secret", target, restart);
    Assert(runner.Calls.All(call => !call.Contains("/BOOTFROZEN") && !call.Contains("/BOOTTHAWED")), "A DFC restart command was sent");
    Assert(runner.Calls[1][1] == (target == BootTarget.Thawed ? "/THAWNEXTBOOT" : "/FREEZENEXTBOOT"), "Wrong next-boot command");
    Assert(runner.Calls.Count == (restart ? 3 : 2), "Windows restart missing or unexpected");
});

await Check("Freeze with restart sends /FREEZENEXTBOOT, then restarts Windows once", async () =>
{
    var runner = new FakeRunner(new(0), new(0), new(0));
    var result = await new DfcClient(runner, "shutdown.exe").ChangeAsync("DFC.exe", "example secret", BootTarget.Frozen, true);
    Assert(result is { Outcome: ChangeOutcome.Accepted, RestartFailed: false } && result.Detail.Contains("boots Frozen"), "Freeze restart not reported");
    Assert(runner.Calls[2].SequenceEqual(new[] { "/r", "/t", "0" }), "Wrong restart command");
});

await Check("Thaw uses /THAWNEXTBOOT and says it lasts until frozen", async () =>
{
    var runner = new FakeRunner(new(1), new(0));
    var result = await new DfcClient(runner, "shutdown.exe").ChangeAsync("DFC.exe", "example secret", BootTarget.Thawed, false);
    Assert(result.Outcome == ChangeOutcome.Accepted && result.Detail.Contains("stays Thawed until you freeze it") && !result.Detail.Contains("Frozen again"), "Thaw promised a refreeze DFC doesn't do");
    Assert(runner.Calls.All(call => !call.Contains("/BOOTTHAWED")), "DFC restart command sent");
});

await Check("Thaw with restart sends /THAWNEXTBOOT, then restarts Windows once", async () =>
{
    var runner = new FakeRunner(new(1), new(0), new(0));
    var result = await new DfcClient(runner, "shutdown.exe").ChangeAsync("DFC.exe", "example secret", BootTarget.Thawed, true);
    Assert(result is { Outcome: ChangeOutcome.Accepted, RestartFailed: false }, "Restart not reported");
    Assert(runner.Calls.Count == 3 && runner.Calls[1].SequenceEqual(new[] { "example secret", "/THAWNEXTBOOT" }), "Wrong Deep Freeze command");
    Assert(runner.Calls[2].SequenceEqual(new[] { "/r", "/t", "0" }) && !runner.Calls[2].Contains("example secret"), "Wrong restart command");
    Assert(runner.Calls.All(call => !call.Contains("/BOOTTHAWED")), "Permanent thaw sent");
});

foreach (var restartResult in new[] { new ProcessResult(1), new ProcessResult(null, TimedOut: true), new ProcessResult(null, Error: "denied") })
await Check("A thaw whose Windows restart fails is still accepted, and says to restart manually", async () =>
{
    var runner = new FakeRunner(new(1), new(0), restartResult);
    var result = await new DfcClient(runner, "shutdown.exe").ChangeAsync("DFC.exe", "example secret", BootTarget.Thawed, true);
    Assert(result is { Outcome: ChangeOutcome.Accepted, RestartFailed: true } && result.Detail.Contains("Restart the PC yourself"), "Failed restart hidden");
});

await Check("A thaw that Deep Freeze rejects never restarts Windows", async () =>
{
    var runner = new FakeRunner(new(1), new(5));
    var result = await new DfcClient(runner, "shutdown.exe").ChangeAsync("DFC.exe", "example secret", BootTarget.Thawed, true);
    Assert(result.Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 2, "Restarted after a rejected thaw");
});

foreach (var password in new[] { "", new string('x', 64), "line\nbreak", "nul\0char" })
await Check("Invalid password never starts a process", async () =>
{
    var runner = new FakeRunner();
    var result = await new DfcClient(runner).ChangeAsync("DFC.exe", password, BootTarget.Frozen, false);
    Assert(result.Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 0, "Invalid request executed");
});

foreach (var code in new[] { 5, 9 })
await Check($"A change rejected with DFC error {code} points to the Command Line password", async () =>
{
    var runner = new FakeRunner(new(1), new(code));
    var result = await new DfcClient(runner).ChangeAsync("DFC.exe", "example secret", BootTarget.Thawed, false);
    Assert(result.Outcome == ChangeOutcome.Rejected && result.ExitCode == code, "Failure not rejected");
    Assert(result.Detail.Contains("Command Line password") && result.Detail.Contains($"error {code}") && !result.Detail.Contains("example secret"), "Password cause missing or secret echoed");
    Assert(runner.Calls.Count == 2, "Change was retried");
});

await Check("A failed change quotes DFC's own output with the password blanked out", async () =>
{
    var runner = new FakeRunner(new(1), new(5, Output: "Error:   bad request for example secret\r\n", StandardError: "details"));
    var result = await new DfcClient(runner).ChangeAsync("DFC.exe", "example secret", BootTarget.Thawed, false);
    Assert(result.Detail.Contains("DFC said: \"Error: bad request for ••• details\"") && !result.Detail.Contains("example secret"), "Output not quoted or password leaked: " + result.Detail);
});

await Check("A failed change with no DFC output says so", async () =>
{
    var result = await new DfcClient(new FakeRunner(new(1), new(5))).ChangeAsync("DFC.exe", "example secret", BootTarget.Thawed, false);
    Assert(result.Detail.EndsWith("DFC printed nothing."), "Empty output not reported");
});

await Check("A status query failing with error 5 keeps the generic explanation", async () =>
{
    var result = await new DfcClient(new FakeRunner(new ProcessResult(5))).QueryAsync("DFC.exe");
    Assert(result.State == ProtectionState.Unknown && result.Detail.Contains("internal error 5") && !result.Detail.Contains("Command Line password"), "Query blamed a password it never used");
});

await Check("63-character password is supported", async () =>
    Assert((await new DfcClient(new FakeRunner(new(0), new(0))).ChangeAsync("DFC.exe", new string('x', 63), BootTarget.Frozen, false)).Outcome == ChangeOutcome.Accepted, "Boundary rejected"));

await Check("Invalid enum does not send commands", async () =>
{
    var runner = new FakeRunner();
    Assert((await new DfcClient(runner).ChangeAsync("DFC.exe", "test", (BootTarget)42, false)).Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 0, "Invalid enum accepted");
});

foreach (var (target, initial) in new[] { (BootTarget.Frozen, 1), (BootTarget.Thawed, 0) })
await Check("Already-current state never changes next-boot settings", async () =>
{
    var runner = new FakeRunner(new ProcessResult(initial));
    Assert((await new DfcClient(runner).ChangeAsync("DFC.exe", "test", target, true)).Outcome == ChangeOutcome.NotNeeded, "Wrong outcome");
    Assert(runner.Calls.Count == 1, "Unnecessary mutation");
});

await Check("Failed preflight prevents changes", async () =>
{
    var runner = new FakeRunner(new ProcessResult(2));
    Assert((await new DfcClient(runner).ChangeAsync("DFC.exe", "test", BootTarget.Thawed, false)).Outcome == ChangeOutcome.Rejected, "Should refuse");
    Assert(runner.Calls.Count == 1, "Mutation despite unknown status");
});

foreach (var code in new[] { 1, 2, 3, 4, 5, 99, -1 })
await Check($"Change exit {code} is never treated as success", async () =>
    Assert((await new DfcClient(new FakeRunner(new(1), new(code))).ChangeAsync("DFC.exe", "test", BootTarget.Thawed, false)).Outcome == ChangeOutcome.Rejected, "False success"));

foreach (var uncertain in new[] { new ProcessResult(null, TimedOut: true), new ProcessResult(null, Error: "Cancelled"), new ProcessResult(null) })
await Check("Lost change response is explicitly uncertain", async () =>
    Assert((await new DfcClient(new FakeRunner(new(1), uncertain)).ChangeAsync("DFC.exe", "test", BootTarget.Thawed, false)).Outcome == ChangeOutcome.Uncertain, "Uncertainty lost"));

await Check("Version parsing only returns the numeric version", async () =>
    Assert(await new DfcClient(new FakeRunner(new ProcessResult(0, Output: "Deep Freeze 9.10.220.1234\nprivate junk"))).GetVersionAsync("DFC.exe") == "9.10.220.1234", "Unexpected output disclosure"));
await Check("Version failure is not treated as a version", async () =>
    Assert(await new DfcClient(new FakeRunner(new ProcessResult(5, Output: "9.0"))).GetVersionAsync("DFC.exe") == "Unavailable", "Failure treated as version"));

var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
var assembly = Assembly.GetExecutingAssembly().Location;
await Check("Real process preserves password-shaped arguments without shell expansion", async () =>
{
    string[] samples = ["a b", "a\"b", "ending\\", "x&echo bad", "$()", "semi;colon", "雪", ""];
    var result = await new CommandProcessRunner().RunAsync(dotnet, new[] { assembly, "--fixture", "echo" }.Concat(samples).ToArray());
    Assert(result.ExitCode == 0, "Child failed");
    Assert(JsonSerializer.Deserialize<string[]>(result.Output)!.SequenceEqual(samples), "Arguments changed");
});
await Check("Real process drains stdout and stderr without deadlock or unbounded capture", async () =>
{
    var result = await new CommandProcessRunner().RunAsync(dotnet, [assembly, "--fixture", "flood"]);
    Assert(result.ExitCode == 0 && result.Output.Length == 4096, "Drain failed");
});
await Check("Real process timeout is bounded", async () =>
{
    var sw = Stopwatch.StartNew();
    var result = await new CommandProcessRunner(TimeSpan.FromMilliseconds(300)).RunAsync(dotnet, [assembly, "--fixture", "hang"]);
    Assert(result.TimedOut && result.ExitCode is null && sw.Elapsed < TimeSpan.FromSeconds(5), "Timeout failed");
});
await Check("Real exit 1 survives transport", async () =>
    Assert((await new CommandProcessRunner().RunAsync(dotnet, [assembly, "--fixture", "exit", "1"])).ExitCode == 1, "Boolean return lost"));
await Check("Missing executable is an error, never thawed", async () =>
    Assert((await new CommandProcessRunner().RunAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), ["get", "/ISFROZEN"])).Error is not null, "Missing file not rejected"));
await Check("Cancellation before start never reports success", async () =>
{
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    var result = await new CommandProcessRunner().RunAsync(dotnet, [assembly, "--fixture", "echo"], cancel.Token);
    Assert(result.Error is not null && result.ExitCode is null, "Cancellation lost");
});

foreach (var (title, busy, uncertain, administrator, hasDfc, state, passwordLength) in new[]
{
    ("An operation is still running", true, false, true, true, ProtectionState.Frozen, 8),
    ("Administrator access is required", false, false, false, true, ProtectionState.Frozen, 8),
    ("Verify the previous request first", false, true, true, true, ProtectionState.Frozen, 8),
    ("Deep Freeze control is unavailable", false, false, true, false, ProtectionState.Unknown, 0),
    ("Protection state is unknown", false, false, true, true, ProtectionState.Unknown, 8),
    ("This PC is already thawed", false, false, true, true, ProtectionState.Thawed, 8),
    ("Enter your Deep Freeze password", false, false, true, true, ProtectionState.Frozen, 0),
    ("The password is too long", false, false, true, true, ProtectionState.Frozen, 64)
})
await Check($"Clickable action explains: {title}", () =>
{
    var blocker = BootActionGuard.Check(BootTarget.Thawed, busy, uncertain, administrator, hasDfc, state, "Query timed out.", passwordLength);
    Assert(blocker?.Title == title && blocker.Message.Length > 0, "Missing or incorrect explanation");
    return Task.CompletedTask;
});
await Check("Ready action passes the UI prerequisites", () =>
{
    Assert(BootActionGuard.Check(BootTarget.Thawed, false, false, true, true, ProtectionState.Frozen, "", 10) is null, "Valid change blocked");
    return Task.CompletedTask;
});
foreach (var (labels, expected) in new (string[], string?)[]
{
    (["Computer Name: JANICE", "Current Policy: DEPLOYMENT", "Group: ELSEWHERE"], "DEPLOYMENT"),
    (["Current Policy:", "Office computers"], "Office computers"),
    (["Current Policy:", "Group: DEPLOYMENT"], null),
    (["Group: DEPLOYMENT", "Default Policy: Wrong"], null),
    (["Current Policy: "], null),
    (["Current Policy: line\nbreak"], null),
    (["Current Policy: " + new string('x', 257)], null)
})
await Check("Policy reader accepts only an explicit, bounded current-policy label", () =>
{
    Assert(PolicyNameParser.FromWindowLabels(labels) == expected, "Wrong policy inferred");
    return Task.CompletedTask;
});

foreach (var (valueName, expected) in new (string, int?)[]
{
    ("CurrentPolicyName", 0), ("Current Policy Name", 0), ("applied_policy_name", 0),
    ("ACTIVE-POLICY-NAME", 0), ("AssignedPolicyName", 1), ("PolicyName", 2),
    ("Policy Name", 2), ("PolicyId", null), ("CurrentPolicyId", null), ("PolicyGuid", null),
    ("PolicyPassword", null), ("PolicyToken", null), ("PolicyData", null), ("Name", null)
})
await Check($"Registry policy field classification: {valueName}", () =>
{
    Assert(PolicyRegistryRules.NamePriority(@"HKLM\SOFTWARE\Faronics\Faronics Cloud", valueName) == expected, "Unsafe or missed policy field");
    return Task.CompletedTask;
});

foreach (var branch in new[] { "History", "PolicyHistory", "Templates", "ScheduledPolicies", "Backups", "Default", "Pending", "Previous" })
await Check($"Registry never treats {branch} as the active policy", () =>
{
    var path = @"HKLM\SOFTWARE\Faronics\Cloud\" + branch;
    Assert(PolicyRegistryRules.NamePriority(path, "CurrentPolicyName") is null, "Historical or future policy selected");
    Assert(PolicyRegistryRules.IsPolicyField(path, "CurrentPolicyName"), "Metadata missing from discovery");
    return Task.CompletedTask;
});

await Check("Generic policy names require Cloud context and Group Policy remains metadata only", () =>
{
    Assert(PolicyRegistryRules.NamePriority(@"HKLM\SOFTWARE\Faronics\OtherProduct", "PolicyName") is null, "Another product's policy selected");
    Assert(PolicyRegistryRules.NamePriority(@"HKLM\SOFTWARE\Policies\Faronics\Cloud", "CurrentPolicyName") is null, "Group Policy treated as live Cloud state");
    Assert(PolicyRegistryRules.IsPolicyField(@"HKLM\SOFTWARE\Faronics\Cloud\CurrentPolicy", "Name"), "Unknown policy field not discoverable");
    return Task.CompletedTask;
});

foreach (var value in new[] { "", "\t", "line\nbreak", new string('x', 257), "{df17e344-f4e2-4d0e-bb46-df228311541a}" })
await Check("Invalid registry strings and GUIDs are not policy names", () =>
{
    Assert(PolicyRegistryRules.ReadName(value) is null, "Invalid policy name accepted");
    return Task.CompletedTask;
});

await Check("Registry names preserve spaces and Unicode", () =>
{
    Assert(PolicyRegistryRules.ReadName("  Office 雪 computers  ") == "Office 雪 computers", "Valid policy name changed");
    return Task.CompletedTask;
});

static PolicyRegistryCandidate PolicyCandidate(string name, string view = "Registry64", int priority = 0) =>
    new(name, @"HKLM\SOFTWARE\Faronics\Cloud", view, "CurrentPolicyName", priority);
static PolicyRegistryScan PolicyScan(bool complete, params PolicyRegistryCandidate[] candidates) => new([], [], candidates, [], complete);

await Check("Matching policy names across registry views resolve with their exact source", () =>
{
    var scan = PolicyScan(true, PolicyCandidate("DEPLOYMENT"), PolicyCandidate("deployment", "Registry32"));
    var result = scan.Resolve();
    Assert(result.Name == "DEPLOYMENT" && result.Source.EndsWith(@"CurrentPolicyName (Registry64)"), "Source or name lost");
    Assert(ReferenceEquals(result.Registry, scan), "Registry discovery was not retained");
    return Task.CompletedTask;
});

await Check("Conflicting registry views do not silently choose a policy", () =>
{
    var result = PolicyScan(true, PolicyCandidate("Office"), PolicyCandidate("Lab", "Registry32")).Resolve();
    Assert(result.Name is null && result.Detail.Contains("conflict"), "Conflicting names were treated as certain");
    return Task.CompletedTask;
});

await Check("An explicit current name takes precedence over generic or assigned names", () =>
{
    var result = PolicyScan(true, PolicyCandidate("Generic", priority: 2), PolicyCandidate("Assigned", priority: 1), PolicyCandidate("Current")).Resolve();
    Assert(result.Name == "Current", "Generic metadata replaced an explicit current policy");
    return Task.CompletedTask;
});

await Check("Incomplete registry discovery cannot silently select a partial match", () =>
{
    var result = PolicyScan(false, PolicyCandidate("Office")).Resolve();
    Assert(result.Name is null && result.Detail.Contains("incomplete"), "Partial search reported a verified policy");
    return Task.CompletedTask;
});

await Check("Unavailable policy diagnostics keep checked keys and unknown field metadata", () =>
{
    var scan = new PolicyRegistryScan([new(@"HKLM\SOFTWARE\Faronics", "Registry32", "Read")],
        [new(@"HKLM\SOFTWARE\Faronics\Cloud", "Registry32", "PolicyData", "Binary", "Metadata only")], [], [], true);
    var result = scan.Resolve();
    var json = JsonSerializer.Serialize(result);
    Assert(result.Name is null && json.Contains("PolicyData") && json.Contains("Registry32") && json.Contains("Metadata only"), "Discovery evidence was lost from the report");
    return Task.CompletedTask;
});

foreach (var (xml, expected) in new (string, string?)[]
{
    ("<Policy Name='DEPLOYMENT'><Product><Policy Name='Wrong'/></Product></Policy>", "DEPLOYMENT"),
    ("<Policy Name=' Office 雪 &amp; Lab '/>", "Office 雪 & Lab"),
    ("<Policy><Product><PolicyName>Wrong</PolicyName></Product></Policy>", null),
    ("<History><Policy Name='Old'/></History>", null),
    ("<Policy Name='A'/><Policy Name='B'/>", null),
    ("<Policy Name='A'><Product></Policy>", null),
    ("<Policy Name='A' Name='B'/>", null),
    ("<!DOCTYPE Policy [<!ENTITY name SYSTEM 'file:///C:/Windows/win.ini'>]><Policy Name='&name;'/>", null),
    ("<Policy xmlns='unknown' Name='Wrong'/>", null),
    ("<Policy Name='line&#10;break'/>", null),
    ("<Policy Name='" + new string('x', 257) + "'/>", null),
    ("<Policy Name='{df17e344-f4e2-4d0e-bb46-df228311541a}'/>", null),
    ("<Policy Name='A'>" + new string('x', CloudPolicyCache.MaximumXmlLength) + "</Policy>", null)
})
await Check("Cache XML accepts only a valid root policy name", () =>
{
    Assert(CloudPolicyCache.ParseName(xml) == expected, "Invalid or unrelated policy selected");
    return Task.CompletedTask;
});

await Check("Real SQLite cache reads only the current policy and leaves database unchanged", () =>
{
    using var fixture = new PolicyCacheFixture(("CURRENT_PRODUCTS_POLICY", "<Policy Name='Office 雪'><Product Password='never-export-this'/></Policy>"), ("OLD_POLICY", "<Policy Name='Wrong'/>"), ("Token", "never-export-this"));
    static byte[] HashCache(string path) { using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return System.Security.Cryptography.SHA256.HashData(file); }
    var before = HashCache(fixture.Path);
    var result = CloudPolicyCache.Read(fixture.Path);
    Assert(result.Name == "Office 雪", "Current policy not read");
    Assert(!JsonSerializer.Serialize(result).Contains("never-export-this"), "Raw configuration leaked");
    Assert(before.SequenceEqual(HashCache(fixture.Path)), "Database was modified");
    return Task.CompletedTask;
});

await Check("Duplicate current policy rows remain ambiguous", () =>
{
    using var fixture = new PolicyCacheFixture(("CURRENT_PRODUCTS_POLICY", "<Policy Name='A'/>"), ("CURRENT_PRODUCTS_POLICY", "<Policy Name='B'/>"));
    Assert(CloudPolicyCache.Read(fixture.Path).Name is null, "Duplicate rows silently selected");
    return Task.CompletedTask;
});

foreach (var value in new string?[] { null, "not XML", "<Policy Name='A'>" + new string('x', CloudPolicyCache.MaximumXmlLength) + "</Policy>" })
await Check("Null, corrupt and oversized cached policies stay unavailable", () =>
{
    using var fixture = new PolicyCacheFixture(("CURRENT_PRODUCTS_POLICY", value));
    Assert(CloudPolicyCache.Read(fixture.Path).Name is null, "Invalid cache reported a name");
    return Task.CompletedTask;
});

await Check("Historical cache keys never supply the current policy", () =>
{
    using var fixture = new PolicyCacheFixture(("PREVIOUS_PRODUCTS_POLICY", "<Policy Name='Wrong'/>"));
    Assert(CloudPolicyCache.Read(fixture.Path).Name is null, "Historical policy selected");
    return Task.CompletedTask;
});

await Check("Locked cache queries return promptly", () =>
{
    using var fixture = new PolicyCacheFixture(("CURRENT_PRODUCTS_POLICY", "<Policy Name='A'/>"));
    fixture.Execute("BEGIN EXCLUSIVE");
    var clock = Stopwatch.StartNew();
    Assert(CloudPolicyCache.Read(fixture.Path).Name is null && clock.Elapsed < TimeSpan.FromSeconds(2), "Locked cache blocked or reported success");
    fixture.Execute("ROLLBACK");
    return Task.CompletedTask;
});

await Check("Missing cache is never created and cancellation stops reads", () =>
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
    Assert(CloudPolicyCache.Read(path).Name is null && !File.Exists(path), "Missing database created");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { CloudPolicyCache.Read(path, cancelled.Token); throw new Exception("Cancelled query ran"); }
    catch (OperationCanceledException) { }
    return Task.CompletedTask;
});

await AecChecks.RunAsync(Check);
await AecDiagnosticChecks.RunAsync(Check);
await AecInsightChecks.RunAsync(Check);
await HistoryChecks.RunAsync(Check);
await Check("Control transport closes stdin and preserves bounded stderr", async () =>
{
    var input = await new CommandProcessRunner().RunAsync(dotnet, [assembly, "--fixture", "stdin"]);
    Assert(input.Output == "closed", "Interactive password prompt could wait on input");
    var output = await new CommandProcessRunner().RunAsync(dotnet, [assembly, "--fixture", "flood"]);
    Assert(output.StandardError.Length == 4096 && output.Output.Length == 4096 && output.ExitCode == 0, "Bounded stderr transport failed");
});

Console.WriteLine($"\n{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

sealed class FakeRunner(params ProcessResult[] results) : ICommandRunner
{
    private readonly Queue<ProcessResult> remaining = new(results);
    public List<string[]> Calls { get; } = [];
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments.ToArray());
        return Task.FromResult(remaining.Dequeue());
    }
}
