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
        var (allowed, session) = await AuthorizeActionAsync(prompt);
        if (!allowed || session is null) return (allowed, null);

        var audit = new ProtectionAudit(new WikiActionAuditClient(WikiHttp, wiki!.Site), session, Guid.NewGuid());
        var result = await audit.Client.RecordRequestAsync(session, audit.RequestId, Environment.MachineName, action, restart, lifetime.Token);
        if (result == WikiAuditStatus.Recorded) return (true, audit);
        if (result == WikiAuditStatus.SignInRequired && ReferenceEquals(wikiSession, session))
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
