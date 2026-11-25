using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using HeavyRain.Core;
using MahApps.Metro.Controls;

namespace HeavyRain;

/// <summary>
/// Shows the code to approve on the wiki and waits for the answer. The session it returns is kept by
/// the main window, in memory only: lab PCs are shared, so nothing is saved for the next person.
/// </summary>
public partial class SignInWindow : MetroWindow
{
    private readonly WikiSignInClient client;
    private readonly CancellationTokenSource cancel = new();
    private readonly DispatcherTimer countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private DeviceLink? link;

    public WikiSession? Session { get; private set; }

    public SignInWindow(WikiSignInClient client, bool startAutomatically = true)
    {
        InitializeComponent();
        this.client = client;
        countdown.Tick += (_, _) => ShowWaiting();
        if (startAutomatically)
            Loaded += async (_, _) => await RunAsync();
        else
        {
            IntroText.Text = "Use your wiki account to open Heavy Rain.";
            StatusText.Visibility = Visibility.Collapsed;
            OpenButton.Content = "Sign in";
            OpenButton.IsEnabled = true;
            Loaded += (_, _) => OpenButton.Focus();
        }
    }

    private async Task RunAsync()
    {
        OpenButton.IsEnabled = false;
        CancelButton.Content = "Cancel";
        IntroText.Text = "Approve this code in your browser, or open the link on your phone.";
        StatusText.Visibility = Visibility.Visible;
        StatusText.SetResourceReference(ForegroundProperty, "TextBrush");
        StatusText.Text = "Requesting a sign-in code…";
        try
        {
            link = await client.StartAsync(Environment.MachineName, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (link is null) { Fail($"The wiki at {client.Site.Host} couldn't start a sign-in. Check this PC's connection and try again."); return; }
            CodeText.Text = link.UserCode;
            LinkText.Text = link.VerificationUri.ToString();
            CodeText.Visibility = LinkText.Visibility = CopyButton.Visibility = Visibility.Visible;
            OpenButton.Content = "Open sign-in page";
            OpenButton.IsEnabled = CopyButton.IsEnabled = true;
            OpenButton.Focus();
            ShowWaiting();
            countdown.Start();
            OpenVerificationPage();
            var result = await client.WaitAsync(link, cancellationToken: cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            countdown.Stop();
            if (result is { Status: LinkStatus.Approved, Session: { } session })
            {
                Session = session;
                DialogResult = true;
                return;
            }
            Fail(result.Problem ?? "The sign-in didn't complete.");
        }
        catch (OperationCanceledException) { }
    }

    private void ShowWaiting()
    {
        if (link is null) return;
        var left = link.ExpiresAt - DateTimeOffset.Now;
        StatusText.Text = left > TimeSpan.Zero ? $"Waiting for approval · the code expires in {(int)left.TotalMinutes}:{left.Seconds:00}" : "The code has expired.";
    }

    private void Fail(string message)
    {
        countdown.Stop();
        link = null;
        CodeText.Visibility = LinkText.Visibility = CopyButton.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
        StatusText.SetResourceReference(ForegroundProperty, "ErrorBrush");
        BrowserHelp.Visibility = Visibility.Collapsed;
        OpenButton.Content = "Try again";
        OpenButton.IsEnabled = true;
        CopyButton.IsEnabled = false;
        CancelButton.Content = "Close";
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (link is not null) OpenVerificationPage();
        else if (OpenButton.IsEnabled) await RunAsync();
    }

    private void OpenVerificationPage()
    {
        if (link is null) return;
        BrowserHelp.Visibility = DesktopBrowser.TryOpen(link.VerificationUri) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (link is null) return;
        try { Clipboard.SetText(link.VerificationUri.ToString()); StatusText.Text = "Link copied."; }
        catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = "The clipboard is busy; try again."; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        countdown.Stop();
        cancel.Cancel();
    }
}
