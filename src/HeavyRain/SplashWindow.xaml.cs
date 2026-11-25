using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
    private bool finished;
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
        var reveal = RevealLogsAsync(token);
        try
        {
            await initialize().WaitAsync(token);
            await Dispatcher.Yield(DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
            CompleteLog();
            SetCompleteStatus("Checks complete.");
        }
        finally
        {
            linked.Cancel();
            try { await reveal; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    internal void ShowSignInRequired() => SetCompleteStatus("Sign in with the TA wiki to continue.");

    private void SetCompleteStatus(string message)
    {
        StartupProgress.IsIndeterminate = false;
        StartupProgress.Value = StartupProgress.Maximum;
        LoadingText.Text = message;
    }

    private void CompleteLog()
    {
        finished = true;
        if (active is { } last) last.IsActive = false;
        active = null;
        while (pending.TryDequeue(out var entry))
            if (!entry.Message.StartsWith(MinorPrefix, StringComparison.Ordinal)) AddEntry(entry);

        CreditPanel.Visibility = Visibility.Collapsed;
        LogPanel.BeginAnimation(OpacityProperty, null);
        LogPanel.Opacity = 1;
        LogPanel.Visibility = Visibility.Visible;
        BeginAnimation(LogOffsetProperty, null);
        LogScroll.ScrollToEnd();
    }

    // The startup log always animates, like the error dialog's shake, because Windows commonly
    // reports animations as off in Remote Desktop and Hyper-V sessions.
    private async Task RevealLogsAsync(CancellationToken token)
    {
        await Task.Delay(1600, token);
        token.ThrowIfCancellationRequested();
        var duration = TimeSpan.FromMilliseconds(650);
        CreditPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        CreditOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -36, duration)
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseIn }
        });
        await Task.Delay(duration, token);
        token.ThrowIfCancellationRequested();
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
        if (Dispatcher.CheckAccess()) Enqueue(entry);
        else if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(() => Enqueue(entry));
    }

    private void Enqueue(StartupEntry entry)
    {
        if (closed || finished) return;
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
            while (pending.Count > 0 && !closed && !finished)
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
        AddEntry(entry);
        active = entry;
        entry.IsActive = true;
        LoadingText.Text = entry.Message;
        GlideToEnd();
    }

    private void AddEntry(StartupEntry entry)
    {
        entries.Add(entry);
        if (entries.Count > 100) entries.RemoveAt(0);
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
