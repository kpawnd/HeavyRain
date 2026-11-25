using System.Net;
using System.Text;
using System.Text.Json;
using HeavyRain.Core;

/// <summary>Heavy Rain's side of the wiki sign-in, against a scripted stand-in for the wiki's API.</summary>
internal static class WikiSignInChecks
{
    private static readonly Uri Site = new("https://wiki.example.test/");
    private const string StartJson = """{"deviceCode":"secret-device-code-0123456789abcdef","userCode":"BCDF-GHJK","verificationPath":"/link?code=BCDF-GHJK","expiresIn":600,"interval":3}""";
    private const string UserJson = """{"username":"ta.one","name":"TA One","email":"ta.one@example.test","role":"technical_assistant","department":null}""";
    private static readonly DateTimeOffset TokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
    private static readonly string TokenJson = $$"""{"token":"tool-token-value","expiresAt":"{{TokenExpiry:O}}","user":{{UserJson}}}""";
    private static readonly string SessionJson = $$"""{"client":"heavy-rain","expiresAt":"{{TokenExpiry:O}}","user":{{UserJson}}}""";

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Wiki sign-in: only https, or a local address for testing", () =>
        {
            Assert(WikiSignInClient.ParseSite("https://wiki.example.test") == Site && WikiSignInClient.ParseSite("https://wiki.example.test/pages/x") == Site, "https address");
            Assert(WikiSignInClient.ParseSite("http://localhost:8787")?.ToString() == "http://localhost:8787/" && WikiSignInClient.ParseSite("http://127.0.0.1:8787") is not null, "Local address");
            foreach (var bad in new[] { null, "", "wiki.example.test", "http://wiki.example.test", "ftp://wiki.example.test", "https://wiki.example.test/?x=1", "https://wiki.example.test/#a", "file:///C:/x" })
                Assert(WikiSignInClient.ParseSite(bad) is null, $"Accepted {bad}");
            return Task.CompletedTask;
        });

        await check("Wiki sign-in: the PC's name is made acceptable to the wiki", () =>
        {
            Assert(WikiSignInClient.ComputerName("LAB-PC-07") == "LAB-PC-07" && WikiSignInClient.ComputerName("PC\u0007名") == "PC" && WikiSignInClient.ComputerName("\u0001") == "Unknown PC", "Characters");
            Assert(WikiSignInClient.ComputerName(new string('A', 90)).Length == 64, "Length");
            return Task.CompletedTask;
        });

        await check("Wiki sign-in: starting a link sends the client and PC, and reads the code", async () =>
        {
            var wiki = new FakeWiki((HttpStatusCode.OK, StartJson));
            var link = await new WikiSignInClient(wiki.Client, Site).StartAsync("LAB-PC-07");
            Assert(link is { UserCode: "BCDF-GHJK" } && link.VerificationUri.ToString() == "https://wiki.example.test/link?code=BCDF-GHJK" && link.Interval == TimeSpan.FromSeconds(3), "Link: " + link);
            Assert(wiki.Requests.Single() is { Method: "POST", Path: "/api/device-links" } request && request.Body.Contains("\"client\":\"heavy-rain\"") && request.Body.Contains("\"computer\":\"LAB-PC-07\""), "Request: " + wiki.Requests.Single());
            Assert(!link!.ToString().Contains("secret-device-code"), "Device code printed");
        });

        await check("Wiki sign-in: a refused, broken or foreign start gives nothing", async () =>
        {
            foreach (var answer in new[] { (HttpStatusCode.TooManyRequests, """{"error":"too_many_requests"}"""), (HttpStatusCode.OK, "not json"), (HttpStatusCode.OK, "{}"),
                (HttpStatusCode.OK, StartJson.Replace("/link?code=BCDF-GHJK", "https://elsewhere.example/link")),
                (HttpStatusCode.OK, StartJson.Replace("/link?code=BCDF-GHJK", "//elsewhere.example/link")) })
                Assert(await new WikiSignInClient(new FakeWiki(answer).Client, Site).StartAsync("PC") is null, "Accepted " + answer.Item2);
            Assert(await new WikiSignInClient(FakeWiki.Down().Client, Site).StartAsync("PC") is null, "Network failure");
        });

        foreach (var (error, status) in new[] { ("authorization_pending", LinkStatus.Pending), ("slow_down", LinkStatus.Pending), ("access_denied", LinkStatus.Denied),
            ("expired_token", LinkStatus.Expired), ("invalid_grant", LinkStatus.Invalid), ("something_new", LinkStatus.Unreachable) })
        await check($"Wiki sign-in: '{error}' means {status}", async () =>
        {
            var result = await new WikiSignInClient(new FakeWiki((HttpStatusCode.BadRequest, $$"""{"error":"{{error}}"}""")).Client, Site).PollAsync(Link());
            Assert(result.Status == status && result.Session is null, "Got " + result);
        });

        await check("Wiki sign-in: an approval brings the token and who it is, without printing the token", async () =>
        {
            var wiki = new FakeWiki((HttpStatusCode.OK, TokenJson));
            var result = await new WikiSignInClient(wiki.Client, Site).PollAsync(Link());
            Assert(result is { Status: LinkStatus.Approved, Session: { Token: "tool-token-value", User: { Username: "ta.one", RoleLabel: "Technical Assistant", DisplayName: "TA One" } } }, "Result: " + result);
            Assert(result.Session!.ExpiresAt == TokenExpiry, "Expiry");
            Assert(!result.ToString().Contains("tool-token-value") && !result.Session.ToString().Contains("tool-token-value"), "Token printed");
            Assert(wiki.Requests.Single() is { Path: "/api/device-links/token" } poll && poll.Body.Contains("secret-device-code"), "Poll request");
        });

        await check("Wiki sign-in: an approval that can't be read isn't taken as one", async () =>
        {
            foreach (var body in new[] { """{"token":"","expiresAt":"2026-11-25T20:00:00Z","user":{"username":"x","name":"","email":"","role":"member"}}""", """{"token":"t","expiresAt":"soon","user":{"username":"x","name":"","email":"","role":"member"}}""", """{"token":"t","expiresAt":"2026-11-25T20:00:00Z"}""" })
                Assert((await new WikiSignInClient(new FakeWiki((HttpStatusCode.OK, body)).Client, Site).PollAsync(Link())).Status == LinkStatus.Unreachable, "Accepted " + body);
        });

        await check("Wiki sign-in: a returned approval must still be current", async () =>
        {
            var expired = TokenJson.Replace(TokenExpiry.ToString("O"), DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            var wiki = new FakeWiki((HttpStatusCode.OK, expired));
            var result = await new WikiSignInClient(wiki.Client, Site).PollAsync(Link());
            Assert(result is { Status: LinkStatus.Expired, Session: null }, "An expired approval was accepted");
        });

        await check("Wiki sign-in: waiting keeps polling through pending and brief failures, and stops at an answer", async () =>
        {
            var wiki = new FakeWiki((HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""), (HttpStatusCode.BadGateway, "<html>"), (HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""), (HttpStatusCode.OK, TokenJson));
            var waits = new List<TimeSpan>();
            var result = await new WikiSignInClient(wiki.Client, Site).WaitAsync(Link(), (span, _) => { waits.Add(span); return Task.CompletedTask; });
            Assert(result.Status == LinkStatus.Approved && wiki.Requests.Count == 4 && waits.All(w => w == TimeSpan.FromSeconds(3)), $"Got {result.Status} after {wiki.Requests.Count}");
        });

        await check("Wiki sign-in: waiting gives up after five failures in a row, or once the code expires", async () =>
        {
            var down = new FakeWiki(Enumerable.Repeat((HttpStatusCode.ServiceUnavailable, "x"), 9).ToArray());
            var result = await new WikiSignInClient(down.Client, Site).WaitAsync(Link(), (_, _) => Task.CompletedTask);
            Assert(result.Status == LinkStatus.Unreachable && down.Requests.Count == 5, $"Gave up after {down.Requests.Count}");
            var now = DateTimeOffset.Now;
            var expired = await new WikiSignInClient(new FakeWiki().Client, Site).WaitAsync(Link() with { ExpiresAt = now }, (_, _) => Task.CompletedTask, () => now);
            Assert(expired.Status == LinkStatus.Expired, "Expired link polled");
        });

        await check("Wiki sign-in: cancelling the wait stops it", async () =>
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            try { await new WikiSignInClient(new FakeWiki().Client, Site).WaitAsync(Link(), (_, _) => Task.CompletedTask, cancellationToken: cancel.Token); throw new Exception("Not cancelled"); }
            catch (OperationCanceledException) { }
        });

        await check("Wiki sign-in: a current token is verified for the app and user", async () =>
        {
            var session = new WikiSession("tool-token-value", DateTimeOffset.Now.AddHours(1), new("ta.one", "TA One", "", "member", null));
            var ok = new FakeWiki((HttpStatusCode.OK, SessionJson));
            Assert(await new WikiSignInClient(ok.Client, Site).CheckAsync(session) == CheckStatus.Valid, "Valid token");
            Assert(ok.Requests.Single() is { Method: "GET", Path: "/api/device-links/me", Authorization: "Bearer tool-token-value" }, "Request: " + ok.Requests.Single());
            Assert(await new WikiSignInClient(new FakeWiki((HttpStatusCode.Unauthorized, "{}")).Client, Site).CheckAsync(session) == CheckStatus.Revoked, "401");
            Assert(await new WikiSignInClient(new FakeWiki((HttpStatusCode.InternalServerError, "{}")).Client, Site).CheckAsync(session) == CheckStatus.Unreachable, "500");
            Assert(await new WikiSignInClient(FakeWiki.Down().Client, Site).CheckAsync(session) == CheckStatus.Unreachable, "Network");
            Assert(session.IsCurrent(DateTimeOffset.Now) && !session.IsCurrent(session.ExpiresAt), "Expiry");
        });

        await check("Wiki sign-in: a success status alone does not verify the token", async () =>
        {
            var session = new WikiSession("tool-token-value", TokenExpiry, new("ta.one", "TA One", "", "member", null));
            foreach (var body in new[] { "<html>Sign in</html>", "{}", SessionJson.Replace("heavy-rain", "other-client"), SessionJson.Replace("ta.one", "ta.two") })
            {
                var wiki = new FakeWiki((HttpStatusCode.OK, body));
                Assert(await new WikiSignInClient(wiki.Client, Site).CheckAsync(session) == CheckStatus.Unreachable, "An unexpected response verified the session");
            }
        });

        await check("Wiki sign-in: expired tokens cannot authorize changes locally or at the wiki", async () =>
        {
            var expiry = DateTimeOffset.UtcNow.AddMinutes(-1);
            var session = new WikiSession("tool-token-value", expiry, new("ta.one", "TA One", "", "member", null));
            var expired = SessionJson.Replace(TokenExpiry.ToString("O"), expiry.ToString("O"));
            var wiki = new FakeWiki((HttpStatusCode.OK, expired));
            var client = new WikiSignInClient(wiki.Client, Site);
            Assert(await client.CheckAsync(session) == CheckStatus.Expired && wiki.Requests.Count == 0, "An expired local session was checked online");
            Assert(await client.CheckAsync(session with { ExpiresAt = TokenExpiry }) == CheckStatus.Expired && wiki.Requests.Count == 1, "The wiki response expired the session");
        });
    }

    private static DeviceLink Link() => new("secret-device-code-0123456789abcdef", "BCDF-GHJK", new Uri(Site, "link?code=BCDF-GHJK"), DateTimeOffset.Now.AddMinutes(10), TimeSpan.FromSeconds(3));

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed record Seen(string Method, string Path, string Body, string? Authorization);

    /// <summary>Answers each request with the next scripted reply and records what was sent.</summary>
    private sealed class FakeWiki : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> answers;
        private readonly bool down;
        public List<Seen> Requests { get; } = [];
        public HttpClient Client => new(this);

        public FakeWiki(params (HttpStatusCode, string)[] answers) => this.answers = new(answers);
        private FakeWiki(bool down) : this() => this.down = down;
        public static FakeWiki Down() => new(down: true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.ToString()));
            if (down) throw new HttpRequestException("No route to host");
            var (status, body) = answers.Count > 0 ? answers.Dequeue() : (HttpStatusCode.InternalServerError, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
