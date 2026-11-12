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

        const string administratorPrompt = "\r\r\nEnter AE Administrator Password : \r\r\nInvalid AE Administrator Password\r\r\n";
        foreach (var (password, expected) in new[] { ("", AecDiagnostic.PasswordRequired), ("example secret", AecDiagnostic.PasswordRejected) })
        await check($"AEC distinguishes missing and supplied passwords: {expected}", async () =>
        {
            var result = await new AecClient(new FakeRunner(new ProcessResult(0, Output: administratorPrompt))).QueryAsync("AEC.exe", password);
            Assert(result.Diagnostic == expected && result.State == AecState.Unknown, "Password error was misclassified");
            Assert(result.Detail.Contains("AE Administrator password") && !result.Detail.Contains("account access"), "Wrong role or generic account error");
            Assert(!JsonSerializer.Serialize(result).Contains("example secret"), "Secret leaked");
        });

        foreach (var (output, role) in new[]
        {
            ("Invalid AEAdmin Password", "AE Administrator password"),
            ("Enter AE Trusted User Password :", "AE Trusted User password"),
            ("Invalid AETrustedUser Password", "AE Trusted User password"),
            ("Password not valid", "Anti-Executable password")
        })
        await check("AEC password errors on stderr retain the requested role", () =>
        {
            var result = AecClient.ParseStatus(new(3, StandardError: output), passwordSupplied: true);
            Assert(result.Diagnostic == AecDiagnostic.PasswordRejected && result.Detail.Contains(role) && result.State == AecState.Unknown, "Password role or nonzero exit lost");
            return Task.CompletedTask;
        });

        await check("AEC missing password blocks mutations with a specific explanation", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: administratorPrompt));
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable);
            Assert(result.Outcome == ChangeOutcome.Rejected && result.Status?.Diagnostic == AecDiagnostic.PasswordRequired && runner.Calls.Count == 1, "Change sent without required password");
        });

        await check("AEC password rejected during a mutation is reported without retry", async () =>
        {
            var runner = new FakeRunner(new(0, Output: "Protected"), new(0, Output: "Invalid AE Administrator Password"));
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "example secret", AecAction.Disable);
            Assert(result.Outcome == ChangeOutcome.Rejected && result.Status?.Diagnostic == AecDiagnostic.PasswordRejected && runner.Calls.Count == 2, "Password rejection lost or request retried");
            Assert(!JsonSerializer.Serialize(result).Contains("example secret"), "Password leaked");
        });

        await check("AEC transport failure takes precedence over partial password output", () =>
        {
            var result = AecClient.ParseStatus(new(null, TimedOut: true, Output: administratorPrompt));
            Assert(result.State == AecState.Unknown && result.Diagnostic == AecDiagnostic.None, "Partial failed query supplied an authentication verdict");
            return Task.CompletedTask;
        });

        foreach (var (initial, action, expected, arguments) in new (string, AecAction, string, string[])[]
        {
            ("Unprotected", AecAction.Enable, "Protected", ["protect", "on"]),
            ("Protected", AecAction.Disable, "Unprotected", ["protect", "off"]),
            ("Protected", AecAction.Maintenance, "Maintenance Mode", ["maintenance", "/duration=60"]),
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

        await check("AEC maintenance always requests one hour without locking input", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: "Protected"), new(0), new(0, Output: "Maintenance Mode"));
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Maintenance);
            Assert(result.Outcome == ChangeOutcome.Accepted && result.Detail.Contains("One hour"), "One-hour request not confirmed");
            Assert(runner.Calls[1].SequenceEqual(new[] { "maintenance", "/duration=60" }), "Wrong duration or unwanted lock argument");
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
            var runner = new FakeRunner([new(0, Output: "Protected"), new(0), .. Enumerable.Repeat(new ProcessResult(0, Output: output), 6)]);
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable);
            Assert(result.Outcome == ChangeOutcome.Uncertain && runner.Calls.Count == (output.Contains("Password") ? 3 : 8), "False success or unbounded verification");
            Assert(runner.Calls.Count(call => call[0] == "protect") == 1 && result.Detail.Contains("Disable was requested"), "Duplicate change or unspecified action");
            if (output == "Protected") Assert(result.Detail.Contains("still reports Protected"), "Observed mismatch was hidden");
        });

        foreach (var (initial, action, final) in new[]
        {
            ("Unprotected", AecAction.Enable, "Protected"),
            ("Protected", AecAction.Disable, "Unprotected"),
            ("Protected", AecAction.Maintenance, "Maintenance")
        })
        await check($"AEC {action} tolerates an empty response and delayed state update", async () =>
        {
            var runner = new FakeRunner(new(0, Output: initial), new(0), new(0), new(0, Output: initial), new(0, Output: final));
            var progress = new List<(int Attempt, AecStatusResult Status)>();
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "test-password", action, verificationProgress: (attempt, status) => progress.Add((attempt, status)));
            Assert(result.Outcome == ChangeOutcome.Accepted && runner.Calls.Count == 5, "Delayed change was not verified");
            Assert(runner.Calls.Count(call => call[0] != "status") == 1, "Mutation was retried");
            Assert(runner.Calls.All(call => call[^1] == "/pw=test-password"), "Authentication was lost during verification");
            Assert(progress.Count == 3 && progress.Select(item => item.Attempt).SequenceEqual(new[] { 1, 2, 3 }) && progress[0].Status.State == AecState.Unknown && progress[^1].Status.State == result.Status?.State, "Verification progress was missing or inaccurate");
            Assert(!JsonSerializer.Serialize(progress.Select(item => item.Status)).Contains("test-password"), "Progress exposed the password");
        });

        await check("AEC verification cancellation preserves uncertainty without retrying the change", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var runner = new VerificationRunner(cancellation);
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable, cancellationToken: cancellation.Token);
            Assert(result.Outcome == ChangeOutcome.Uncertain && result.Status?.State == AecState.Unknown && runner.Calls == 3, "Cancelled verification lost uncertainty or retried");
        });

        await check("AEC verification exception does not lose the outcome of an already sent change", async () =>
        {
            var runner = new VerificationRunner(throwOnReadback: true);
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable);
            Assert(result.Outcome == ChangeOutcome.Uncertain && runner.Calls == 3 && !JsonSerializer.Serialize(result).Contains("sensitive failure"), "Exception leaked or change was retried");
        });

        await check("AEC verification has a deadline even when the status process hangs", async () =>
        {
            var runner = new VerificationRunner();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await new AecClient(runner).ChangeAsync("AEC.exe", "", AecAction.Disable);
            Assert(result.Outcome == ChangeOutcome.Uncertain && runner.Calls == 3 && clock.Elapsed < TimeSpan.FromSeconds(13), "Verification did not stop at its deadline");
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

        foreach (var (code, expected) in new (int?, AecState?)[] { (1105, AecState.Enabled), (1100, null), (1101, null), (1104, null), (null, null), (0, null), (2001, null) })
        await check($"Provider protection code {code?.ToString() ?? "null"} maps only when confirmed", () =>
        {
            Assert(AecProvider.MapProtection(code) == expected, "Wrong protection mapping");
            return Task.CompletedTask;
        });

        foreach (var (json, protection, present) in new (string, int?, bool)[]
        {
            ("{\"present\":true,\"Protection\":1105,\"DriverStatus\":\"Installed\",\"StatusFlags\":1073807556}", 1105, true),
            ("{\"present\":true,\"Protection\":1100,\"DriverStatus\":\"Installed\"}", 1100, true),
            ("[{\"present\":true,\"Protection\":1105}]", 1105, true)
        })
        await check("Provider JSON yields the reported protection code", () =>
        {
            var reading = AecProvider.ParseProviderJson(json);
            Assert(reading is not null && reading.Protection == protection && reading.Present == present, "Reading not parsed");
            return Task.CompletedTask;
        });

        await check("Provider present-false marks the class empty without a code", () =>
        {
            var reading = AecProvider.ParseProviderJson("{\"present\":false}");
            Assert(reading is { Present: false, Protection: null }, "Empty class mishandled");
            return Task.CompletedTask;
        });

        foreach (var json in new[] { "", "   ", "{\"error\":true}", "not json", "null", "{\"Protection\":\"text\"}" })
        await check("Unreadable provider JSON is null or codeless, never a false code", () =>
        {
            var reading = AecProvider.ParseProviderJson(json);
            Assert(reading is null || AecProvider.MapProtection(reading.Protection) is null, "Fabricated a protection state");
            return Task.CompletedTask;
        });

        var enabledProvider = new AecProviderReading(1105, "Installed");
        foreach (var (cliState, cliDetail) in new[] { (AecState.Disabled, "Anti-Executable execution protection is off."), (AecState.Unknown, "AEC returned no readable status.") })
        await check($"Provider 1105 overrides a command line that reports {cliState}", () =>
        {
            var reconciled = AecProvider.Reconcile(new(cliState, cliDetail, 0), enabledProvider);
            Assert(reconciled.State == AecState.Enabled && reconciled.Detail.Contains("Faronics provider"), "False negative not corrected");
            Assert(cliState != AecState.Unknown ? reconciled.Detail.Contains("overriding") : reconciled.Detail.Contains("command line returned"), "Override wording lost");
            return Task.CompletedTask;
        });

        await check("Provider agreement keeps the richer command-line detail", () =>
        {
            var cli = new AecStatusResult(AecState.Enabled, "Anti-Executable is enforcing its execution rules.", 0);
            Assert(ReferenceEquals(AecProvider.Reconcile(cli, enabledProvider), cli), "Agreement rewrote the result");
            return Task.CompletedTask;
        });

        foreach (var reading in new AecProviderReading?[] { null, new(1100, "Installed"), new(null, "Installed"), new(1105, "Installed", Present: false) })
        await check("An unconfirmed or absent provider never changes the command-line verdict", () =>
        {
            var cli = new AecStatusResult(AecState.Disabled, "Anti-Executable execution protection is off.", 0);
            Assert(ReferenceEquals(AecProvider.Reconcile(cli, reading), cli), "Unconfirmed reading altered the state");
            return Task.CompletedTask;
        });

        await check("Provider read failure leaves the command-line status in place", async () =>
        {
            var client = new AecClient(new FakeRunner(new ProcessResult(0, Output: "Unprotected")), providerReader: new ThrowingProviderReader());
            Assert((await client.QueryAsync("AEC.exe")).State == AecState.Disabled, "Provider failure changed the verdict");
        });

        await check("Status query trusts the provider over a false Unprotected", async () =>
        {
            var client = new AecClient(new FakeRunner(new ProcessResult(0, Output: "Unprotected")), providerReader: new FakeProviderReader(enabledProvider));
            var result = await client.QueryAsync("AEC.exe");
            Assert(result.State == AecState.Enabled && result.ExitCode == 0, "Provider did not correct the status");
        });

        await check("Enable verifies once the provider reports protection even if AEC keeps saying Unprotected", async () =>
        {
            var runner = new FakeRunner(new(0, Output: "Unprotected"), new(0), new(0, Output: "Unprotected"), new(0, Output: "Unprotected"));
            var provider = new SequencedProviderReader(null, null, enabledProvider);
            var result = await new AecClient(runner, providerReader: provider).ChangeAsync("AEC.exe", "pw", AecAction.Enable);
            Assert(result.Outcome == ChangeOutcome.Accepted && result.Status?.State == AecState.Enabled, "Change not verified through the provider");
            Assert(runner.Calls.Count(call => call[0] == "protect") == 1, "Change was retried");
        });

        await check("Provider reader runs a read-only CIM query and parses its JSON", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0, Output: "{\"present\":true,\"Protection\":1105,\"DriverStatus\":\"Installed\"}"));
            var reading = await new AecProviderReader(runner, "powershell.exe").ReadAsync();
            Assert(reading?.Protection == 1105, "Reader did not parse the query output");
            Assert(runner.Calls.Count == 1 && runner.Calls[0][0] == "-NoProfile" && runner.Calls[0].Contains("-NonInteractive"), "Query was not read-only");
            Assert(runner.Calls[0][^1].Contains("Get-CimInstance") && runner.Calls[0][^1].Contains("root/Faronics"), "Wrong provider query");
        });

        foreach (var response in new[] { new ProcessResult(1, Output: "{\"Protection\":1105}"), new ProcessResult(null, TimedOut: true), new ProcessResult(null, Error: "denied") })
        await check("Provider reader reports nothing when the query transport fails", async () =>
        {
            Assert(await new AecProviderReader(new FakeRunner(response), "powershell.exe").ReadAsync() is null, "Failed query produced a reading");
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class FakeProviderReader(AecProviderReading? reading) : IAecProviderReader
    {
        public Task<AecProviderReading?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(reading);
    }

    private sealed class ThrowingProviderReader : IAecProviderReader
    {
        public Task<AecProviderReading?> ReadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("provider failure");
    }

    private sealed class SequencedProviderReader(params AecProviderReading?[] readings) : IAecProviderReader
    {
        private int index;
        public Task<AecProviderReading?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(readings[Math.Min(index++, readings.Length - 1)]);
    }

    private sealed class VerificationRunner(CancellationTokenSource? cancellation = null, bool throwOnReadback = false) : ICommandRunner
    {
        public int Calls { get; private set; }
        public async Task<ProcessResult> RunAsync(string path, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1) return new(0, Output: "Protected");
            if (Calls == 2) return new(0);
            if (throwOnReadback) throw new IOException("sensitive failure");
            if (cancellation is not null) { cancellation.Cancel(); return new(0); }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new Exception("Unreachable");
        }
    }
}
