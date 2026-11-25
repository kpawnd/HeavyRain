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

    internal bool SignInAtStartup(SplashWindow splash)
    {
        if (wiki is null) return true;
        splash.ShowSignInRequired();
        return OpenSignIn(splash, startAutomatically: false) is not null;
    }

    private WikiSession? OpenSignIn(Window? owner = null, bool startAutomatically = true)
    {
        if (wiki is null) return null;
        wikiSession = null;
        RenderSignIn();

        var dialog = new SignInWindow(wiki, startAutomatically) { Owner = owner ?? this };
        if (dialog.ShowDialog() != true || dialog.Session is not { } signedIn || !signedIn.IsCurrent(DateTimeOffset.UtcNow)) return null;

        wikiSession = signedIn;
        Log($"Signed in as {signedIn.User.Username} ({signedIn.User.RoleLabel}) through the wiki, until {signedIn.ExpiresAt.LocalDateTime:HH:mm}.");
        RenderSignIn();
        return signedIn;
    }

}
