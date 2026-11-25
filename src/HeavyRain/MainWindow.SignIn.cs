using System.Net.Http;
using System.Reflection;
using System.Windows;
using HeavyRain.Core;

namespace HeavyRain;

/// <summary>
/// Signing in through the TA wiki. Only active in a build given the wiki's address (see
/// HeavyRain.local.props.example); without one, nothing here shows and nothing changes.
/// </summary>
public partial class MainWindow
{
    private static readonly HttpClient WikiHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private WikiSignInClient? wiki;
    private WikiSession? wikiSession;

    private static Uri? ConfiguredWiki()
    {
        var built = typeof(MainWindow).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "WikiUrl")?.Value;
        return WikiSignInClient.ParseSite(built);
    }

    private void InitializeSignIn()
    {
        wiki = demo ? null : ConfiguredWiki() is { } site ? new WikiSignInClient(WikiHttp, site) : null;
        SignInButton.Visibility = wiki is null ? Visibility.Collapsed : Visibility.Visible;
        RenderSignIn();
        if (wiki is not null) Log($"Sign-in through the wiki at {wiki.Site.Host} is required for changes.");
    }

    private void RenderSignIn()
    {
        if (wikiSession is { } session && session.IsCurrent(DateTimeOffset.UtcNow))
        {
            SignInButton.Content = session.User.DisplayName;
            SignInButton.ToolTip = $"Signed in as {session.User.Username} · {session.User.RoleLabel} · until {session.ExpiresAt.LocalDateTime:HH:mm}. Click to sign out.";
        }
        else
        {
            SignInButton.Content = "Sign in";
            SignInButton.ToolTip = "Sign in with the TA wiki";
        }
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (wikiSession is { } session && session.IsCurrent(DateTimeOffset.UtcNow))
        {
            wikiSession = null;
            Log($"Signed out ({session.User.Username}).");
            RenderSignIn();
            return;
        }
        OpenSignIn();
    }

    private WikiSession? OpenSignIn()
    {
        if (wiki is null) return null;
        wikiSession = null;
        RenderSignIn();

        var dialog = new SignInWindow(wiki) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Session is not { } signedIn || !signedIn.IsCurrent(DateTimeOffset.UtcNow)) return null;

        wikiSession = signedIn;
        Log($"Signed in as {signedIn.User.Username} ({signedIn.User.RoleLabel}) through the wiki, until {signedIn.ExpiresAt.LocalDateTime:HH:mm}.");
        RenderSignIn();
        return signedIn;
    }

    private async Task<(bool Allowed, WikiSession? Session)> AuthorizeActionAsync(string action)
    {
        if (wiki is null) return (true, null);
        if (lifetime.IsCancellationRequested) return (false, null);

        if (wikiSession is { } session)
        {
            CheckStatus status;
            try { status = await wiki.CheckAsync(session, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return (false, null); }
            if (lifetime.IsCancellationRequested || !ReferenceEquals(wikiSession, session)) return (false, null);

            if (session.IsCurrent(DateTimeOffset.UtcNow))
            {
                if (status == CheckStatus.Valid) return (true, session);
                if (status == CheckStatus.Unreachable)
                {
                    Log($"Couldn't verify {session.User.Username}'s wiki sign-in. The request to {action} was blocked.");
                    new ActionNoticeWindow(new("Couldn't verify wiki sign-in", $"Heavy Rain needs to reach the TA wiki to {action}. Check your connection and try again.")) { Owner = this }.ShowDialog();
                    return (false, null);
                }
            }

            Log(status == CheckStatus.Revoked ? "The wiki no longer accepts this sign-in." : "The wiki sign-in expired.");
        }

        var signedIn = OpenSignIn();
        return signedIn is not null && !lifetime.IsCancellationRequested ? (true, signedIn) : (false, null);
    }
}
