using System.Text.RegularExpressions;

namespace HeavyRain.Core;

public enum AecState { Unknown, Enabled, Disabled, Maintenance }
public enum AecAction { Enable, Disable, Maintenance }
public sealed record AecStatusResult(AecState State, string Detail, int? ExitCode = null, AecDiagnostic Diagnostic = AecDiagnostic.None);
public sealed record AecChangeResult(ChangeOutcome Outcome, string Detail, AecStatusResult? Status = null, int? ExitCode = null);

public sealed class AecClient(ICommandRunner runner, IAecDiagnosticReader? diagnosticReader = null)
{
    public const int MaintenanceMinutes = 60;

    public async Task<AecStatusResult> QueryAsync(string path, string password = "", CancellationToken cancellationToken = default)
    {
        if (!ValidPassword(password)) return new(AecState.Unknown, "The Anti-Executable password contains unsupported control characters or is too long.");
        var result = await RunAsync(path, ["status"], password, cancellationToken);
        if (diagnosticReader is not null && TransportSucceeded(result) && string.IsNullOrWhiteSpace(result.Output) && string.IsNullOrWhiteSpace(result.StandardError))
        {
            var diagnostic = await diagnosticReader.DiagnoseAsync(path, cancellationToken);
            if (diagnostic == AecDiagnostic.PasswordRejected) diagnostic = AecDiagnostic.PasswordRequired;
            if (AecDiagnostics.Describe(diagnostic) is { } explanation) return new(AecState.Unknown, explanation, result.ExitCode, diagnostic);
        }
        return ParseStatus(result, password.Length > 0);
    }

    public async Task<string?> GetVersionAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(path, ["version"], "", cancellationToken);
        if (!TransportSucceeded(result) || Failure(result) is not null) return null;
        var match = Regex.Match(result.Output.Trim(), @"\A(?:(?:Faronics\s+)?Anti-Executable\s+)?(?:Version\s*:?\s*)?(\d{1,3}\.\d{1,4}(?:\.\d{1,6}){0,2})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    public async Task<AecChangeResult> ChangeAsync(string path, string password, AecAction action,
        bool allowMaintenanceDisable = false, CancellationToken cancellationToken = default,
        Action<int, AecStatusResult>? verificationProgress = null)
    {
        if (!Enum.IsDefined(action)) return new(ChangeOutcome.Rejected, "Invalid Anti-Executable action.");
        if (!ValidPassword(password)) return new(ChangeOutcome.Rejected, "The Anti-Executable password contains unsupported control characters or is too long.");

        var current = await QueryAsync(path, password, cancellationToken);
        if (current.State == AecState.Unknown)
            return new(ChangeOutcome.Rejected, "No change sent. " + current.Detail, current);
        var desired = action switch { AecAction.Enable => AecState.Enabled, AecAction.Disable => AecState.Disabled, _ => AecState.Maintenance };
        if (current.State == desired)
            return new(ChangeOutcome.NotNeeded, action == AecAction.Maintenance
                ? "Maintenance is already active. Its duration was not changed."
                : "Anti-Executable is already in the requested state.", current);
        if (action == AecAction.Disable && current.State == AecState.Maintenance && !allowMaintenanceDisable)
            return new(ChangeOutcome.Rejected, "Disabling protection from Maintenance requires confirmation that Maintenance will end and protection will remain off.", current);
        if (cancellationToken.IsCancellationRequested)
            return new(ChangeOutcome.Rejected, "No change sent. The operation was cancelled.", current);

        List<string> arguments = action switch
        {
            AecAction.Enable => ["protect", "on"],
            AecAction.Disable => current.State == AecState.Maintenance ? ["protect", "off", "/force"] : ["protect", "off"],
            _ => ["maintenance", "/duration=" + MaintenanceMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)]
        };
        var result = await RunAsync(path, arguments, password, cancellationToken);
        if (result.TimedOut || result.Error is not null || result.ExitCode is null)
            return new(ChangeOutcome.Uncertain, "The request may have taken effect, but its result could not be read. Check status before retrying.", ExitCode: result.ExitCode);
        var failure = Failure(result, password.Length > 0);
        if (result.ExitCode != 0 || failure is not null)
            return new(ChangeOutcome.Rejected, failure ?? $"AEC rejected the request (exit {result.ExitCode}). Check the installed version and Anti-Executable permissions.", PasswordFailure(result, password.Length > 0), result.ExitCode);

