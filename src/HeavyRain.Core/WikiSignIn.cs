using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeavyRain.Core;

/// <summary>Who signed in, as the wiki reports it.</summary>
public sealed record WikiUser(string Username, string Name, string Email, string Role, string? Department)
{
    /// <summary>The wiki's own labels for its roles.</summary>
    public string RoleLabel => Role switch
    {
        "technical_assistant" => "Technical Assistant",
        "super_admin" => "Super Admin",
        "member" => "Member",
        "candidate" => "Candidate",
        "trainee" => "Trainee",
        "pending" => "Pending Approval",
        _ => Role
    };

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Username : Name;
}

/// <summary>A sign-in the wiki approved. The token is kept in memory only and never shown, logged or exported.</summary>
public sealed record WikiSession(string Token, DateTimeOffset ExpiresAt, WikiUser User)
{
    public bool IsCurrent(DateTimeOffset now) => now < ExpiresAt;
    public override string ToString() => $"WikiSession {{ User = {User.Username}, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>A sign-in started on the wiki, waiting for someone to approve its code.</summary>
public sealed record DeviceLink(string DeviceCode, string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt, TimeSpan Interval)
{
    public override string ToString() => $"DeviceLink {{ UserCode = {UserCode}, ExpiresAt = {ExpiresAt:O} }}";
}

public enum LinkStatus { Pending, Approved, Denied, Expired, Invalid, Unreachable }

public sealed record LinkResult(LinkStatus Status, WikiSession? Session = null, string? Problem = null);

public enum CheckStatus { Valid, Revoked, Unreachable, Expired }

/// <summary>
/// Signs Heavy Rain in by borrowing the wiki's own Microsoft sign-in, so nothing is registered with
/// Entra for Heavy Rain. Heavy Rain starts a link and shows its code; a person approves the code on
/// the wiki while signed in there; Heavy Rain then collects a token. This is the device sign-in
/// pattern of RFC 8628, implemented by the wiki's /api/device-links routes.
/// </summary>
public sealed class WikiSignInClient(HttpClient http, Uri site)
{
    public const string ClientName = "heavy-rain";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Uri Site { get; } = site;

    /// <summary>
    /// The wiki address from configuration, or null when it isn't usable. Only https is accepted, except
    /// a local address for testing.
    /// </summary>
    public static Uri? ParseSite(string? text)
    {
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        var local = uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !(local && uri.Scheme == Uri.UriSchemeHttp)) return null;
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    /// <summary>The PC's name as the wiki accepts it: printable characters, 64 at most.</summary>
    public static string ComputerName(string name)
    {
        var printable = new string(name.Where(c => c is >= ' ' and <= '~').ToArray()).Trim();
        return printable.Length == 0 ? "Unknown PC" : printable.Length > 64 ? printable[..64] : printable;
    }

    public async Task<DeviceLink?> StartAsync(string computer, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(new Uri(Site, "api/device-links"), new { client = ClientName, computer = ComputerName(computer) }, Json, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadFromJsonAsync<StartBody>(Json, cancellationToken);
            if (body is not { DeviceCode.Length: > 0, UserCode.Length: > 0, VerificationPath: { } path } || !path.StartsWith('/') ||
                !Uri.TryCreate(Site, path, out var verificationUri) || verificationUri.GetLeftPart(UriPartial.Authority) != Site.GetLeftPart(UriPartial.Authority)) return null;
            return new(body.DeviceCode, body.UserCode, verificationUri, DateTimeOffset.Now + TimeSpan.FromSeconds(Math.Clamp(body.ExpiresIn, 30, 3600)),
                TimeSpan.FromSeconds(Math.Clamp(body.Interval, 1, 30)));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException && !cancellationToken.IsCancellationRequested) { return null; }
    }

