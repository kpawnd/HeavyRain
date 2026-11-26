using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
            SignInButton.Content = $"{session.User.DisplayName} ▾";
            SignInButton.ToolTip = $"Account menu · {session.User.DisplayName}";
            AutomationProperties.SetName(SignInButton, $"Account menu for {session.User.DisplayName}");
        }
        else
        {
            SignInButton.Content = "Sign in";
            SignInButton.ToolTip = "Sign in with the TA wiki";
            AutomationProperties.SetName(SignInButton, "Sign in with the TA wiki");
        }
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (wikiSession is { } session && session.IsCurrent(DateTimeOffset.UtcNow))
        {
            var menu = (ContextMenu)FindResource("WikiAccountMenu");
            menu.PlacementTarget = SignInButton;
            menu.DataContext = new { session.User.DisplayName, session.User.RoleLabel, session.ExpiresAt };
            menu.IsOpen = true;
            return;
        }
        OpenSignIn();
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (busy || wikiSession is not { } session) return;
        wikiSession = null;
        var menu = (ContextMenu)FindResource("WikiAccountMenu");
        menu.IsOpen = false;
        menu.DataContext = null;
        Log($"Signed out ({session.User.Username}).");
        RenderSignIn();
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
