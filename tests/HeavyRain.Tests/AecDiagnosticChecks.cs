using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HeavyRain.Core;

internal static class AecDiagnosticChecks
{
    private const string External = "In CCmdParser::HandleCommandLine(). No command line for External User.";
    private const string FixtureVariable = "HEAVYRAIN_DIAGNOSTIC_TEST";

    public static async Task<int> RunFixtureAsync()
    {
        var mode = Environment.GetEnvironmentVariable(FixtureVariable);
        if (mode == "hang") { await Task.Delay(30000); return 0; }
        if (mode == "external") OutputDebugStringW(External);
        if (mode == "adjacent") OutputDebugStringW("Other message\0" + External);
        if (mode == "conflict")
        {
            OutputDebugStringW(External);
            OutputDebugStringW("In CCmdParser::HandleCommandLine(). Is not EnterpriseVersion().");
        }
        OutputDebugStringW("Private configuration example-secret");
        return 0;
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        foreach (var (message, expected) in new[]
        {
            (External, AecDiagnostic.ExternalUser),
            ("In CCmdParser::HandleCommandLine(). Is not EnterpriseVersion().", AecDiagnostic.UnsupportedEdition),
            ("In CCmdParser::HandleCommandLine(). m_AeConfiguration.Initialize() Failed.", AecDiagnostic.ConfigurationUnavailable),
            ("In CCmdParser::HandleCommandLine(). Password incorrect.", AecDiagnostic.PasswordRejected),
            ("Other message\0" + External, AecDiagnostic.None),
            ("prefix " + External, AecDiagnostic.None),
            (External + "\0Private configuration", AecDiagnostic.ExternalUser),
            ("Private configuration example-secret", AecDiagnostic.None)
        })
        await check("AEC diagnostics match only known complete messages before the terminator", () =>
        {
            Assert(AecDiagnostics.Classify(message) == expected, "Wrong diagnostic");
            Assert(!JsonSerializer.Serialize(AecDiagnostics.Describe(expected)).Contains("example-secret"), "Raw diagnostics leaked");
            return Task.CompletedTask;
        });

        await check("AEC silent success receives a specific access explanation without a protection guess", async () =>
        {
            var diagnostics = new DiagnosticReader(AecDiagnostic.ExternalUser);
            var client = new AecClient(new FakeRunner(new ProcessResult(0)), diagnostics);
            var result = await client.QueryAsync("AEC.exe", "example-secret");
            Assert(result.State == AecState.Unknown && result.Diagnostic == AecDiagnostic.ExternalUser && result.Detail.Contains("External User"), "Missing authorization reason");
            Assert(diagnostics.Calls == 1 && !JsonSerializer.Serialize(result).Contains("example-secret"), "Diagnostics or secret handling failed");
        });

        await check("AEC rejected Windows account sends no protection change", async () =>
        {
            var runner = new FakeRunner(new ProcessResult(0));
            var result = await new AecClient(runner, new DiagnosticReader(AecDiagnostic.ExternalUser)).ChangeAsync("AEC.exe", "example-secret", AecAction.Disable);
            Assert(result.Outcome == ChangeOutcome.Rejected && runner.Calls.Count == 1 && result.Detail.Contains("account authorization"), "Authorization requirement was bypassed");
        });

        await check("Password-free diagnostics cannot reject a supplied password", async () =>
        {
            var client = new AecClient(new FakeRunner(new ProcessResult(0)), new DiagnosticReader(AecDiagnostic.PasswordRejected));
            var result = await client.QueryAsync("AEC.exe", "example-secret");
            Assert(result.Diagnostic == AecDiagnostic.PasswordRequired && !result.Detail.Contains("supplied"), "Password-free probe judged an unseen password");
        });

        foreach (var result in new[] { new ProcessResult(0, Output: "Protected"), new ProcessResult(0, StandardError: "denied"), new ProcessResult(0, TimedOut: true), new ProcessResult(4), new ProcessResult(null, Error: "blocked") })
        await check("AEC diagnostics are reserved for successful empty status responses", async () =>
        {
            var diagnostics = new DiagnosticReader(AecDiagnostic.ExternalUser);
            await new AecClient(new FakeRunner(result), diagnostics).QueryAsync("AEC.exe");
            Assert(diagnostics.Calls == 0, "Unnecessary diagnostic process");
        });

        await check("AEC diagnostics do not retry mutations with empty acknowledgement", async () =>
        {
            var diagnostics = new DiagnosticReader(AecDiagnostic.ExternalUser);
            var runner = new FakeRunner(new(0, Output: "Protected"), new(0), new(0, Output: "Unprotected"));
            var result = await new AecClient(runner, diagnostics).ChangeAsync("AEC.exe", "", AecAction.Disable);
            Assert(result.Outcome == ChangeOutcome.Accepted && diagnostics.Calls == 0 && runner.Calls.Count == 3, "Mutation was diagnosed or retried");
        });

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return;
        var executable = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
        var previous = Environment.GetEnvironmentVariable(FixtureVariable);
        try
        {
            foreach (var (mode, expected) in new[] { ("external", AecDiagnostic.ExternalUser), ("adjacent", AecDiagnostic.None), ("conflict", AecDiagnostic.None) })
            await check($"Native AEC diagnostic transport: {mode}", async () =>
            {
                Environment.SetEnvironmentVariable(FixtureVariable, mode);
                var probe = new AecDiagnosticProbe();
                Assert(await probe.DiagnoseAsync(executable) == expected, "Unexpected native diagnostic");
            });
            await check("Native AEC diagnostic cache avoids repeated status processes", async () =>
            {
                Environment.SetEnvironmentVariable(FixtureVariable, "external");
                var probe = new AecDiagnosticProbe();
                Assert(await probe.DiagnoseAsync(executable) == AecDiagnostic.ExternalUser, "Initial diagnosis missing");
                Environment.SetEnvironmentVariable(FixtureVariable, "hang");
                var clock = Stopwatch.StartNew();
                Assert(await probe.DiagnoseAsync(executable) == AecDiagnostic.ExternalUser && clock.Elapsed < TimeSpan.FromMilliseconds(100), "Cached diagnosis was not reused");
            });
            await check("Native AEC diagnostic timeout terminates its own status process", async () =>
            {
                Environment.SetEnvironmentVariable(FixtureVariable, "hang");
                var clock = Stopwatch.StartNew();
                Assert(await new AecDiagnosticProbe(TimeSpan.FromMilliseconds(350)).DiagnoseAsync(executable) == AecDiagnostic.None && clock.Elapsed < TimeSpan.FromSeconds(3), "Diagnostic timeout did not return promptly");
            });
            await check("Native AEC diagnostic cancellation stops its status process", async () =>
            {
                Environment.SetEnvironmentVariable(FixtureVariable, "hang");
                using var cancellation = new CancellationTokenSource(350);
                var clock = Stopwatch.StartNew();
                try { await new AecDiagnosticProbe().DiagnoseAsync(executable, cancellation.Token); throw new Exception("Cancellation was ignored"); }
                catch (OperationCanceledException) { Assert(clock.Elapsed < TimeSpan.FromSeconds(3), "Cancellation was too slow"); }
            });
        }
        finally { Environment.SetEnvironmentVariable(FixtureVariable, previous); }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class DiagnosticReader(AecDiagnostic result) : IAecDiagnosticReader
    {
        public int Calls { get; private set; }
        public Task<AecDiagnostic> DiagnoseAsync(string executable, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(result); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void OutputDebugStringW(string message);
}
