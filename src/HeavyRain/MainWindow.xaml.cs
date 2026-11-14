using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HeavyRain.Core;
using Microsoft.Win32;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;

namespace HeavyRain;

public partial class MainWindow : MetroWindow
{
    private const string DocsUrl = "https://docs.faronics.com/deep-freeze-cloud/using-deep-freeze-cloud-console/deep-freeze-service/advanced-settings-tab/deep-freeze-command-line-syntax";
    private readonly bool demo = Environment.GetCommandLineArgs().Contains("--demo");
    private readonly bool admin = InstallationProbe.IsAdministrator();
    private readonly DfcClient client;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<string> activity = [];
    private InstallationInfo? installation;
    private StatusResult status = new(ProtectionState.Unknown, "Not queried yet", null);
    private DateTimeOffset? checkedAt;
    private string? customPath;
    private bool busy;
    private bool commandUncertain;
    private string deepFreezeVersion = "Unavailable";
    private CloudPolicyInfo policy = CloudPolicyInfo.Unavailable("Not queried yet.");
    private DateTimeOffset metadataCheckedAt = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        client = new DfcClient(demo ? new DemoRunner() : new CommandProcessRunner());
        aecClient = new AecClient(demo ? new AecDemoRunner() : new CommandProcessRunner(), demo ? null : new AecDiagnosticProbe(), demo ? null : CreateAecProviderReader());
        // The demo shows a sample name, so screenshots of it don't carry the real computer's name.
        MachineNameText.Text = demo ? "LAB-PC-07" : Environment.MachineName;
        DemoBanner.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        timer.Tick += async (_, _) => { if (!busy && WindowState != WindowState.Minimized) await RefreshAsync(false); };
        StateChanged += async (_, _) => { if (timer.IsEnabled && WindowState != WindowState.Minimized && !busy) await RefreshAsync(false); };
        Closed += (_, _) => { timer.Stop(); lifetime.Cancel(); PasswordInput.Clear(); AecPasswordInput.Clear(); ActivityProgress.IsIndeterminate = false; ActivityProgress.Visibility = Visibility.Collapsed; };
    }

    // The Faronics Anti-Executable WMI provider is read through Windows PowerShell's CIM cmdlets,
    // which are present on every supported Windows build. If PowerShell cannot be located the
    // reader is omitted and Heavy Rain falls back to the AEC.exe command line alone.
    private static IAecProviderReader? CreateAecProviderReader()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(powerShell) ? new AecProviderReader(new CommandProcessRunner(TimeSpan.FromSeconds(20)), powerShell) : null;
    }

    public async Task InitializeAsync(Action<string>? reportProgress = null)
    {
        Log(demo ? "Demo mode started. No vendor processes will run." : $"Local session on {Environment.MachineName}. {(admin ? "Administrator" : "Read-only")}.");
        reportProgress?.Invoke(admin ? "Administrator access confirmed." : "Running without administrator access.");
        reportProgress?.Invoke($"Runtime ready: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture}).");
        var libraries = new[] { "HeavyRain.Core", "MahApps.Metro", "ControlzEx", "Microsoft.Xaml.Behaviors", "PresentationFramework", "PresentationCore", "WindowsBase" };
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName()).OrderBy(assembly => assembly.Name))
            if (libraries.Contains(assembly.Name)) reportProgress?.Invoke($"Loaded {assembly.Name}.dll ({assembly.Version}).");
        reportProgress?.Invoke("Metro interface initialized.");
        if (demo) reportProgress?.Invoke("Demo mode: using sample data; no vendor commands will run.");
        await RefreshAsync(true, reportProgress);
        if (!lifetime.IsCancellationRequested) timer.Start();
    }

    private async Task RefreshAsync(bool log, Action<string>? reportProgress = null)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        SetBusy(true);
        Task aecTask = Task.CompletedTask;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            lifetime.Token.ThrowIfCancellationRequested();
            var inspectMetadata = log || installation is null || DateTimeOffset.UtcNow - metadataCheckedAt >= TimeSpan.FromMinutes(5);
            aecTask = RefreshAecAsync(inspectMetadata, log, reportProgress);
            var previousPolicy = policy;
            var policyTask = demo ? Task.FromResult(new CloudPolicyInfo("Sample policy", "Demo", "Simulated policy name."))
                : Task.Run(() => CloudPolicyProbe.Inspect(reportProgress, lifetime.Token), lifetime.Token);
            reportProgress?.Invoke(demo ? "Loading the sample installation…" : "Searching for the local Deep Freeze installation…");
            var installationTask = demo
                ? Task.FromResult(new InstallationInfo("Demo — no executable", "Demo", ["Deep Freeze Cloud (sample)"], ["Sample installation evidence"], []))
                : inspectMetadata ? Task.Run(() => InstallationProbe.Inspect(customPath, reportProgress, lifetime.Token), lifetime.Token)
                : Task.FromResult(installation!);
            await Task.WhenAll(installationTask, policyTask);
            installation = await installationTask;
            policy = await policyTask;
            lifetime.Token.ThrowIfCancellationRequested();
            DfcPathText.Text = installation.DfcPath ?? "Not found";
            InstallationText.Text = installation.Products.Length > 0 ? string.Join("\n", installation.Products)
                : installation.Evidence.Length > 0 ? string.Join("\n", installation.Evidence)
                : "No matching registration found";
            PolicyNameText.Text = policy.Name ?? "Unavailable";
            PolicySourceText.Text = policy.Name is null ? policy.Detail : "";
            PolicySourceText.Visibility = policy.Name is null ? Visibility.Visible : Visibility.Collapsed;
            PolicyNameText.ToolTip = policy.Source;
            reportProgress?.Invoke(policy.Name is { } name ? $"Policy: {name} ({policy.Source})." : $"Policy unavailable. {policy.Detail}");
            if (log || previousPolicy.Name != policy.Name || previousPolicy.Source != policy.Source)
            {
                Log(policy.Name is { } policyName ? $"Policy: {policyName}. Source: {policy.Source}." : $"Policy unavailable. {policy.Detail}");
                if (policy.Registry is { } registry)
                    Log($"Policy registry scan: {registry.Keys.Length} keys, {registry.Fields.Length} policy fields, {registry.Candidates.Length} name candidates. Details are included in diagnostic reports.");
            }
            var previous = status.State;
            reportProgress?.Invoke(demo ? "Loading the sample protection state…" : installation.DfcPath is not null
                ? "Running DFC.exe get /ISFROZEN…" : "Skipping the protection query: DFC.exe is unavailable.");
            status = installation.DfcPath is { } path ? await client.QueryAsync(path, lifetime.Token)
                : new(ProtectionState.Unknown, "The Deep Freeze control interface is unavailable. A Cloud agent may be present without the Deep Freeze protection component.", null);
            lifetime.Token.ThrowIfCancellationRequested();
            reportProgress?.Invoke($"Protection: {status.State}." + (status.State == ProtectionState.Unknown ? $" {status.Detail}" : ""));
            if (inspectMetadata)
            {
                reportProgress?.Invoke(demo ? "Loading the sample version…" : installation.DfcPath is not null
                    ? "Running DFC.exe get /VERSION…" : "Skipping the version query: DFC.exe is unavailable.");
                deepFreezeVersion = installation.DfcPath is { } versionPath ? await client.GetVersionAsync(versionPath, lifetime.Token) : "Unavailable";
                if (installation.DfcPath is not null) reportProgress?.Invoke(demo ? "Loading the sample licence…" : "Running DFC.exe get /LICENSETYPE and /LICENSESTATUS…");
                license = installation.DfcPath is { } licensePath ? await client.GetLicenseAsync(licensePath, lifetime.Token) : null;
                RenderLicense();
                reportProgress?.Invoke(license is null ? "Licence: not reported by DFC." : $"Licence: {license.Describe(DateOnly.FromDateTime(DateTime.Today))}.");
                metadataCheckedAt = DateTimeOffset.UtcNow;
            }
            lifetime.Token.ThrowIfCancellationRequested();
            reportProgress?.Invoke($"Deep Freeze version: {deepFreezeVersion}.");
            VersionText.Text = deepFreezeVersion;
            VersionText.ToolTip = installation.DfcFileVersion is not "Unavailable" and not "Demo" ? $"DFC.exe file version {installation.DfcFileVersion}" : null;
            checkedAt = DateTimeOffset.Now;
            RenderStatus();
            if (log || previous != status.State) Log($"Status: {status.State}. {status.Detail}" + (status.ExitCode is { } code ? $" (DFC {code})" : ""));
            await RefreshInsightsAsync(reportProgress);
            await aecTask;
            reportProgress?.Invoke("Ready.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            status = new(ProtectionState.Unknown, "The query could not be completed. Refresh to try again.", null);
            checkedAt = DateTimeOffset.Now;
            RenderStatus();
            Log($"Query failed ({ex.GetType().Name}).");
            reportProgress?.Invoke($"Query failed ({ex.GetType().Name}); protection is unknown.");
        }
        finally { await aecTask; SetBusy(false); }
    }

    private void RenderStatus()
    {
        StateText.Text = status.State.ToString();
        var tone = status.State switch { ProtectionState.Frozen => "AccentBrush", ProtectionState.Thawed => "WarningBrush", _ => "MutedBrush" };
        StateText.SetResourceReference(ForegroundProperty, tone);
        StateStripe.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, tone);
        // The detail only earns its space when it explains why the state is unknown.
        StateDetailText.Text = status.State == ProtectionState.Unknown ? status.Detail : "";
        StateDetailText.Visibility = status.State == ProtectionState.Unknown ? Visibility.Visible : Visibility.Collapsed;
        CheckedText.Text = $"Checked {checkedAt:HH:mm:ss}{(demo ? " · simulated" : "")}";
        // DFC can only switch to the opposite state, so offer just that action and highlight it.
        // When the state is unknown both stay available; the action then explains why it can't run.
        SetAction(ThawButton, status.State != ProtectionState.Thawed, primary: status.State == ProtectionState.Frozen);
        SetAction(FreezeButton, status.State != ProtectionState.Frozen, primary: status.State == ProtectionState.Thawed);
        RenderStateNote();
        RenderPending();
    }

    private void SetAction(System.Windows.Controls.Button button, bool visible, bool primary)
    {
        button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : typeof(System.Windows.Controls.Button));
    }

    /// <summary>This session's request, shown only when Deep Freeze's own records can't be read.</summary>
    private void SetLastRequest(string text)
    {
        sessionRequest = text;
        RenderPending();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        SetActivity(value);
        RefreshButton.IsEnabled = BrowseButton.IsEnabled = DetectButton.IsEnabled = ExportButton.IsEnabled = !value;
        PasswordInput.IsEnabled = RestartSwitch.IsEnabled = !value;
        AecPasswordInput.IsEnabled = AecBrowseButton.IsEnabled = AecDetectButton.IsEnabled = !value;
        UpdateControls();
    }

    private void SetActivity(bool value)
    {
        ActivityProgress.IsIndeterminate = value;
        ActivityProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateControls()
    {
        if (FreezeButton is null) return;
        FreezeButton.IsEnabled = ThawButton.IsEnabled = true;
    }

    private async Task ChangeAsync(BootTarget target)
    {
        using var securePassword = PasswordInput.SecurePassword;
        var blocker = BootActionGuard.Check(target, busy, commandUncertain, admin || demo,
            installation?.DfcPath is not null, status.State, status.Detail, securePassword.Length);
        if (blocker is not null)
        {
            new ActionNoticeWindow(blocker) { Owner = this }.ShowDialog();
            return;
        }
        var path = installation!.DfcPath!;
        var restart = RestartSwitch.IsOn;
        var thaw = target == BootTarget.Thawed;
        var message = $"Computer: {Environment.MachineName}{(demo ? " (DEMO)" : "")}\n\n" +
            (thaw
                ? (restart ? "Restart now into the Thawed state? It stays Thawed until you freeze it. Save all open work first; this restart discards unsaved changes on frozen drives."
                           : "Thaw at the next restart? It stays Thawed until you freeze it. This doesn't restart the PC now.")
                : (restart ? "Restart now into the Frozen state? Save all open work first."
                           : "Freeze at the next restart? This doesn't restart the PC now. Freezing protects the PC's contents as they are at that restart."));
        timer.Stop();
        SetBusy(true);
        var confirmation = await this.ShowMessageAsync(thaw ? (restart ? "Restart thawed?" : "Thaw at next restart?") : restart ? "Restart frozen?" : "Freeze at next restart?",
            message, MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings
            {
                AffirmativeButtonText = restart ? "Confirm restart" : "Confirm change",
                NegativeButtonText = "Cancel", DefaultButtonFocus = MessageDialogResult.Negative
            });
        if (confirmation != MessageDialogResult.Affirmative)
        {
            PasswordInput.Clear(); SetBusy(false); timer.Start(); return;
        }
        SetBusy(true);
        var password = PasswordInput.Password;
        PasswordInput.Clear();
        try
        {
            var result = await client.ChangeAsync(path, password, target, restart, lifetime.Token);
            password = string.Empty;
            commandUncertain = result.Outcome == ChangeOutcome.Uncertain;
            Log($"{target} {(restart ? "and restart" : "next boot")}: {result.Outcome}. {result.Detail}");
            // Successes show in Last request; problems show once, in a dialog, not also on the page.
            if (result.Outcome == ChangeOutcome.Accepted)
                SetLastRequest((thaw ? "Thawed" : "Frozen") + (restart ? ", restarting now" : " from next restart") + $" · {DateTime.Now:HH:mm:ss}{(demo ? " (demo)" : "")}");
            if (result.RestartFailed)
                new ActionNoticeWindow(new("Restart the PC to apply the change", result.Detail)) { Owner = this }.ShowDialog();
            else if (result.Outcome == ChangeOutcome.Uncertain)
                SetLastRequest($"{target} · outcome unknown · {DateTime.Now:HH:mm:ss}");
            if (result.Outcome is ChangeOutcome.Rejected or ChangeOutcome.Uncertain or ChangeOutcome.NotNeeded)
                new ActionNoticeWindow(new(result.Outcome == ChangeOutcome.Rejected ? "Deep Freeze didn't accept the request" : "The change could not be completed", result.Detail)) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            commandUncertain = true;
            SetLastRequest($"{target} · outcome unknown · {DateTime.Now:HH:mm:ss}");
            Log($"Change outcome unknown ({ex.GetType().Name}).");
            new ActionNoticeWindow(new("The result could not be confirmed", "Check Deep Freeze before retrying; it may already have applied the change.")) { Owner = this }.ShowDialog();
        }
        finally
        {
            password = string.Empty; PasswordInput.Clear(); RestartSwitch.IsOn = false; SetBusy(false);
            if (!lifetime.IsCancellationRequested) timer.Start();
        }
        await RefreshAsync(false);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(true);
    private async void Freeze_Click(object sender, RoutedEventArgs e) => await ChangeAsync(BootTarget.Frozen);
    private async void Thaw_Click(object sender, RoutedEventArgs e) => await ChangeAsync(BootTarget.Thawed);
    private void Password_Changed(object sender, RoutedEventArgs e) => UpdateControls();

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select the Faronics DFC.exe supplied with this installation", Filter = "Deep Freeze control (DFC.exe)|DFC.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true || !Path.GetFileName(dialog.FileName).Equals("DFC.exe", StringComparison.OrdinalIgnoreCase)) return;
        customPath = dialog.FileName;
        Log("A custom DFC location was selected. Use only the executable supplied by Faronics.");
        await RefreshAsync(true);
    }

    private async void Detect_Click(object sender, RoutedEventArgs e) { customPath = null; await RefreshAsync(true); }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"HeavyRain-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.json", Filter = "JSON diagnostic report|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var report = new
        {
            schemaVersion = 3, demo, computer = Environment.MachineName, os = Environment.OSVersion.VersionString,
            administrator = admin, checkedAt, state = status.State.ToString(), status.Detail, status.ExitCode,
            deepFreezeVersion, installation, policy, sessionBootRequest = sessionRequest, license,
            deepFreeze = insights is null ? null : new
            {
                pending = pendingBoot, thawed = thawedRun, lastBoot = insights.LastBoot,
                history = DeepFreezeHistory.Timeline(insights.Entries, HistoryLimit),
                schedules = insights.Policy?.Schedules, checkInMinutes = insights.Policy?.CheckInMinutes, persistEventLogs = insights.Policy?.PersistEventLogs,
                policyLastModified = insights.Policy?.LastModified, components = insights.Components,
                problems = new[] { insights.EventProblem, insights.CloudProblem, insights.ComponentProblem }.Where(problem => problem is not null)
            },
            antiExecutable = new { state = aecStatus.State.ToString(), aecStatus.Detail, aecStatus.ExitCode, diagnostic = aecStatus.Diagnostic.ToString(), checkedAt = aecCheckedAt, version = aecVersion, installation = aecInstallation, uncertain = aecUncertain, sessionRequest = AecPendingText.Text, policy = aecPolicyInfo?.Policy },
            note = "Registry evidence does not establish protection state. The pending change comes from Deep Freeze's event log. Passwords, raw command output, Cloud task bodies and the raw policy cache are excluded.",
            activity = activity.ToArray()
        };
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Log("Report saved. It includes this computer's name, installation paths and session activity.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Report could not be saved. Choose a writable location."); }
    }

    private void Documentation_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(DocsUrl) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Log("Windows could not open the documentation in a browser."); }
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/kpawnd") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Log("Windows could not open the GitHub profile in a browser."); }
    }

    private void Log(string message)
    {
        activity.Add($"{DateTimeOffset.Now:HH:mm:ss}  {message}");
        if (activity.Count > 150) activity.RemoveAt(0);
        ActivityText.Text = string.Join(Environment.NewLine, activity);
        ActivityText.ScrollToEnd();
    }
}
