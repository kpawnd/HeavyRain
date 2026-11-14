using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MahApps.Metro.Controls;

namespace HeavyRain;

public partial class SplashWindow : MetroWindow
{
    // Lines whose text starts with one of these are check results, drawn in the accent colour.
    private static readonly string[] ResultPrefixes =
    [
        "Protection:", "Policy:", "Deep Freeze version:", "Anti-Executable:", "Anti-Executable version:",
        "Anti-Executable policy:", "Anti-Executable log:", "Found DFC.exe", "Found AEC.exe", "Licence:", "Deep Freeze events:",
        "Cloud tasks:", "Pending:", "Thawed since", "Deep Freeze schedule:", "Components:", "Ready."
    ];

    // Individual path probes. Shown when the log is keeping up, skipped when lines back up, so
    // the log stays readable; the "Found …" line that follows them is always shown.
    private const string MinorPrefix = "Looking for ";

    /// <summary>The log's scroll position, as an animatable property so the view can glide.</summary>
    public static readonly DependencyProperty LogOffsetProperty = DependencyProperty.Register(
        nameof(LogOffset), typeof(double), typeof(SplashWindow),
        new PropertyMetadata(0.0, (owner, e) => ((SplashWindow)owner).LogScroll.ScrollToVerticalOffset((double)e.NewValue)));

    private readonly ObservableCollection<StartupEntry> entries = [];
    private readonly Queue<StartupEntry> pending = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource lifetime = new();
    private StartupEntry? active;
    private bool revealed;
    private bool pumping;
    private bool started;
    private bool closed;

    public SplashWindow()
    {
        InitializeComponent();
        LogEntries.ItemsSource = entries;
    }

    public double LogOffset
    {
        get => (double)GetValue(LogOffsetProperty);
        set => SetValue(LogOffsetProperty, value);
    }

    public async Task RunStartupAsync(Func<Task> initialize, CancellationToken cancellationToken = default)
    {
        if (started) throw new InvalidOperationException("Startup has already begun.");
        started = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var token = linked.Token;
        elapsed.Restart();
        await Task.WhenAll(initialize().WaitAsync(token), RevealLogsAsync(token));
        // Let the queued lines finish at their own pace, within a limit, then hold the last line
        // briefly so the final results can be read.
        var drainUntil = elapsed.Elapsed + TimeSpan.FromSeconds(4);
        while ((pending.Count > 0 || pumping) && elapsed.Elapsed < drainUntil) await Task.Delay(40, token);
        token.ThrowIfCancellationRequested();
        LoadingText.Text = "Checks complete.";
        var remaining = TimeSpan.FromMilliseconds(Math.Max(6000, elapsed.Elapsed.TotalMilliseconds + 700)) - elapsed.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
    }

    // The startup log always animates, like the error dialog's shake, because Windows commonly
    // reports animations as off in Remote Desktop and Hyper-V sessions.
    private async Task RevealLogsAsync(CancellationToken token)
    {
        await Task.Delay(1600, token);
        var duration = TimeSpan.FromMilliseconds(650);
        CreditPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        CreditOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -36, duration)
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseIn }
        });
        await Task.Delay(duration, token);
        CreditPanel.Visibility = Visibility.Collapsed;
        LogPanel.Visibility = Visibility.Visible;
        LogPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        revealed = true;
        if (!pumping) _ = PumpAsync();
    }

    public void ReportProgress(string message)
    {
        var entry = new StartupEntry($"{elapsed.Elapsed.TotalSeconds:00.0}s", message,
            ResultPrefixes.Any(prefix => message.StartsWith(prefix, StringComparison.Ordinal)));
        if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(() => Enqueue(entry));
    }

    private void Enqueue(StartupEntry entry)
    {
        if (closed) return;
        pending.Enqueue(entry);
        if (revealed && !pumping) _ = PumpAsync();
    }

    // Plays queued lines one at a time on a steady beat. A backlog shortens the beat a little
    // and drops individual path probes, but never bunches lines together.
    private async Task PumpAsync()
    {
        pumping = true;
        try
        {
            while (pending.Count > 0 && !closed)
            {
                var entry = pending.Dequeue();
                if (pending.Count > 2 && entry.Message.StartsWith(MinorPrefix, StringComparison.Ordinal)) continue;
                Show(entry);
                await Task.Delay(pending.Count switch { > 12 => 70, > 5 => 100, _ => 150 });
            }
        }
        finally { pumping = false; }
    }

    private void Show(StartupEntry entry)
    {
        if (active is { } previous) previous.IsActive = false;
        entries.Add(entry);
        if (entries.Count > 100) entries.RemoveAt(0);
        active = entry;
        entry.IsActive = true;
        LoadingText.Text = entry.Message;
        GlideToEnd();
    }

    // Retargets the scroll animation from wherever it currently is, so consecutive lines blend
    // into one continuous movement instead of jumping.
    private void GlideToEnd()
    {
        LogScroll.UpdateLayout();
        var target = LogScroll.ScrollableHeight;
        if (Math.Abs(target - LogOffset) < 0.5) return;
        BeginAnimation(LogOffsetProperty, new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(650),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        BeginAnimation(LogOffsetProperty, null);
        lifetime.Cancel();
        lifetime.Dispose();
        base.OnClosed(e);
    }

    private sealed class StartupEntry(string time, string message, bool isResult) : INotifyPropertyChanged
    {
        private bool isActive;

        public string Time { get; } = time;
        public string Message { get; } = message;
        public bool IsResult { get; } = isResult;

        public bool IsActive
        {
            get => isActive;
            set { if (isActive == value) return; isActive = value; Changed(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
