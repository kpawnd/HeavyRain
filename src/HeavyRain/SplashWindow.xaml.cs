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
        "Anti-Executable policy:", "Anti-Executable log:", "Found DFC.exe", "Found AEC.exe", "Ready."
    ];

    private readonly ObservableCollection<StartupEntry> entries = [];
    private readonly Queue<StartupEntry> pending = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer typing = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer caret = new() { Interval = TimeSpan.FromMilliseconds(480) };
    private StartupEntry? active;
    private int charsPerTick = 1;
    private bool revealed;
    private bool pumping;
    private bool started;
    private bool closed;

    public SplashWindow()
    {
        InitializeComponent();
        LogEntries.ItemsSource = entries;
        typing.Tick += (_, _) => TypeStep();
        caret.Tick += (_, _) => { if (active is { Typing: false } line) line.CaretOn = !line.CaretOn; };
    }

    public async Task RunStartupAsync(Func<Task> initialize, CancellationToken cancellationToken = default)
    {
        if (started) throw new InvalidOperationException("Startup has already begun.");
        started = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var token = linked.Token;
        elapsed.Restart();
        await Task.WhenAll(initialize().WaitAsync(token), RevealLogsAsync(token));
        // Let queued lines finish playing, but never hold startup more than a moment for them.
        var drainUntil = elapsed.Elapsed + TimeSpan.FromSeconds(2.5);
        while ((pending.Count > 0 || pumping) && elapsed.Elapsed < drainUntil) await Task.Delay(40, token);
        token.ThrowIfCancellationRequested();
        LoadingText.Text = "Checks complete.";
        var remaining = TimeSpan.FromMilliseconds(6000) - elapsed.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
    }

    // The startup log always animates, like the error dialog's shake, because Windows commonly
    // reports animations as off in Remote Desktop and Hyper-V sessions.
    private async Task RevealLogsAsync(CancellationToken token)
    {
        await Task.Delay(1600, token);
        var duration = TimeSpan.FromMilliseconds(420);
        CreditPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
        CreditOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -48, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        await Task.Delay(duration, token);
        CreditPanel.Visibility = Visibility.Collapsed;
        LogPanel.Visibility = Visibility.Visible;
        LogPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        revealed = true;
        caret.Start();
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
        LoadingText.Text = entry.Message;
        if (revealed && !pumping) _ = PumpAsync();
    }

    // Plays queued lines one at a time. A short backlog types each line out; a long one shows
    // lines whole and moves faster, so the animation never falls far behind the real checks.
    private async Task PumpAsync()
    {
        pumping = true;
        try
        {
            while (pending.Count > 0 && !closed)
            {
                Show(pending.Dequeue(), pending.Count);
                await Task.Delay(pending.Count switch { > 12 => 22, > 4 => 50, _ => 110 });
                while (active?.Typing == true && !closed) await Task.Delay(16);
            }
        }
        finally { pumping = false; }
    }

    private void Show(StartupEntry entry, int backlog)
    {
        if (active is { } previous) { previous.Finish(); previous.IsActive = false; }
        entries.Add(entry);
        if (entries.Count > 100) entries.RemoveAt(0);
        active = entry;
        entry.IsActive = true;
        var typingMs = backlog > 4 ? 0 : Math.Min(260, entry.Message.Length * 7);
        if (typingMs == 0) entry.Finish();
        else
        {
            charsPerTick = Math.Max(1, (int)Math.Ceiling(entry.Message.Length / (typingMs / 16.0)));
            typing.Start();
        }
        LogScroll.ScrollToEnd();
    }

    private void TypeStep()
    {
        if (active is not { Typing: true } line) { typing.Stop(); return; }
        line.Advance(charsPerTick);
        if (!line.Typing)
        {
            typing.Stop();
            LogScroll.ScrollToEnd();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        typing.Stop();
        caret.Stop();
        lifetime.Cancel();
        lifetime.Dispose();
        base.OnClosed(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>A startup log line that reveals its text a few characters at a time.</summary>
    private sealed class StartupEntry(string time, string message, bool isResult) : INotifyPropertyChanged
    {
        private int shown;
        private bool isActive;
        private bool caretOn = true;

        public string Time { get; } = time;
        public string Message { get; } = message;
        public bool IsResult { get; } = isResult;
        public bool Typing => shown < Message.Length;

        /// <summary>The revealed text plus a caret while this is the newest line.</summary>
        public string Display => Message[..shown] + (isActive && (Typing || caretOn) ? "▌" : "");

        public bool IsActive
        {
            get => isActive;
            set { if (isActive == value) return; isActive = value; Changed(); Changed(nameof(Display)); }
        }

        public bool CaretOn
        {
            get => caretOn;
            set { if (caretOn == value) return; caretOn = value; Changed(nameof(Display)); }
        }

        public void Advance(int count) { shown = Math.Min(Message.Length, shown + count); Changed(nameof(Display)); }
        public void Finish() { if (!Typing) return; shown = Message.Length; Changed(nameof(Display)); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
