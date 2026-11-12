using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MahApps.Metro.Controls;

namespace HeavyRain;

public partial class SplashWindow : MetroWindow
{
    private readonly ObservableCollection<StartupEntry> entries = [];
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource lifetime = new();
    private bool started;
    private bool closed;

    public SplashWindow()
    {
        InitializeComponent();
        LogEntries.ItemsSource = entries;
    }

    public async Task RunStartupAsync(Func<Task> initialize, CancellationToken cancellationToken = default)
    {
        if (started) throw new InvalidOperationException("Startup has already begun.");
        started = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var token = linked.Token;
        elapsed.Restart();
        await Task.WhenAll(initialize().WaitAsync(token), RevealLogsAsync(token));
        token.ThrowIfCancellationRequested();
        LoadingText.Text = "Checks complete.";
        var remaining = TimeSpan.FromMilliseconds(6000) - elapsed.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
    }

    private async Task RevealLogsAsync(CancellationToken token)
    {
        await Task.Delay(1600, token);
        if (SystemParameters.ClientAreaAnimation)
        {
            var duration = TimeSpan.FromMilliseconds(420);
            CreditPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
            CreditOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -48, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
            await Task.Delay(duration, token);
        }
        CreditPanel.Visibility = Visibility.Collapsed;
        LogPanel.Visibility = Visibility.Visible;
        LogPanel.Opacity = 1;
        if (SystemParameters.ClientAreaAnimation)
            LogPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    public void ReportProgress(string message)
    {
        var entry = new StartupEntry($"{elapsed.Elapsed.TotalSeconds:00.0}s", message);
        if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(() => Append(entry));
    }

    private void Append(StartupEntry entry)
    {
        if (closed) return;
        entries.Add(entry);
        if (entries.Count > 100) entries.RemoveAt(0);
        LoadingText.Text = entry.Message;
        LogScroll.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        base.OnClosed(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record StartupEntry(string Time, string Message);
}
