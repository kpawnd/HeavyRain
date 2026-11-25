using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HeavyRain.Core;

public enum WikiProtectionAction { Freeze, Thaw, EnableAntiExecutable, DisableAntiExecutable, StartMaintenance, AllowFile }
public enum WikiAuditStatus { Recorded, SignInRequired, Unavailable }

public sealed class WikiActionAuditClient(HttpClient http, Uri wiki)
{
    public Task<WikiAuditStatus> RecordRequestAsync(WikiSession session, Guid requestId, string computer,
        WikiProtectionAction action, bool restart, CancellationToken cancellationToken = default) =>
        SendAsync(session, "api/device-links/actions", new
        {
            requestId,
            computer = WikiSignInClient.ComputerName(computer),
            action = action switch
            {
                WikiProtectionAction.Freeze => "freeze",
                WikiProtectionAction.Thaw => "thaw",
                WikiProtectionAction.EnableAntiExecutable => "anti_executable_enable",
                WikiProtectionAction.DisableAntiExecutable => "anti_executable_disable",
                WikiProtectionAction.StartMaintenance => "anti_executable_maintenance",
                WikiProtectionAction.AllowFile => "anti_executable_allow",
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            },
            restart
        }, cancellationToken);

    public Task<WikiAuditStatus> RecordOutcomeAsync(WikiSession session, Guid requestId, ChangeOutcome outcome,
        CancellationToken cancellationToken = default) =>
        SendAsync(session, $"api/device-links/actions/{requestId:D}", new
        {
            outcome = outcome switch
            {
                ChangeOutcome.Accepted => "accepted",
                ChangeOutcome.Rejected => "rejected",
                ChangeOutcome.Uncertain => "uncertain",
                ChangeOutcome.NotNeeded => "not_needed",
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            }
        }, cancellationToken);

    private async Task<WikiAuditStatus> SendAsync<T>(WikiSession session, string path, T body, CancellationToken cancellationToken)
    {
        // The server accepts identical retries for the same request ID. Retry a lost response once.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(wiki, path)) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            try
            {
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return WikiAuditStatus.SignInRequired;
                if (response.IsSuccessStatusCode)
                {
                    using var acknowledgment = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: timeout.Token);
                    return acknowledgment?.RootElement is { ValueKind: JsonValueKind.Object } root
                        && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                        ? WikiAuditStatus.Recorded : WikiAuditStatus.Unavailable;
                }
                if ((int)response.StatusCode < 500) return WikiAuditStatus.Unavailable;
            }
            catch (JsonException) { return WikiAuditStatus.Unavailable; }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        return WikiAuditStatus.Unavailable;
    }
}