        return await VerifyChangeAsync(path, password, action, desired, result.ExitCode, cancellationToken, verificationProgress);
    }

    private async Task<AecChangeResult> VerifyChangeAsync(string path, string password, AecAction action,
        AecState desired, int? commandExitCode, CancellationToken cancellationToken, Action<int, AecStatusResult>? verificationProgress)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var verified = new AecStatusResult(AecState.Unknown, "The requested state has not been verified.");
        var checks = 0;
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                var response = await RunAsync(path, ["status"], password, deadline.Token);
                checks++;
                deadline.Token.ThrowIfCancellationRequested();
                verified = TransportSucceeded(response) && string.IsNullOrWhiteSpace(response.Output) && string.IsNullOrWhiteSpace(response.StandardError)
                    ? new(AecState.Unknown, "AEC returned no status while the change was being verified.", response.ExitCode)
                    : ParseStatus(response, password.Length > 0);
                verificationProgress?.Invoke(checks, verified);
                if (verified.State == desired)
                    return new(ChangeOutcome.Accepted, action switch
                    {
                        AecAction.Enable => "Anti-Executable protection is enabled. Status was verified.",
                        AecAction.Disable => "Anti-Executable protection is disabled. Status was verified.",
                        _ => "Maintenance is active. One hour was requested; AEC status does not report the remaining time."
                    }, verified, commandExitCode);
                if (verified.Diagnostic is AecDiagnostic.PasswordRequired or AecDiagnostic.PasswordRejected || !TransportSucceeded(response)) break;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            verified = new(AecState.Unknown, "Status verification stopped before the requested state could be confirmed.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            verified = new(AecState.Unknown, "The status query failed while verifying the change.");
        }
        var observed = verified.State switch
        {
            AecState.Enabled => "AEC still reports Protected.",
            AecState.Disabled => "AEC still reports Unprotected.",
            AecState.Maintenance => "AEC still reports Maintenance.",
            _ => verified.Diagnostic is AecDiagnostic.PasswordRequired or AecDiagnostic.PasswordRejected ? verified.Detail
                : "AEC did not provide a readable current state."
        };
        return new(ChangeOutcome.Uncertain, $"{action} was requested but not verified after {checks} status {(checks == 1 ? "check" : "checks")}. {observed} Check status before retrying; the change command was sent only once.", verified, commandExitCode);
    }

    public static AecStatusResult ParseStatus(ProcessResult result, bool passwordSupplied = false)
    {
        if (result.TimedOut || result.Error is not null || result.ExitCode is null) return new(AecState.Unknown, result.TimedOut
            ? "AEC did not finish within the time limit. Check Anti-Executable before retrying."
            : result.Error is not null ? "AEC could not be run or its response could not be read. Check the executable and administrator access."
            : $"AEC did not return a successful status response (exit {result.ExitCode?.ToString() ?? "unavailable"}).", result.ExitCode);
        if (PasswordFailure(result, passwordSupplied) is { } passwordFailure) return passwordFailure;
        if (Failure(result, passwordSupplied) is { } failure) return new(AecState.Unknown, failure, result.ExitCode);
        if (result.ExitCode != 0) return new(AecState.Unknown, $"AEC did not return a successful status response (exit {result.ExitCode}).", result.ExitCode);
        var states = new HashSet<AecState>();
        var lines = (result.Output + "\n" + result.StandardError).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var normalized = Regex.Replace(line, @"\A(?:(?:Anti-Executable\s+)?(?:Protection\s+)?(?:Status|State)\s*:\s*)", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim().TrimEnd('.');
            var state = normalized.ToLowerInvariant() switch
            {
                "protected" or "enabled" or "protection on" or "protection enabled" => AecState.Enabled,
                "unprotected" or "disabled" or "protection off" or "protection disabled" => AecState.Disabled,
                "maintenance" or "maintenance mode" => AecState.Maintenance,
                _ => AecState.Unknown
            };
            if (state != AecState.Unknown) states.Add(state);
            else if (line.Length > 0) return new(AecState.Unknown, "AEC returned an unrecognized status format. Check status in Anti-Executable; no protection state was inferred.", result.ExitCode);
        }
        if (states.Count != 1) return new(AecState.Unknown, lines.Length == 0
            ? "AEC returned no readable status. Your Windows account must be authorized in Anti-Executable User Settings. An AE password is an additional requirement when configured; it does not replace account authorization."
            : "AEC returned conflicting states. Check status in Anti-Executable before making changes.", result.ExitCode);
        var known = states.Single();
        return new(known, known switch
        {
            AecState.Enabled => "Anti-Executable is enforcing its execution rules.",
            AecState.Disabled => "Anti-Executable execution protection is off.",
            _ => "Maintenance is active. New or changed executables can be added to the allowed list."
        }, result.ExitCode);
    }

    private async Task<ProcessResult> RunAsync(string path, List<string> arguments, string password, CancellationToken cancellationToken)
    {
        if (password.Length > 0) arguments.Add("/pw=" + password);
        try { return await runner.RunAsync(path, arguments, cancellationToken); }
        finally { arguments.Clear(); }
    }

    private static bool ValidPassword(string password) => password.Length <= 255 && !password.Any(char.IsControl);
    private static bool TransportSucceeded(ProcessResult result) => !result.TimedOut && result.Error is null && result.ExitCode == 0;

    private static AecStatusResult? PasswordFailure(ProcessResult result, bool passwordSupplied)
    {
        var output = result.Output + "\n" + result.StandardError;
        if (!Regex.IsMatch(output, @"\b(?:Enter\s+(?:AE\s*)?(?:(?:Administrator|Admin|Trusted\s*User)\s+)?Password\s*:|Invalid\s+(?:AE\s*)?(?:(?:Administrator|Admin|Trusted\s*User)\s+)?Password\b|Password\s+(?:is\s+)?(?:incorrect|invalid|not valid)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return null;
        var role = Regex.IsMatch(output, @"\b(?:AE\s*)?Trusted\s*User\s+Password\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "AE Trusted User password"
            : Regex.IsMatch(output, @"\b(?:AE\s*)?(?:Administrator|Admin)\s+Password\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "AE Administrator password" : "Anti-Executable password";
        var diagnostic = passwordSupplied ? AecDiagnostic.PasswordRejected : AecDiagnostic.PasswordRequired;
        var detail = passwordSupplied
            ? $"AEC did not accept the supplied {role}. Check that password in your policy's User Settings, then enter it again. This is separate from your Windows login and Deep Freeze password."
            : $"AEC is requesting the {role}. Enter it from your policy's User Settings, then choose Check status. This is separate from your Windows login and Deep Freeze password. Automatic checks do not retain passwords.";
        return new(AecState.Unknown, detail, result.ExitCode, diagnostic);
    }

    private static string? Failure(ProcessResult result, bool passwordSupplied = false)
    {
        if (PasswordFailure(result, passwordSupplied) is { } passwordFailure) return passwordFailure.Detail;
        var output = result.Output + "\n" + result.StandardError;
        if (Regex.IsMatch(output, @"password|appropriate right|access denied|not authorized|unauthori[sz]ed|permission|administrator", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "AEC requires an authorized Anti-Executable account and its AE password when configured. Windows elevation or an AE password alone does not grant account access.";
        if (Regex.IsMatch(output, @"\b(failed|failure|unable|error|cannot|invalid|usage)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "AEC could not complete the command. Check Anti-Executable permissions, Cloud policy, and Deep Freeze maintenance compatibility.";
        return null;
    }
}