    /// <summary>One check on whether the code has been approved.</summary>
    public async Task<LinkResult> PollAsync(DeviceLink link, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(new Uri(Site, "api/device-links/token"), new { deviceCode = link.DeviceCode }, Json, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<TokenBody>(Json, cancellationToken);
                if (body is not { Token.Length: > 0, User: { Username.Length: > 0 } user } || !DateTimeOffset.TryParse(body.ExpiresAt, out var expires))
                    return new(LinkStatus.Unreachable, Problem: "The wiki's answer couldn't be read.");
                if (expires <= DateTimeOffset.UtcNow) return new(LinkStatus.Expired, Problem: "The wiki sign-in expired. Start again.");
                return new(LinkStatus.Approved, new WikiSession(body.Token, expires, user));
            }
            var error = await ErrorCode(response, cancellationToken);
            return error switch
            {
                "authorization_pending" or "slow_down" => new(LinkStatus.Pending),
                "access_denied" => new(LinkStatus.Denied, Problem: "The sign-in was denied on the wiki."),
                "expired_token" => new(LinkStatus.Expired, Problem: "The code expired before anyone approved it."),
                "invalid_grant" => new(LinkStatus.Invalid, Problem: "The wiki no longer knows this sign-in. Start again."),
                _ => new(LinkStatus.Unreachable, Problem: $"The wiki answered {(int)response.StatusCode}.")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        { return new(LinkStatus.Unreachable, Problem: "The wiki couldn't be reached."); }
    }

    /// <summary>
    /// Polls until the code is approved, denied or expires, or <paramref name="cancellationToken"/> is
    /// cancelled. A brief network failure doesn't end the wait.
    /// </summary>
    public async Task<LinkResult> WaitAsync(DeviceLink link, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? clock = null, CancellationToken cancellationToken = default)
    {
        delay ??= Task.Delay;
        clock ??= () => DateTimeOffset.Now;
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock() >= link.ExpiresAt) return new(LinkStatus.Expired, Problem: "The code expired before anyone approved it.");
            await delay(link.Interval, cancellationToken);
            var result = await PollAsync(link, cancellationToken);
            if (result.Status == LinkStatus.Pending) { failures = 0; continue; }
            if (result.Status == LinkStatus.Unreachable && ++failures < 5) continue;
            return result;
        }
    }

    /// <summary>Asks the wiki whether a token still stands: not expired, not signed out everywhere, role still allowed.</summary>
    public async Task<CheckStatus> CheckAsync(WikiSession session, CancellationToken cancellationToken = default)
    {
        if (!session.IsCurrent(DateTimeOffset.UtcNow)) return CheckStatus.Expired;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Site, "api/device-links/me"));
            request.Headers.Authorization = new("Bearer", session.Token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return CheckStatus.Revoked;
            if (response.StatusCode != HttpStatusCode.OK) return CheckStatus.Unreachable;

            var body = await response.Content.ReadFromJsonAsync<SessionBody>(Json, cancellationToken);
            if (body is not { Client: ClientName, User: { } user } || !DateTimeOffset.TryParse(body.ExpiresAt, out var expires) ||
                !string.Equals(user.Username, session.User.Username, StringComparison.OrdinalIgnoreCase)) return CheckStatus.Unreachable;
            return session.IsCurrent(DateTimeOffset.UtcNow) && expires > DateTimeOffset.UtcNow ? CheckStatus.Valid : CheckStatus.Expired;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        { return CheckStatus.Unreachable; }
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return (await response.Content.ReadFromJsonAsync<ErrorBody>(Json, cancellationToken))?.Error; }
        catch (JsonException) { return null; }
    }

    private sealed record StartBody(string DeviceCode, string UserCode, string? VerificationPath, int ExpiresIn, int Interval);
    private sealed record TokenBody(string Token, string? ExpiresAt, WikiUser? User);
    private sealed record SessionBody(string? Client, string? ExpiresAt, WikiUser? User);
    private sealed record ErrorBody(string? Error);
}
