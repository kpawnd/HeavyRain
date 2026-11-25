using System.Net;
using System.Text.Json;
using HeavyRain.Core;

internal static class WikiActionAuditChecks
{
    private static readonly Uri Wiki = new("https://wiki.example.test/");
    private static readonly Guid RequestId = Guid.Parse("7480a19b-d42e-4e47-8c8d-3dd28c7190d4");
    private static readonly WikiSession Session = new("test-only-bearer", DateTimeOffset.UtcNow.AddHours(1),
        new WikiUser("test-user", "Test User", "test@example.test", "technical_assistant", null));

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        foreach (var (action, wireValue) in new[]
        {
            (WikiProtectionAction.Freeze, "freeze"), (WikiProtectionAction.Thaw, "thaw"),
            (WikiProtectionAction.EnableAntiExecutable, "anti_executable_enable"),
            (WikiProtectionAction.DisableAntiExecutable, "anti_executable_disable"),
            (WikiProtectionAction.StartMaintenance, "anti_executable_maintenance"),
            (WikiProtectionAction.AllowFile, "anti_executable_allow")
        })
        {
            await check($"Wiki action audit sends {wireValue} with only the request fields", async () =>
            {
                var handler = new AuditHandler(() => Response(HttpStatusCode.OK));
                using var http = new HttpClient(handler);
                var restart = action == WikiProtectionAction.Freeze;
                var result = await new WikiActionAuditClient(http, Wiki).RecordRequestAsync(Session, RequestId, " LAB-PC\t ", action, restart);
                var call = handler.Calls.Single();
                Assert(result == WikiAuditStatus.Recorded && call.Path == "/api/device-links/actions", "Request was not acknowledged");
                Assert(call.Method == HttpMethod.Post && call.Authorization == "Bearer test-only-bearer", "Wrong method or authentication");
                using var json = JsonDocument.Parse(call.Body);
                var body = json.RootElement;
                Assert(body.EnumerateObject().Count() == 4 && body.GetProperty("requestId").GetGuid() == RequestId
                    && body.GetProperty("computer").GetString() == "LAB-PC" && body.GetProperty("action").GetString() == wireValue
                    && body.GetProperty("restart").GetBoolean() == restart, "Malformed or excessive request data");
            });
        }

        foreach (var (outcome, wireValue) in new[]
        {
            (ChangeOutcome.Accepted, "accepted"), (ChangeOutcome.Rejected, "rejected"),
            (ChangeOutcome.Uncertain, "uncertain"), (ChangeOutcome.NotNeeded, "not_needed")
        })
        {
            await check($"Wiki action audit correlates the {wireValue} outcome without sending local details", async () =>
            {
                var handler = new AuditHandler(() => Response(HttpStatusCode.OK));
                using var http = new HttpClient(handler);
                var result = await new WikiActionAuditClient(http, Wiki).RecordOutcomeAsync(Session, RequestId, outcome);
                var call = handler.Calls.Single();
                Assert(result == WikiAuditStatus.Recorded && call.Path == $"/api/device-links/actions/{RequestId:D}", "Wrong outcome request");
                using var json = JsonDocument.Parse(call.Body);
                Assert(json.RootElement.EnumerateObject().Count() == 1 && json.RootElement.GetProperty("outcome").GetString() == wireValue,
                    "Malformed or excessive outcome data");
            });
        }

        await check("Wiki action audit retries a lost response using the identical request ID and payload", async () =>
        {
            var handler = new AuditHandler(() => throw new HttpRequestException(), () => Response(HttpStatusCode.OK));
            using var http = new HttpClient(handler);
            var audit = new WikiActionAuditClient(http, Wiki);
            var result = await audit.RecordRequestAsync(Session, RequestId, "LAB-PC", WikiProtectionAction.Freeze, true);
            Assert(result == WikiAuditStatus.Recorded && handler.Calls.Count == 2 && handler.Calls[0] == handler.Calls[1], "Retry changed the request");
        });

