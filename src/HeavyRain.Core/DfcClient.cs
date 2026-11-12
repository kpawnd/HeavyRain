namespace HeavyRain.Core;

public enum ProtectionState { Unknown, Frozen, Thawed }
public enum BootTarget { Frozen, Thawed }
public enum ChangeOutcome { Accepted, Rejected, Uncertain, NotNeeded }

public sealed record ProcessResult(int? ExitCode, bool TimedOut = false, string Output = "", string? Error = null, string StandardError = "");
public sealed record StatusResult(ProtectionState State, string Detail, int? ExitCode);
public sealed record ChangeResult(ChangeOutcome Outcome, string Detail, int? ExitCode = null);

public interface ICommandRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed class DfcClient(ICommandRunner runner)
{
    public async Task<StatusResult> QueryAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(path, ["get", "/ISFROZEN"], cancellationToken);
        if (result.TimedOut || result.Error is not null || result.ExitCode is null)
            return new(ProtectionState.Unknown, DescribeFailure(result), result.ExitCode);
        return result.ExitCode switch
        {
            0 => new(ProtectionState.Thawed, "Changes to thawed drives can persist after a restart.", 0),
            1 => new(ProtectionState.Frozen, "Deep Freeze reports that this computer is currently frozen.", 1),
            _ => new(ProtectionState.Unknown, DescribeFailure(result), result.ExitCode)
        };
    }

    public async Task<string> GetVersionAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(path, ["get", "/VERSION"], cancellationToken);
        if (result.TimedOut || result.Error is not null || result.ExitCode != 0) return "Unavailable";
        var match = System.Text.RegularExpressions.Regex.Match(result.Output, @"\b\d{1,3}\.\d{1,4}(?:\.\d{1,6}){0,2}\b");
        return match.Success ? match.Value : "Not returned by DFC";
    }

    public async Task<ChangeResult> ChangeAsync(string path, string password, BootTarget target, bool restartNow,
        CancellationToken cancellationToken = default)
    {
        if (password.Length is < 1 or > 63 || password.Any(char.IsControl))
            return new(ChangeOutcome.Rejected, "Enter a 1–63 character Deep Freeze password with command-line rights. Control characters are not supported.");
        if (!Enum.IsDefined(target)) return new(ChangeOutcome.Rejected, "Invalid boot target.");

        var current = await QueryAsync(path, cancellationToken);
        if (current.State == ProtectionState.Unknown)
            return new(ChangeOutcome.Rejected, "No change sent: current protection could not be verified. " + current.Detail, current.ExitCode);
        if ((target == BootTarget.Frozen && current.State == ProtectionState.Frozen) ||
            (target == BootTarget.Thawed && current.State == ProtectionState.Thawed))
            return new(ChangeOutcome.NotNeeded, "No change sent: the computer is already in that state. DFC only supports a transition from the opposite current state; this does not verify or cancel an earlier next-boot request.");

        string command = (target, restartNow) switch
        {
            (BootTarget.Frozen, false) => "/FREEZENEXTBOOT",
            (BootTarget.Thawed, false) => "/THAWNEXTBOOT",
            (BootTarget.Frozen, true) => "/BOOTFROZEN",
            (BootTarget.Thawed, true) => "/BOOTTHAWED",
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        var result = await runner.RunAsync(path, [password, command], cancellationToken);
        if (result.TimedOut || result.Error is not null || result.ExitCode is null)
            return new(ChangeOutcome.Uncertain,
                "Command outcome is unknown. It may already have been applied. Check Deep Freeze before retrying. " + DescribeFailure(result), result.ExitCode);
        return result.ExitCode == 0
            ? new(ChangeOutcome.Accepted, restartNow
                ? $"DFC accepted the request to restart {target.ToString().ToLowerInvariant()}. Confirm the current state after the restart."
                : $"DFC accepted {target.ToString().ToLowerInvariant()} for the next boot. The current protection state has not changed. Next-boot configuration is not independently queried.", 0)
            : new(ChangeOutcome.Rejected, "DFC did not report success. " + DescribeFailure(result), result.ExitCode);
    }

    public static string DescribeFailure(ProcessResult result) => result switch
    {
        { TimedOut: true } => "DFC did not finish within the time limit.",
        { Error: not null } => result.Error,
        { ExitCode: 2 } => "Administrator rights are required. Reopen this utility as administrator.",
        { ExitCode: 3 } => "This command is not valid on this installation. Check the Deep Freeze edition and command-line password configuration.",
        { ExitCode: 4 } => "DFC rejected the command as invalid. Check that DFC matches the installed Deep Freeze version.",
        { ExitCode: 1 } => "DFC returned 1. This means Frozen only for a status query, not success for a change.",
        { ExitCode: >= 5 } => $"DFC reported internal error {result.ExitCode}. Check the command-line password and the installation; consult Faronics if it persists.",
        _ => $"DFC returned an unrecognized result ({result.ExitCode?.ToString() ?? "no exit code"})."
    };
}
