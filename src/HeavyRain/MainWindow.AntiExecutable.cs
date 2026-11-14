using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using HeavyRain.Core;
using MahApps.Metro.Controls.Dialogs;
using Microsoft.Win32;

namespace HeavyRain;

public partial class MainWindow
{
    private readonly AecClient aecClient;
    private AecInstallationInfo? aecInstallation;
    private AecStatusResult aecStatus = new(AecState.Unknown, "Not queried yet.");
    private DateTimeOffset? aecCheckedAt;
    private string? aecCustomPath;
    private string? aecVersion;
    private bool aecUncertain;
    private AecAction? aecPendingAction;
    private AecPolicyInfo? aecPolicyInfo;
    private string? aecActivitySignature;
    private const int AecActivityLimit = 100;

    private async Task RefreshAecAsync(bool inspectMetadata, bool log, Action<string>? progress = null)
    {
        try
        {
            if (inspectMetadata || aecInstallation is null)
            {
                progress?.Invoke(demo ? "Loading the sample Anti-Executable installation…" : "Searching for AEC.exe…");
                aecInstallation = demo ? new("Demo — no executable", "6.11.2112.730", [])
                    : await Task.Run(() => AecInstallationProbe.Inspect(aecCustomPath, progress, lifetime.Token), lifetime.Token);
                if (!demo && aecInstallation.Path is not null) progress?.Invoke("Running AEC.exe version…");
                aecVersion = aecInstallation.Path is { } path ? await aecClient.GetVersionAsync(path, lifetime.Token) : null;
                lifetime.Token.ThrowIfCancellationRequested();
                AecPathText.Text = aecInstallation.Path ?? "Not found";
                AecInstallationDetailText.Text = string.Join("\n", aecInstallation.Warnings);
                AecVersionText.Text = aecVersion ?? aecInstallation.FileVersion;
                var versionSource = demo ? "sample" : aecVersion is not null ? "reported by AEC" : aecInstallation.Path is not null ? "AEC file version" : "no AEC interface";
                AecVersionText.ToolTip = char.ToUpperInvariant(versionSource[0]) + versionSource[1..];
                progress?.Invoke($"Anti-Executable version: {AecVersionText.Text} ({versionSource}).");
            }
            var previous = aecStatus;
            progress?.Invoke(demo ? "Loading the sample Anti-Executable state…" : aecInstallation.Path is not null ? "Running AEC.exe status…" : "Skipping Anti-Executable status: AEC.exe is unavailable.");
            var result = aecInstallation.Path is { } executable ? await aecClient.QueryAsync(executable, cancellationToken: lifetime.Token)
                : new(AecState.Unknown, "The Anti-Executable control interface is unavailable. Open installation to locate AEC.exe.");
            lifetime.Token.ThrowIfCancellationRequested();
            ApplyAecStatus(result);
            progress?.Invoke($"Anti-Executable: {AecStateText.Text}." + (result.State == AecState.Unknown ? $" {result.Detail}" : ""));
            if (log || previous != result) Log($"Anti-Executable: {AecStateText.Text}. {result.Detail}");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ApplyAecStatus(new(AecState.Unknown, "The Anti-Executable query could not be completed. Check status to try again."));
            Log($"Anti-Executable query failed ({ex.GetType().Name}).");
            progress?.Invoke("Anti-Executable query failed; its state is unknown.");
        }
        await RefreshAecInsightsAsync(progress);
    }

