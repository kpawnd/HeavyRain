using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HeavyRain.Core;

namespace HeavyRain;

/// <summary>Deep Freeze's history, pending change, schedules, licence and components.</summary>
public partial class MainWindow
{
    private const int HistoryLimit = 200;
    private static readonly TimeSpan LongThaw = TimeSpan.FromHours(8);
    private DeepFreezeInsights? insights;
    private DfLicense? license;
    private PendingBoot? pendingBoot;
    private ThawedRun? thawedRun;
    private string? sessionRequest;
    private string? historySignature;

    private async Task RefreshInsightsAsync(Action<string>? progress)
    {
        try
        {
            progress?.Invoke(demo ? "Loading the sample Deep Freeze history…" : "Reading Deep Freeze's events, Cloud tasks and components…");
            var policySnapshot = policy;
            var read = demo ? DeepFreezeDemoInsights.Create()
                : await Task.Run(() => DeepFreezeInsightsProbe.Read(policySnapshot, lifetime.Token), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            var previousPending = pendingBoot;
            insights = read;
            pendingBoot = DeepFreezeHistory.Pending(read.Entries, read.LastBoot);
            RenderInsights();
            if (pendingBoot != previousPending && pendingBoot is { } pending)
                Log($"Deep Freeze records a pending change: {PendingTitle(pending)}, requested {pending.RequestedAt.LocalDateTime:HH:mm:ss}{(pending.Source is null ? "" : $" from the {pending.Source}")}.");
            ReportInsights(progress, read);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Deep Freeze history couldn't be read ({ex.GetType().Name}).");
            progress?.Invoke("Deep Freeze history couldn't be read.");
        }
    }

    private void ReportInsights(Action<string>? progress, DeepFreezeInsights read)
    {
        if (progress is null) return;
        var boots = read.Entries.Count(entry => entry.Kind == DfEventKind.BootStatus);
        var cloud = read.Entries.Count(entry => entry.Kind == DfEventKind.CloudTask);
        progress(read.EventProblem ?? $"Deep Freeze events: {boots} boots and {read.Entries.Count(e => e.Kind is DfEventKind.NextBootRequest or DfEventKind.ImmediateChange)} requests in {DeepFreezeInsightsProbe.HistoryDays} days.");
        progress(read.CloudProblem ?? $"Cloud tasks: {cloud} recorded.");
        progress(pendingBoot is { } pending ? $"Pending: {PendingTitle(pending)}." : "Pending: nothing recorded since the last restart.");
        if (thawedRun is { } run) progress($"Thawed since {run.Since.LocalDateTime:ddd HH:mm} ({run.Boots} boot{(run.Boots == 1 ? "" : "s")}).");
        if (read.Policy is { } details)
            progress(details.Schedules.Count == 0 ? "Deep Freeze schedule: none active." : $"Deep Freeze schedule: {details.Schedules.Count} active.");
        progress(read.ComponentProblem ?? $"Components: {ComponentHealth.Summary(read.Components).Split('\n')[0]}");
    }

    private void RenderInsights()
    {
        RenderStateNote();
        RenderPending();
        RenderSchedule();
        RenderComponents();
        RenderHistory();
        RenderAeAccount();
    }

    /// <summary>"For 2 h 14 min · since Fri 18:27 · 3 restarts" under a Thawed state; amber once it's been a while.</summary>
    private void RenderStateNote()
    {
        thawedRun = insights is { } data ? DeepFreezeHistory.Thawed(data.Entries, status.State, data.LastBoot) : null;
        if (thawedRun is not { } run)
        {
            StateNoteText.Visibility = Visibility.Collapsed;
            return;
        }
        var length = DateTimeOffset.Now - run.Since;
        var restarts = run.Boots > 1 ? $" · {run.Boots}{(run.AtLeast ? "+" : "")} restarts" : "";
        StateNoteText.Text = $"For {(run.AtLeast ? "at least " : "")}{Duration(length)} · since {run.Since.LocalDateTime.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)}{restarts}";
        StateNoteText.SetResourceReference(ForegroundProperty, length >= LongThaw ? "WarningBrush" : "MutedBrush");
        StateNoteText.ToolTip = run.AtLeast ? "The latest restart has no Deep Freeze record, so it may have been Thawed for longer." : "From Deep Freeze's boot records.";
        StateNoteText.Visibility = Visibility.Visible;
    }

    private static string PendingTitle(PendingBoot pending) => $"{pending.Target} at next restart";

    private void RenderPending()
    {
        if (pendingBoot is { } pending)
        {
            PendingText.Text = pending.Target == status.State ? $"Stays {pending.Target} after restart" : $"{pending.Target} at next restart";
            PendingText.SetResourceReference(ForegroundProperty, pending.Target == ProtectionState.Thawed ? "WarningBrush" : "AccentBrush");
            var when = pending.RequestedAt.LocalDateTime.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
            PendingDetailText.Text = $"Requested {when}{(pending.Source is null ? "" : $" from the {pending.Source.ToLowerInvariant()}")}"
                + (pending.RestartRequested ? ". The restart it asked for hasn't happened." : ".");
            return;
        }
        PendingText.SetResourceReference(ForegroundProperty, "TextBrush");
        if (insights is null || insights.EventProblem is not null)
        {
            PendingText.Text = sessionRequest ?? "Unknown";
            PendingDetailText.Text = sessionRequest is null ? insights?.EventProblem ?? "Checking Deep Freeze's records…" : "This session's request. Deep Freeze's records couldn't be read.";
            return;
        }
        PendingText.Text = "Nothing pending";
        PendingDetailText.Text = "No change since the last restart.";
    }

    private void RenderSchedule()
    {
        if (insights?.Policy is not { } details)
        {
            ScheduleText.Text = demo || insights is null ? "Checking…" : "Unavailable";
            ScheduleText.ToolTip = insights?.CloudProblem;
            return;
        }
        var now = DateTime.Now;
        var lines = new List<string>();
        var tips = new List<string>();
        foreach (var (schedule, next) in details.Upcoming(now).Take(3))
        {
            var window = schedule.Stop is { } stop ? $"{Clock(schedule.Start)}–{Clock(stop)}" : Clock(schedule.Start);
            var when = schedule.IsRunning(now) ? "running now" : next is { } at ? $"next {(at.Date == now.Date ? "today" : at.ToString("ddd d MMM", CultureInfo.InvariantCulture))}" : "no days set";
            lines.Add($"{CloudPolicyParser.Describe(schedule.Kind)} · {CloudPolicyParser.DescribeDays(schedule.Days)} {window} · {when}");
            tips.Add($"{schedule.Name}: {(schedule.RestartsThawed ? "restarts Thawed to run, then Frozen again" : schedule.Kind == ScheduleKind.Shutdown ? "shuts the PC down" : "restarts the PC")}"
                + (schedule.DisableInput ? "; keyboard and mouse off" : "") + (schedule.LeadSeconds is { } lead ? $"; {lead} s warning" : ""));
        }
        if (details.Schedules.Count > 3) lines.Add($"and {details.Schedules.Count - 3} more");
        if (lines.Count == 0) lines.Add("No Deep Freeze schedule");
        if (details.CheckInMinutes is { } minutes) lines.Add($"Cloud check-in every {Duration(TimeSpan.FromMinutes(minutes))}");
        if (details.CloudMaintenance is { } cloud) lines.Add($"Cloud maintenance {cloud.Start}–{cloud.End}");
        ScheduleText.Text = string.Join("\n", lines);
        ScheduleText.ToolTip = tips.Count == 0 ? "From the cached Cloud policy." : string.Join("\n", tips) + "\nFrom the cached Cloud policy.";
    }

    private void RenderComponents()
    {
        if (insights is not { } data) return;
        if (data.ComponentProblem is { } problem)
        {
            ComponentsSummaryText.Text = problem;
            ComponentsSummaryText.SetResourceReference(ForegroundProperty, "MutedBrush");
            ComponentsList.ItemsSource = null;
            return;
        }
        var problems = ComponentHealth.Problems(data.Components);
        ComponentsSummaryText.Text = ComponentHealth.Summary(data.Components);
        ComponentsSummaryText.SetResourceReference(ForegroundProperty, problems.Count > 0 ? "ErrorBrush" : "TextBrush");
        ComponentsList.ItemsSource = data.Components.Select(component => new ComponentItem(component)).ToList();
    }

    private void RenderHistory()
    {
        if (insights is not { } data) return;
        var notes = new List<string>();
        if (data.EventProblem is not null) notes.Add(data.EventProblem);
        if (data.CloudProblem is not null) notes.Add(data.CloudProblem);
        if (data.Policy?.PersistEventLogs == false) notes.Add("This policy doesn't keep event logs across Frozen restarts, so history from Frozen sessions is lost.");
        var timeline = DeepFreezeHistory.Timeline(data.Entries, HistoryLimit);
        if (timeline.Count == 0 && notes.Count == 0) notes.Add($"No Deep Freeze events or Cloud tasks in the last {DeepFreezeInsightsProbe.HistoryDays} days.");
        DfHistorySourceText.Text = string.Join("\n", notes);
        DfHistorySourceText.Visibility = notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var signature = timeline.Count == 0 ? "" : $"{timeline.Count}|{timeline[0]}";
        if (signature == historySignature) return;
        historySignature = signature;
        var selected = (DfHistoryList.SelectedItem as DfHistoryItem)?.Entry;
        var items = timeline.Select(entry => new DfHistoryItem(entry)).ToList();
        DfHistoryList.ItemsSource = items;
        if (selected is not null) DfHistoryList.SelectedItem = items.FirstOrDefault(item => item.Entry == selected);
    }

    private void RenderAeAccount()
    {
        if (insights?.Policy is not { HasAeLists: true } details)
        {
            AecAccountPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var (membership, entry) = demo ? (AeMembership.Administrator, details.AeAdministrators.LastOrDefault()) : DeepFreezeInsightsProbe.CurrentAeMembership(details);
        AecAccountText.Text = membership switch
        {
            AeMembership.Administrator => "Anti-Executable administrator",
            AeMembership.TrustedUser => "Trusted user",
            _ => "Not in the policy's administrator or trusted user lists"
        };
        AecAccountText.SetResourceReference(ForegroundProperty, membership == AeMembership.NotListed ? "WarningBrush" : "TextBrush");
        AecAccountText.ToolTip = entry is null ? $"Signed in as {Environment.UserName}." : $"Listed as {(entry.IsGroup ? "the group " : "")}{entry.Name} in the cached policy.";
        AecAccountPanel.Visibility = Visibility.Visible;
    }

    private void RenderLicense()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        LicenseText.Text = license?.Describe(today) ?? "Unavailable";
        LicenseText.SetResourceReference(ForegroundProperty, license?.NeedsAttention(today) == true ? "WarningBrush" : "TextBrush");
        LicenseText.ToolTip = license is null ? "DFC didn't report the licence." : $"Reported by DFC.{(license.Status is { } state ? $" Status: {state}." : "")}";
    }

    private static string Clock(TimeSpan time) => $"{time.Hours:00}:{time.Minutes:00}";

    private static string Duration(TimeSpan span) => DurationText.Describe(span);
}
