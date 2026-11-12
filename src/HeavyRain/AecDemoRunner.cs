using HeavyRain.Core;

namespace HeavyRain;

internal sealed class AecDemoRunner : ICommandRunner
{
    private string state = "Protected";
    private DateTimeOffset? maintenanceEnds;

    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maintenanceEnds <= DateTimeOffset.UtcNow) { state = "Protected"; maintenanceEnds = null; }
        var command = arguments[0];
        if (command == "status") return Task.FromResult(new ProcessResult(0, Output: state));
        if (command == "version") return Task.FromResult(new ProcessResult(0, Output: "6.11.2112.730"));
        if (command == "protect") { state = arguments[1] == "on" ? "Protected" : "Unprotected"; maintenanceEnds = null; }
        if (command == "maintenance")
        {
            state = "Maintenance Mode";
            maintenanceEnds = DateTimeOffset.UtcNow.AddMinutes(AecClient.MaintenanceMinutes);
        }
        return Task.FromResult(new ProcessResult(0));
    }
}
