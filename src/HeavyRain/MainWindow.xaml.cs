using System.Diagnostics;
using System.IO;
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

    public MainWindow()
    {
        InitializeComponent();
        client = new DfcClient(demo ? new DemoRunner() : new DfcProcessRunner());
        MachineNameText.Text = Environment.MachineName;
        DemoBanner.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        timer.Tick += async (_, _) => { if (!busy) await RefreshAsync(false); };
        Closed += (_, _) => { timer.Stop(); lifetime.Cancel(); PasswordInput.Clear(); };
    }

    public async Task InitializeAsync(Action<string>? reportProgress = null)
    {
        Log(demo ? "Demo mode started. No DFC processes will run." : $"Local session on {Environment.MachineName}. {(admin ? "Administrator" : "Read-only")}.");
        await RefreshAsync(true, reportProgress);
        if (!lifetime.IsCancellationRequested) timer.Start();
    }

    private async Task RefreshAsync(bool log, Action<string>? reportProgress = null)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        SetBusy(true, "Querying this computer…");
        try
        {
            reportProgress?.Invoke("Finding the local Deep Freeze installation…");
            installation = demo
                ? new("Demo — no executable", "Demo", ["Deep Freeze Cloud (sample)"], ["Sample installation evidence"], [])
                : await Task.Run(() => InstallationProbe.Inspect(customPath));
            DfcPathText.Text = installation.DfcPath ?? "DFC.exe was not found. Locate the vendor-supplied executable inside this PC.";
            InstallationText.Text = installation.Products.Length > 0 ? string.Join("\n", installation.Products)
                : installation.Evidence.Length > 0 ? string.Join("\n", installation.Evidence)
                : "No matching registration found. This is not proof that Deep Freeze is absent.";
            reportProgress?.Invoke("Reading the local Cloud policy…");
            policy = demo ? new("Sample policy", "Demo", "Simulated policy name.") : await Task.Run(CloudPolicyProbe.Inspect);
            PolicyNameText.Text = policy.Name ?? "Unavailable";
            PolicySourceText.Text = policy.Detail;
            PolicyNameText.ToolTip = policy.Source;
            var previous = status.State;
            reportProgress?.Invoke("Checking the current protection state…");
            status = installation.DfcPath is { } path ? await client.QueryAsync(path, lifetime.Token)
                : new(ProtectionState.Unknown, "The Deep Freeze control interface is unavailable. A Cloud agent may be present without the Deep Freeze protection component.", null);
            reportProgress?.Invoke("Reading version information…");
            deepFreezeVersion = installation.DfcPath is { } versionPath ? await client.GetVersionAsync(versionPath, lifetime.Token) : "Unavailable";
            VersionText.Text = deepFreezeVersion + (installation.DfcFileVersion is not "Unavailable" and not "Demo" ? $"\nDFC file: {installation.DfcFileVersion}" : "");
            checkedAt = DateTimeOffset.Now;
            RenderStatus();
            reportProgress?.Invoke("Ready. Opening your dashboard…");
            if (log || previous != status.State) Log($"Status: {status.State}. {status.Detail}" + (status.ExitCode is { } code ? $" (DFC {code})" : ""));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            status = new(ProtectionState.Unknown, "The query could not be completed. Refresh to try again.", null);
            checkedAt = DateTimeOffset.Now;
            RenderStatus();
            Log($"Query failed ({ex.GetType().Name}).");
        }
        finally { SetBusy(false); }
    }

    private void RenderStatus()
    {
        StateText.Text = status.State.ToString();
        var color = status.State switch { ProtectionState.Frozen => "#B69AFF", ProtectionState.Thawed => "#E4B6F3", _ => "#B6ADC6" };
        StateText.Foreground = StateStripe.Background = (Brush)new BrushConverter().ConvertFromString(color)!;
        StateDetailText.Text = status.Detail;
        CheckedText.Text = $"Checked {checkedAt:HH:mm:ss} · {Environment.MachineName} · {(demo ? "Simulated" : "DFC /ISFROZEN")}";
    }

    private void SetBusy(bool value, string? label = null)
    {
        busy = value;
        RefreshButton.IsEnabled = BrowseButton.IsEnabled = DetectButton.IsEnabled = ExportButton.IsEnabled = !value;
        PasswordInput.IsEnabled = RestartCheck.IsEnabled = !value;
        FooterText.Text = label ?? $"Refreshes every 30 seconds · {(admin ? "Administrator" : "Standard user")} · Local control only";
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (FreezeButton is null) return;
        FreezeButton.IsEnabled = ThawButton.IsEnabled = true;
        ControlHintText.Text = busy ? "An operation is in progress. Click an action for details."
            : "Choose an action. Heavy Rain will explain anything needed before it can proceed.";
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
        SetBusy(true, "Awaiting confirmation…");
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
        SetBusy(true, $"Requesting {targetName}…");
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

    private void Log(string message)
    {
        activity.Add($"{DateTimeOffset.Now:HH:mm:ss}  {message}");
        if (activity.Count > 150) activity.RemoveAt(0);
        ActivityText.Text = string.Join(Environment.NewLine, activity);
        ActivityText.ScrollToEnd();
    }
}
