using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HeavyRain.Core;

if (args.FirstOrDefault() == "--fixture")
{
    switch (args[1])
    {
        case "echo": Console.Write(JsonSerializer.Serialize(args.Skip(2))); break;
        case "flood":
            for (int i = 0; i < 100; i++) { Console.Write(new string('x', 16384)); Console.Error.Write(new string('y', 16384)); }
            break;
        case "hang": await Task.Delay(30000); break;
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
    (BootTarget.Frozen, false, "/FREEZENEXTBOOT", 0), (BootTarget.Thawed, false, "/THAWNEXTBOOT", 1),
    (BootTarget.Frozen, true, "/BOOTFROZEN", 0), (BootTarget.Thawed, true, "/BOOTTHAWED", 1) })
await Check($"Correct transition {command} with fresh preflight", async () =>
{
    var runner = new FakeRunner(new(initial), new(0));
    var result = await new DfcClient(runner).ChangeAsync("DFC.exe", "example secret", target, restart);
    Assert(result.Outcome == ChangeOutcome.Accepted, "Change not accepted");
    Assert(runner.Calls.Count == 2 && runner.Calls[0][1] == "/ISFROZEN", "Preflight missing");
    Assert(runner.Calls[1].SequenceEqual(new[] { "example secret", command }), "Wrong arguments");
    Assert(!result.Detail.Contains("example secret"), "Password leaked");
});

foreach (var password in new[] { "", new string('x', 64), "line\nbreak", "nul\0char" })
await Check("Invalid password never starts a process", async () =>
{
    var runner = new FakeRunner();
    var result = await new DfcClient(runner).ChangeAsync("DFC.exe", password, BootTarget.Frozen, false);
    Assert(result.Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 0, "Invalid request executed");
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
    var result = await new DfcProcessRunner().RunAsync(dotnet, new[] { assembly, "--fixture", "echo" }.Concat(samples).ToArray());
    Assert(result.ExitCode == 0, "Child failed");
    Assert(JsonSerializer.Deserialize<string[]>(result.Output)!.SequenceEqual(samples), "Arguments changed");
});
await Check("Real process drains stdout and stderr without deadlock or unbounded capture", async () =>
{
    var result = await new DfcProcessRunner().RunAsync(dotnet, [assembly, "--fixture", "flood"]);
    Assert(result.ExitCode == 0 && result.Output.Length == 4096, "Drain failed");
});
await Check("Real process timeout is bounded", async () =>
{
    var sw = Stopwatch.StartNew();
    var result = await new DfcProcessRunner(TimeSpan.FromMilliseconds(300)).RunAsync(dotnet, [assembly, "--fixture", "hang"]);
    Assert(result.TimedOut && result.ExitCode is null && sw.Elapsed < TimeSpan.FromSeconds(5), "Timeout failed");
});
await Check("Real exit 1 survives transport", async () =>
    Assert((await new DfcProcessRunner().RunAsync(dotnet, [assembly, "--fixture", "exit", "1"])).ExitCode == 1, "Boolean return lost"));
await Check("Missing executable is an error, never thawed", async () =>
    Assert((await new DfcProcessRunner().RunAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), ["get", "/ISFROZEN"])).Error is not null, "Missing file not rejected"));
await Check("Cancellation before start never reports success", async () =>
{
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    var result = await new DfcProcessRunner().RunAsync(dotnet, [assembly, "--fixture", "echo"], cancel.Token);
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

Console.WriteLine($"\n{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

sealed class FakeRunner(params ProcessResult[] results) : IDfcRunner
{
    private readonly Queue<ProcessResult> remaining = new(results);
    public List<string[]> Calls { get; } = [];
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments.ToArray());
        return Task.FromResult(remaining.Dequeue());
    }
}
