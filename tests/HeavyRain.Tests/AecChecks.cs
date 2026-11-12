using System.Text.Json;
using HeavyRain.Core;

internal static class AecChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        foreach (var (output, expected) in new[]
        {
            ("Protected", AecState.Enabled), ("Unprotected", AecState.Disabled),
            ("Maintenance Mode", AecState.Maintenance), ("\r\nProtection ON.\r\n", AecState.Enabled),
            ("Status: Unprotected", AecState.Disabled), ("Protection state: Maintenance", AecState.Maintenance),
            ("", AecState.Unknown), ("Protected\nUnprotected", AecState.Unknown),
            ("Not Protected", AecState.Unknown), ("Protection ON.\nFailed", AecState.Unknown),
            ("Invalid AEAdmin Password", AecState.Unknown), ("You do not have appropriate right.", AecState.Unknown),
            ("Current policy: Protected", AecState.Unknown), ("Maintenance Mode set successfully.", AecState.Unknown),
            ("Usage: status | protected | unprotected", AecState.Unknown), ("Protection ON.\nextra unknown line", AecState.Unknown),
            ("Geschützt", AecState.Unknown), ("Unprotected example secret", AecState.Unknown)
        })
        await check($"AEC status parser: {output.Replace('\n', ' ')}", () =>
        {
            Assert(AecClient.ParseStatus(new(0, Output: output)).State == expected, "Unexpected state");
            return Task.CompletedTask;
        });

        foreach (var result in new[] { new ProcessResult(1, Output: "Protected"), new ProcessResult(null, Output: "Protected"), new ProcessResult(0, true, "Protected"), new ProcessResult(0, Output: "Protected", Error: "example secret"), new ProcessResult(0, Output: "Protected", StandardError: "Access denied: example secret") })
        await check("AEC transport or authorization failure never reports protection", () =>
        {
            var parsed = AecClient.ParseStatus(result);
            Assert(parsed.State == AecState.Unknown && !JsonSerializer.Serialize(parsed).Contains("example secret"), "False state or leaked output");
            return Task.CompletedTask;
        });

        await check("AEC status can arrive on stderr without losing its state", () =>
        {
            Assert(AecClient.ParseStatus(new(0, StandardError: "Unprotected")).State == AecState.Disabled, "Stderr was ignored");
            return Task.CompletedTask;
        });

        foreach (var (initial, action, expected, arguments) in new (string, AecAction, string, string[])[]
        {
            ("Unprotected", AecAction.Enable, "Protected", ["protect", "on"]),
            ("Protected", AecAction.Disable, "Unprotected", ["protect", "off"]),
            ("Protected", AecAction.Maintenance, "Maintenance Mode", ["maintenance", "/duration=15"]),
            ("Maintenance Mode", AecAction.Enable, "Protected", ["protect", "on"]),
            ("Maintenance Mode", AecAction.Disable, "Unprotected", ["protect", "off", "/force"])
        })
        await check($"AEC {initial} to {action} verifies both sides of the change", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: initial), new(0), new(0, Output: expected));
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "p a\"ss&word", action, allowMaintenanceDisable: true);
            Assert(result.Outcome == ChangeOutcome.Accepted && result.Status?.State != AecState.Unknown, "Change not verified");
            Assert(runner.Calls.Count == 3 && runner.Calls[0][0] == "status" && runner.Calls[2][0] == "status", "Missing preflight or readback");
            Assert(runner.Calls[1].SequenceEqual(arguments.Append("/pw=p a\"ss&word")), "Wrong command or password token");
            Assert(!JsonSerializer.Serialize(result).Contains("ss&word"), "Password leaked");
        });

        foreach (var output in new[] { "", "Invalid AEAdmin Password", "unexpected format" })
        await check("AEC unknown preflight blocks all changes", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: output));
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable);
            Assert(result.Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 1, "Change sent despite unknown status");
            Assert(runner.Calls[0].SequenceEqual(new[] { "status" }), "Blank password should use Windows account authorization");
        });

        foreach (var (state, action) in new[] { ("Protected", AecAction.Enable), ("Unprotected", AecAction.Disable), ("Maintenance Mode", AecAction.Maintenance) })
        await check("AEC already at target does not send a change or extend maintenance", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: state));
            Assert((await new AecClient(runner).ChangeAsync("AEC.exe", "", action)).Outcome == ChangeOutcome.NotNeeded && runner.Calls.Count == 1, "Duplicate change");
        });

        await check("AEC does not force Maintenance off without explicit confirmation", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: "Maintenance Mode"));
            Assert((await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable)).Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 1, "Unexpected force");
        });

        foreach (var duration in new[] { -1, 0, 1441, int.MaxValue })
        await check($"AEC rejects maintenance duration {duration}", async () =>
        {
            var runner = new FakeRunner();
            Assert((await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Maintenance, duration)).Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 0, "Invalid duration sent");
        });

        foreach (var duration in new[] { 1, 1440 })
        await check($"AEC accepts maintenance duration boundary {duration}", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: "Protected"), new(0), new(0, Output: "Maintenance Mode"));
            Assert((await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Maintenance, duration)).Outcome == ChangeOutcome.Accepted, "Valid duration blocked");
            Assert(runner.Calls[1].SequenceEqual(new[] { "maintenance", $"/duration={duration}" }), "Duration or unwanted lock argument");
        });

        foreach (var password in new[] { "line\nbreak", "null\0byte", new string('x', 256) })
        await check("AEC rejects invalid passwords before starting a process", async () =>
        {
            var runner = new FakeRunner();
            var client = new AecClient(runner);
            Assert((await client.ChangeAsync("AEC.exe", password, AecAction.Disable)).Outcome == ChangeOutcome.Rejected, "Invalid password sent");
            Assert((await client.QueryAsync("AEC.exe", password)).State == AecState.Unknown && runner.Calls.Count == 0, "Invalid query sent");
        });

        foreach (var output in new[] { "", "Protected", "Invalid AEAdmin Password" })
        await check("AEC exit zero is insufficient without matching readback", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: "Protected"), new(0), new(0, Output: output));
            Assert((await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable)).Outcome == ChangeOutcome.Uncertain && runner.Calls.Count == 3, "False success");
        });

        foreach (var response in new[] { new ProcessResult(null, true), new ProcessResult(null, Error: "example secret"), new ProcessResult(null) })
        await check("AEC lost response is uncertain and never retries a change", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: "Protected"), response);
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable);
            Assert(result.Outcome == ChangeOutcome.Uncertain && runner.Calls.Count == 2 && !JsonSerializer.Serialize(result).Contains("example secret"), "Lost response mishandled");
        });

        foreach (var response in new[] { new ProcessResult(7), new ProcessResult(0, Output: "Failed to disable protection."), new ProcessResult(0, StandardError: "Invalid AEAdmin Password example secret") })
        await check("AEC explicit rejection never claims success or leaks response text", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: "Protected"), response);
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable);
            Assert(result.Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 2 && !JsonSerializer.Serialize(result).Contains("example secret"), "Failure mishandled");
        });

        await check("AEC cancellation after preflight sends no mutation", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var runner = new FakeRunner(new ProcessResult(0, Output: "Protected"));
            Assert((await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable, cancellationToken: cancellation.Token)).Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 1, "Cancelled change sent");
        });

        await check("AEC invalid action sends no process", async () =>
        {
            var runner = new FakeRunner();
            Assert((await new AecClient(runner).ChangeAsync("AEC.exe", "", (AecAction)99)).Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 0, "Invalid action sent");
        });

        foreach (var (output, expected) in new (string, string?)[] { ("6.11.2112.730", "6.11.2112.730"), ("Version: 6.11.2112.730", "6.11.2112.730"), ("", null), ("Error 6.11.2112.730", null), ("secret 6.11.2112.730", null) })
        await check("AEC version parsing excludes unrelated output", async () =>
        {
            Assert(await new AecClient(new FakeRunner(new ProcessResult(0, Output: output))).GetVersionAsync("AEC.exe") == expected, "Bad version");
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