        foreach (var (code, content, expected, sends) in new[]
        {
            (HttpStatusCode.Unauthorized, "{}", WikiAuditStatus.SignInRequired, 1),
            (HttpStatusCode.Forbidden, "{}", WikiAuditStatus.SignInRequired, 1),
            (HttpStatusCode.NotFound, "{}", WikiAuditStatus.Unavailable, 1),
            (HttpStatusCode.Conflict, "{}", WikiAuditStatus.Unavailable, 1),
            (HttpStatusCode.BadGateway, "{}", WikiAuditStatus.Unavailable, 2),
            (HttpStatusCode.OK, "<html>Sign in</html>", WikiAuditStatus.Unavailable, 1),
            (HttpStatusCode.OK, "{\"ok\":false}", WikiAuditStatus.Unavailable, 1)
        })
        {
            await check($"Wiki action audit requires a real acknowledgment ({(int)code}, {expected})", async () =>
            {
                var handler = new AuditHandler(() => Response(code, content), () => Response(code, content));
                using var http = new HttpClient(handler);
                var result = await new WikiActionAuditClient(http, Wiki).RecordOutcomeAsync(Session, RequestId, ChangeOutcome.Accepted);
                Assert(result == expected && handler.Calls.Count == sends, "Invalid response was acknowledged or retried incorrectly");
            });
        }

        await check("Wiki action audit honors cancellation before sending", async () =>
        {
            var handler = new AuditHandler();
            using var http = new HttpClient(handler);
            try
            {
                await new WikiActionAuditClient(http, Wiki).RecordOutcomeAsync(Session, RequestId, ChangeOutcome.Accepted, new CancellationToken(true));
                throw new Exception("Caller cancellation was ignored");
            }
            catch (OperationCanceledException) { }
            Assert(handler.Calls.Count == 0, "A cancelled call reached the wiki");
        });

        foreach (var allowed in new[] { true, false })
        {
            await check($"Deep Freeze waits for wiki outcome before restart (acknowledged: {allowed})", async () =>
            {
                var runner = new RestartRunner(new(0), new(0), new(0));
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = new DfcClient(runner, "shutdown.exe").ChangeAsync("DFC.exe", "test-password", BootTarget.Frozen, true,
                    beforeRestart: () => { entered.SetResult(); return ack.Task; });
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert(runner.Commands.SequenceEqual(new[] { "/ISFROZEN", "/FREEZENEXTBOOT" }), "Restart occurred before the outcome was recorded");
                ack.SetResult(allowed);
                var result = await task;
                Assert(result.Outcome == ChangeOutcome.Accepted && result.RestartFailed == !allowed, "An accepted next-boot result was lost");
                Assert(runner.Commands.Contains("shutdown.exe") == allowed, "Restart launched without the required acknowledgment");
            });
        }

        await check("Deep Freeze never invokes the restart gate for rejected or deferred commands", async () =>
        {
            foreach (var (code, restart) in new[] { (5, true), (0, false) })
            {
                var runner = new RestartRunner(new(0), new(code));
                var result = await new DfcClient(runner, "shutdown.exe").ChangeAsync("DFC.exe", "test-password", BootTarget.Frozen, restart,
                    beforeRestart: () => throw new Exception("Restart gate invoked without an accepted immediate restart"));
                Assert(result.Outcome == (code == 0 ? ChangeOutcome.Accepted : ChangeOutcome.Rejected), "Incorrect vendor result");
            }
        });
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string content = "{\"ok\":true}") => new(status)
        { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") };

    private static void Assert(bool condition, string detail)
    {
        if (!condition) throw new Exception(detail);
    }

    private sealed record AuditCall(HttpMethod Method, string Path, string? Authorization, string Body);

    private sealed class AuditHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> remaining = new(responses);
        public List<AuditCall> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(new(request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return remaining.Dequeue()();
        }
    }

    private sealed class RestartRunner(params ProcessResult[] results) : ICommandRunner
    {
        private readonly Queue<ProcessResult> remaining = new(results);
        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Commands.Add(executable == "DFC.exe" ? arguments[1] : executable);
            return Task.FromResult(remaining.Dequeue());
        }
    }
}
