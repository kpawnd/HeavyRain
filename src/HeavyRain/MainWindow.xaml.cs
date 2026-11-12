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
        client = new DfcClient(demo ? new DemoRunner() : new DfcProcessRunner());
        MachineNameText.Text = Environment.MachineName;
        DemoBanner.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        timer.Tick += async (_, _) => { if (!busy && WindowState != WindowState.Minimized) await RefreshAsync(false); };
        StateChanged += async (_, _) => { if (timer.IsEnabled && WindowState != WindowState.Minimized && !busy) await RefreshAsync(false); };
        Closed += (_, _) => { timer.Stop(); lifetime.Cancel(); PasswordInput.Clear(); };
    }

    public async Task InitializeAsync(Action<string>? reportProgress = null)
    {
        Log(demo ? "Demo mode started. No DFC processes will run." : $"Local session on {Environment.MachineName}. {(admin ? "Administrator" : "Read-only")}.");
        reportProgress?.Invoke(admin ? "Administrator access confirmed." : "Running without administrator access.");
        reportProgress?.Invoke($"Runtime ready: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture}).");
        var libraries = new[] { "HeavyRain.Core", "MahApps.Metro", "ControlzEx", "Microsoft.Xaml.Behaviors", "PresentationFramework", "PresentationCore", "WindowsBase" };
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName()).OrderBy(assembly => assembly.Name))
            if (libraries.Contains(assembly.Name)) reportProgress?.Invoke($"Loaded {assembly.Name}.dll ({assembly.Version}).");
        reportProgress?.Invoke("Metro interface initialized.");
        if (demo) reportProgress?.Invoke("Demo mode: using sample data; no Deep Freeze queries will run.");
        await RefreshAsync(true, reportProgress);
        if (!lifetime.IsCancellationRequested) timer.Start();
    }

    private async Task RefreshAsync(bool log, Action<string>? reportProgress = null)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        SetBusy(true);
        try
        {
            var inspectMetadata = log || installation is null || DateTimeOffset.UtcNow - metadataCheckedAt >= TimeSpan.FromMinutes(5);
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
            DfcPathText.Text = installation.DfcPath ?? "DFC.exe was not found. Locate the vendor-supplied executable inside this PC.";
            InstallationText.Text = installation.Products.Length > 0 ? string.Join("\n", installation.Products)
                : installation.Evidence.Length > 0 ? string.Join("\n", installation.Evidence)
                : "No matching registration found. This is not proof that Deep Freeze is absent.";
            PolicyNameText.Text = policy.Name ?? "Unavailable";
            PolicySourceText.Text = policy.Detail;
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
                metadataCheckedAt = DateTimeOffset.UtcNow;
            }
            lifetime.Token.ThrowIfCancellationRequested();
            reportProgress?.Invoke($"Deep Freeze version: {deepFreezeVersion}.");
            VersionText.Text = deepFreezeVersion + (installation.DfcFileVersion is not "Unavailable" and not "Demo" ? $"\nDFC file: {installation.DfcFileVersion}" : "");
            checkedAt = DateTimeOffset.Now;
            RenderStatus();
            reportProgress?.Invoke(status.State == ProtectionState.Unknown ? "Ready. Protection could not be verified." : "Ready.");
            if (log || previous != status.State) Log($"Status: {status.State}. {status.Detail}" + (status.ExitCode is { } code ? $" (DFC {code})" : ""));
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
        finally { SetBusy(false); }
    }

    private void RenderStatus()
    {
        StateText.Text = status.State.ToString();
        StateText.Foreground = StateStripe.Background = (Brush)FindResource(status.State == ProtectionState.Unknown ? "MutedBrush" : "AccentBrush");
        StateDetailText.Text = status.Detail;
        CheckedText.Text = $"Checked {checkedAt:HH:mm:ss} · {Environment.MachineName} · {(demo ? "Simulated" : installation?.DfcPath is not null ? "DFC /ISFROZEN" : "No DFC interface")}";
    }

    private void SetBusy(bool value)
    {
        busy = value;
        RefreshButton.IsEnabled = BrowseButton.IsEnabled = DetectButton.IsEnabled = ExportButton.IsEnabled = !value;
        PasswordInput.IsEnabled = RestartCheck.IsEnabled = !value;
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (FreezeButton is null) return;
        FreezeButton.IsEnabled = ThawButton.IsEnabled = true;
        ControlHintText.Text = busy ? "An operation is in progress. Click an action for details."
            : "Changes require confirmation.";
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
        var restart = RestartCheck.IsChecked == true;
        var targetName = target.ToString().ToLowerInvariant();
        var message = $"Computer: {Environment.MachineName}{(demo ? " (DEMO)" : "")}\n\n" +
            (restart ? $"Request a restart into the {targetName} state now? Save all open work first.\n\n" : $"Request the {targetName} state for the next restart? This does not restart the PC now.\n\n") +
            (status.State == ProtectionState.Frozen ? "Restarting while frozen can discard changes on protected drives.\n\n" : "Freezing will protect the computer's current contents after restart.\n\n") +
            "The Deep Freeze command-line password is required. Cloud policy may need to be reapplied afterward.";
        timer.Stop();
        SetBusy(true);
        var confirmation = await this.ShowMessageAsync(restart ? "Restart and change protection?" : "Change the next boot?",
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
            ResultBanner.Visibility = Visibility.Visible;
            ResultText.Text = result.Detail;
            Log($"{target} {(restart ? "and restart" : "next boot")}: {result.Outcome}. {result.Detail}");
            if (result.Outcome == ChangeOutcome.Accepted)
                PendingText.Text = $"{target} requested at {DateTime.Now:HH:mm:ss}{(demo ? " (demo)" : "")}. DFC accepted the request; verify after restart. This is a session record, not a query of next-boot settings.";
            else if (result.Outcome == ChangeOutcome.Uncertain)
                PendingText.Text = $"{target} request has an unknown outcome. Verify next-boot settings in Deep Freeze before retrying.";
            if (result.Outcome is ChangeOutcome.Rejected or ChangeOutcome.Uncertain or ChangeOutcome.NotNeeded)
                new ActionNoticeWindow(new("The change could not be completed", result.Detail)) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            commandUncertain = true;
            ResultBanner.Visibility = Visibility.Visible;
            ResultText.Text = "The request could not be confirmed. Check Deep Freeze before retrying; it may already have applied the change.";
            PendingText.Text = "Last request outcome unknown. Verify next-boot settings in Deep Freeze.";
            Log($"Change outcome unknown ({ex.GetType().Name}).");
            new ActionNoticeWindow(new("The result could not be confirmed", ResultText.Text)) { Owner = this }.ShowDialog();
        }
        finally
        {
            password = string.Empty; PasswordInput.Clear(); RestartCheck.IsChecked = false; SetBusy(false);
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
            schemaVersion = 1, demo, computer = Environment.MachineName, os = Environment.OSVersion.VersionString,
            administrator = admin, checkedAt, state = status.State.ToString(), status.Detail, status.ExitCode,
            deepFreezeVersion, installation, policy, sessionBootRequest = PendingText.Text,
            note = "Registry evidence does not establish protection state. Next-boot settings are not queried. Passwords and raw DFC output are excluded.",
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
