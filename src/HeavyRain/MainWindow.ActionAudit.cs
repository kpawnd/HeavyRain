using System.Windows;
using HeavyRain.Core;

namespace HeavyRain;

public partial class MainWindow
{
    private sealed record ProtectionAudit(WikiActionAuditClient Client, WikiSession Session, Guid RequestId)
    {
        public string Attribution => $" by {Session.User.Username}";
        public Task<bool>? OutcomeReport { get; set; }
    }

    private async Task<(bool Allowed, ProtectionAudit? Audit)> BeginProtectionAuditAsync(string prompt,
        WikiProtectionAction action, bool restart = false)
    {
        if (wiki is not { } signIn) return (true, null);
        if (lifetime.IsCancellationRequested) return (false, null);

        var session = wikiSession;
        var canRenew = session?.IsCurrent(DateTimeOffset.UtcNow) == true;
        if (!canRenew) session = OpenSignIn();
        if (session is null || lifetime.IsCancellationRequested) return (false, null);

        var client = new WikiActionAuditClient(WikiHttp, signIn.Site);
        var requestId = Guid.NewGuid();
        // The wiki authenticates the user and records the request together.
        async Task<WikiAuditStatus> RecordRequestAsync(WikiSession actor)
        {
            try { return await client.RecordRequestAsync(actor, requestId, Environment.MachineName, action, restart, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return WikiAuditStatus.Unavailable; }
        }

        var result = await RecordRequestAsync(session);
        if (lifetime.IsCancellationRequested || !ReferenceEquals(wikiSession, session)) return (false, null);
        if (result == WikiAuditStatus.SignInRequired && canRenew)
        {
            Log("The wiki no longer accepts this sign-in.");
            session = OpenSignIn();
            if (session is null || lifetime.IsCancellationRequested) return (false, null);
            result = await RecordRequestAsync(session);
            if (lifetime.IsCancellationRequested || !ReferenceEquals(wikiSession, session)) return (false, null);
        }
        if (result == WikiAuditStatus.Recorded) return (true, new ProtectionAudit(client, session, requestId));
        if (result == WikiAuditStatus.SignInRequired)
        {
            wikiSession = null;
            RenderSignIn();
        }

        var detail = result == WikiAuditStatus.SignInRequired
            ? "The wiki no longer accepts this sign-in. Sign in again before retrying."
            : "The wiki did not confirm the audit record. Check the wiki connection and retry.";
        Log($"Wiki did not authorize the request to {prompt}. No change was sent.");
        new ActionNoticeWindow(new("No protection change was sent", detail)) { Owner = this }.ShowDialog();
        return (false, null);
    }

    private Task<bool> ReportProtectionOutcomeAsync(ProtectionAudit? audit, ChangeOutcome outcome) =>
        audit is null ? Task.FromResult(true) : audit.OutcomeReport ??= SendProtectionOutcomeAsync(audit, outcome);

    private async Task<bool> SendProtectionOutcomeAsync(ProtectionAudit audit, ChangeOutcome outcome)
    {
        // Once a request has been sent, closing the window should not cancel the bounded outcome report.
        var result = await audit.Client.RecordOutcomeAsync(audit.Session, audit.RequestId, outcome);
        if (result == WikiAuditStatus.Recorded) return true;
        if (!lifetime.IsCancellationRequested)
            Log($"Wiki audit outcome was not confirmed for request {audit.RequestId:D}. Local result: {outcome}.");
        return false;
    }
}