    // Policy and activity are read separately from the protection state, so a missing or locked
    // log never affects the reported state.
    private async Task RefreshAecInsightsAsync(Action<string>? progress)
    {
        try
        {
            progress?.Invoke(demo ? "Loading the sample Anti-Executable policy and log…" : "Reading Anti-Executable policy settings and log…");
            AecPolicyInfo info;
            IReadOnlyList<AeLogEntry>? entries;
            string? error;
            if (demo) (info, entries, error) = (new AecPolicyInfo(AecDemoInsights.Policy, "sample data", null), AeLog.Parse(AecDemoInsights.LogLines), null);
            else (info, entries, error) = await Task.Run(ReadAecInsights, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            ApplyAecPolicy(info);
            ApplyAecActivity(entries, info.LogPath, error);
            progress?.Invoke(info.Policy is { } aePolicy
                ? $"Anti-Executable policy: unknown files · {(aePolicy.UnknownFiles is { } unknown ? AecPolicy.Humanize(unknown) : "not set")}."
                : info.Warning ?? "Anti-Executable policy settings not found.");
            progress?.Invoke(entries is null ? error ?? "Anti-Executable log unavailable."
                : entries.Count == 0 ? "Anti-Executable log: no recent entries."
                : $"Anti-Executable log: {Math.Min(entries.Count, AecActivityLimit)} recent entries, latest · {entries[^1].Event.TrimEnd('.')}.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Anti-Executable policy or activity could not be read ({ex.GetType().Name}).");
            ApplyAecActivity(null, "", "Anti-Executable's activity couldn't be read.");
        }
    }

    private static (AecPolicyInfo Info, IReadOnlyList<AeLogEntry>? Entries, string? Error) ReadAecInsights()
    {
        var info = AecPolicyProbe.Read();
        try
        {
            if (!File.Exists(info.LogPath)) return (info, null, $"Anti-Executable's log wasn't found at {info.LogPath}.");
            return (info, AeLog.Parse(AeLog.ReadTailLines(info.LogPath)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (info, null, $"Anti-Executable's log couldn't be read ({ex.GetType().Name}).");
        }
    }

    private void ApplyAecPolicy(AecPolicyInfo info)
    {
        aecPolicyInfo = info;
        var aePolicy = info.Policy;
        AecPolicyText.Text = aePolicy is null ? "Unavailable" : string.Join("\n", aePolicy.Describe().Select(item => $"{item.Label}: {item.Value}"));
        // Only problems get a line here; where the settings come from is in the README.
        var problem = demo ? null : info.Warning ?? (aePolicy is null ? "No policy settings found" : null);
        AecPolicySourceText.Text = problem ?? "";
        AecPolicySourceText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        AecPolicyNoteText.Visibility = aePolicy?.UnknownFilesRun == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyAecActivity(IReadOnlyList<AeLogEntry>? entries, string logPath, string? error)
    {
        AecActivityList.ToolTip = entries is null || demo ? null : logPath;
        if (entries is null)
        {
            aecActivitySignature = null;
            AecActivityList.ItemsSource = null;
            ShowAecActivityProblem(error ?? "Anti-Executable's log is unavailable.");
            return;
        }
        var newest = AeLog.Newest(entries, AecActivityLimit);
        ShowAecActivityProblem(newest.Count == 0 ? "No recent entries" : null);
        var signature = newest.Count == 0 ? "" : $"{entries.Count}|{newest[0]}";
        if (signature == aecActivitySignature) return;
        aecActivitySignature = signature;
        var selected = (AecActivityList.SelectedItem as AecActivityItem)?.Entry;
        var items = newest.Select(entry => new AecActivityItem(entry)).ToList();
        AecActivityList.ItemsSource = items;
        if (selected is not null) AecActivityList.SelectedItem = items.FirstOrDefault(item => item.Entry == selected);
    }

    private void ShowAecActivityProblem(string? problem)
    {
        AecActivitySourceText.Text = problem ?? "";
        AecActivitySourceText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void AecAllow_Click(object sender, RoutedEventArgs e)
    {
        if (AecActivityList.SelectedItem is not AecActivityItem { Entry.FilePath: { } file } item)
        {
            new ActionNoticeWindow(new("Select a program to allow", "Choose an activity entry for a program, such as an Unknown File entry, then choose Allow selected file.")) { Owner = this }.ShowDialog();
            return;
        }
        if (AecBlocked(true)) return;
        if (!AecClient.IsAllowablePath(file))
        {
            new ActionNoticeWindow(new("This entry can't be allowed", "Only programs with a full local path can be added to the allowed list.")) { Owner = this }.ShowDialog();
            return;
        }
        if (!demo && !File.Exists(file))
        {
            new ActionNoticeWindow(new("The file is no longer there", $"{file} wasn't found, so there's nothing to allow. It may have been moved or deleted, or removed by a frozen restart.")) { Owner = this }.ShowDialog();
            return;
        }
        var entry = item.Entry;
        var details = string.Join("\n", new[]
        {
            entry.Product is null ? null : $"Product: {entry.Product}{(entry.Version is null ? "" : " " + entry.Version)}",
            entry.Publisher is null ? null : $"Publisher: {entry.Publisher}",
            entry.Hash is null ? null : $"Hash: {entry.Hash}",
            $"Logged: {entry.Time} · {entry.Event}"
        }.Where(line => line is not null));
        SetBusy(true);
        timer.Stop();
        var password = string.Empty;
        try
        {
            // One MahApps dialog both confirms the file and takes the AE password, since this
            // runs from the activity tab, away from the Anti-Executable password box.
            var login = await this.ShowLoginAsync("Allow this file?",
                $"Computer: {Environment.MachineName}{(demo ? " (DEMO)" : "")}\n\n{file}\n{details}\n\nAnti-Executable will add it to the local allowed list, so it can run even when unknown programs are blocked. Only allow files you trust.",
                new LoginDialogSettings
                {
                    ShouldHideUsername = true, PasswordWatermark = "Anti-Executable password, if set", EnablePasswordPreview = false,
                    RememberCheckBoxVisibility = Visibility.Collapsed, NegativeButtonVisibility = Visibility.Visible,
                    AffirmativeButtonText = "Allow file", NegativeButtonText = "Cancel"
                });
            if (login is null || lifetime.IsCancellationRequested) return;
            password = login.Password ?? string.Empty;
            var result = await aecClient.AllowAsync(aecInstallation!.Path!, password, file, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            SetActivity(false);
            AecPendingText.Text = $"{DateTime.Now:HH:mm:ss} · {result.Detail}";
            Log($"Anti-Executable allow {entry.FileName}: {result.Outcome}. {result.Detail}");
            if (result.Outcome is ChangeOutcome.Rejected or ChangeOutcome.Uncertain)
                new ActionNoticeWindow(new(result.Outcome == ChangeOutcome.Rejected ? "Anti-Executable didn't allow the file" : "The allow request's result is unknown", result.Detail)) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetActivity(false);
            AecPendingText.Text = "The allow request couldn't be completed. It may have taken effect; check the allowed list in Anti-Executable.";
            Log($"Anti-Executable allow failed ({ex.GetType().Name}).");
            new ActionNoticeWindow(new("The allow request couldn't be completed", AecPendingText.Text)) { Owner = this }.ShowDialog();
        }
        finally
        {
            password = string.Empty; AecPasswordInput.Clear(); SetBusy(false);
            if (!lifetime.IsCancellationRequested) timer.Start();
        }
    }

    private void ApplyAecStatus(AecStatusResult result)
    {
        aecStatus = result;
        aecCheckedAt = DateTimeOffset.Now;
        if (result.State != AecState.Unknown) aecUncertain = false;
        if (result.State != AecState.Unknown && aecPendingAction is { } pending)
        {
            var desired = pending switch { AecAction.Enable => AecState.Enabled, AecAction.Disable => AecState.Disabled, _ => AecState.Maintenance };
            var observed = result.State switch { AecState.Enabled => "Protected", AecState.Disabled => "Unprotected", _ => "Maintenance" };
            var detail = result.State == desired ? $"{pending} verified by the latest status check: {observed}."
                : $"{pending} was requested; the latest status check reports {observed}.";
            AecPendingText.Text = $"{DateTime.Now:HH:mm:ss} · {detail}";
            Log($"Anti-Executable: {detail}");
            aecPendingAction = null;
        }
        AecStateText.Text = result.Diagnostic switch
        {
            AecDiagnostic.ExternalUser => "Not authorized",
            AecDiagnostic.PasswordRequired => "Password required",
            AecDiagnostic.PasswordRejected => "Password rejected",
            _ => result.State switch { AecState.Enabled => "Protected", AecState.Disabled => "Unprotected", AecState.Maintenance => "Maintenance", _ => "Unknown" }
        };
        var tone = result.State switch { AecState.Enabled => "AccentBrush", AecState.Disabled or AecState.Maintenance => "WarningBrush", _ => "MutedBrush" };
        AecStateText.SetResourceReference(ForegroundProperty, tone);
        AecStateStripe.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, tone);
        // Like the Deep Freeze tab, the detail is shown only when it explains an unknown state.
        AecDetailText.Text = result.State == AecState.Unknown ? result.Detail : "";
        AecDetailText.Visibility = result.State == AecState.Unknown ? Visibility.Visible : Visibility.Collapsed;
        AecCheckedText.Text = $"Checked {aecCheckedAt:HH:mm:ss}{(demo ? " · simulated" : "")}";
        // As on the Deep Freeze tab, offer only the changes that apply to the current state.
        var state = result.State;
        SetAction(AecEnableButton, state != AecState.Enabled, primary: state is AecState.Disabled or AecState.Maintenance);
        SetAction(AecDisableButton, state != AecState.Disabled, primary: false);
        AecMaintenanceButton.Visibility = state == AecState.Maintenance ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool AecBlocked(bool changing)
    {
        ActionBlocker? blocker = busy ? new("An operation is still running", "Wait for the current request to finish, then try again.")
            : !admin && !demo ? new("Administrator access is required", "Reopen Heavy Rain as administrator.")
            : aecInstallation?.Path is null ? new("Anti-Executable control is unavailable", "AEC.exe was not found. Open installation and use Locate AEC.exe to select the executable supplied with Anti-Executable.")
            : changing && aecUncertain ? new("Verify the previous request first", "The last Anti-Executable request has an unknown outcome. Use Check status before sending another change.")
            : null;
        if (blocker is null) return false;
        if (!busy) AecPasswordInput.Clear();
        new ActionNoticeWindow(blocker) { Owner = this }.ShowDialog();
        return true;
    }

    private async void AecQuery_Click(object sender, RoutedEventArgs e)
    {
        if (AecBlocked(false)) return;
        SetBusy(true);
        var password = AecPasswordInput.Password;
        AecPasswordInput.Clear();
        try
        {
            var result = await aecClient.QueryAsync(aecInstallation!.Path!, password, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            SetActivity(false);
            ApplyAecStatus(result);
            Log($"Anti-Executable status: {AecStateText.Text}. {result.Detail}");
            if (result.State == AecState.Unknown)
            {
                var title = result.Diagnostic switch
                {
                    AecDiagnostic.ExternalUser => "Authorize your Windows account",
                    AecDiagnostic.PasswordRequired => "Enter your Anti-Executable password",
                    AecDiagnostic.PasswordRejected => "Anti-Executable password was rejected",
                    _ => "Anti-Executable status is unavailable"
                };
                new ActionNoticeWindow(new(title, result.Detail)) { Owner = this }.ShowDialog();
                if (result.Diagnostic is AecDiagnostic.PasswordRequired or AecDiagnostic.PasswordRejected && !lifetime.IsCancellationRequested)
                    _ = Dispatcher.BeginInvoke(() => AecPasswordInput.Focus());
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetActivity(false);
            ApplyAecStatus(new(AecState.Unknown, "The status query failed. Check Anti-Executable and try again."));
            Log($"Anti-Executable status query failed ({ex.GetType().Name}).");
            new ActionNoticeWindow(new("Anti-Executable status is unavailable", aecStatus.Detail)) { Owner = this }.ShowDialog();
        }
        finally { password = string.Empty; AecPasswordInput.Clear(); SetBusy(false); }
    }

    private async Task ChangeAecAsync(AecAction action)
    {
        if (AecBlocked(true)) return;
        var message = action switch
        {
            AecAction.Enable => "Enable Anti-Executable protection now? If Maintenance is active, this ends it and applies its learned changes.",
            AecAction.Disable => "Disable Anti-Executable protection now? Execution rules will stop being enforced. If Maintenance is active, this ends it and leaves protection off.",
            _ => "Start Maintenance for one hour? New or modified executables can be added to the allowed list. Protection resumes when the period ends."
        };
        SetBusy(true);
        timer.Stop();
        var password = AecPasswordInput.Password;
        AecPasswordInput.Clear();
        var sent = false;
        try
        {
            var confirmation = await this.ShowMessageAsync("Change Anti-Executable?", $"Computer: {Environment.MachineName}{(demo ? " (DEMO)" : "")}\n\n{message}\n\nLocal changes may differ from the Cloud policy.",
                MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings
                { AffirmativeButtonText = "Confirm change", NegativeButtonText = "Cancel", DefaultButtonFocus = MessageDialogResult.Negative });
            if (confirmation != MessageDialogResult.Affirmative || lifetime.IsCancellationRequested) return;
            sent = true;
            var result = await aecClient.ChangeAsync(aecInstallation!.Path!, password, action, allowMaintenanceDisable: action == AecAction.Disable, cancellationToken: lifetime.Token,
                verificationProgress: (attempt, observed) =>
                {
                    if (lifetime.IsCancellationRequested) return;
                    var state = observed.State switch { AecState.Enabled => "Protected", AecState.Disabled => "Unprotected", AecState.Maintenance => "Maintenance", _ => "Unknown" };
                    AecPendingText.Text = $"Verifying {action} · check {attempt} · {state}";
                    Log($"Anti-Executable {action}: verification {attempt}, {state} (AEC exit {observed.ExitCode?.ToString() ?? "unavailable"}).");
                });
            if (lifetime.IsCancellationRequested) return;
            SetActivity(false);
            ApplyAecStatus(result.Status ?? new(AecState.Unknown, "Check status to verify the current Anti-Executable state."));
            aecUncertain = result.Outcome == ChangeOutcome.Uncertain;
            aecPendingAction = aecUncertain ? action : null;
            AecPendingText.Text = $"{DateTime.Now:HH:mm:ss} · {result.Detail}";
            Log($"Anti-Executable {action}: {result.Outcome}. {result.Detail}");
            if (result.Outcome is ChangeOutcome.Rejected or ChangeOutcome.Uncertain)
                new ActionNoticeWindow(new("Anti-Executable change was not confirmed", result.Detail)) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetActivity(false);
            aecUncertain = sent;
            aecPendingAction = sent ? action : null;
            ApplyAecStatus(new(AecState.Unknown, "The operation could not be completed. Check status before retrying."));
            AecPendingText.Text = sent ? "The last request has an unknown outcome. It may have taken effect." : "No change was sent.";
            Log($"Anti-Executable operation failed ({ex.GetType().Name}). {AecPendingText.Text}");
            new ActionNoticeWindow(new("Anti-Executable request could not be completed", AecPendingText.Text)) { Owner = this }.ShowDialog();
        }
        finally
        {
            password = string.Empty; AecPasswordInput.Clear(); SetBusy(false);
            if (!lifetime.IsCancellationRequested) timer.Start();
        }
    }

    private async void AecEnable_Click(object sender, RoutedEventArgs e) => await ChangeAecAsync(AecAction.Enable);
    private async void AecDisable_Click(object sender, RoutedEventArgs e) => await ChangeAecAsync(AecAction.Disable);
    private async void AecMaintenance_Click(object sender, RoutedEventArgs e) => await ChangeAecAsync(AecAction.Maintenance);

    private async void AecBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select AEC.exe supplied with Anti-Executable", Filter = "Anti-Executable control (AEC.exe)|AEC.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true || !Path.GetFileName(dialog.FileName).Equals("AEC.exe", StringComparison.OrdinalIgnoreCase)) return;
        aecCustomPath = dialog.FileName;
        await RefreshAsync(true);
    }

    private async void AecDetect_Click(object sender, RoutedEventArgs e) { aecCustomPath = null; await RefreshAsync(true); }

    private void AecDocumentation_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://support.faronics.com/help/en-ca/46-anti-executable/216-what-is-the-command-line-syntax-for-faronics-anti-executable-enterprise") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Log("Windows could not open the documentation in a browser."); }
    }
}
