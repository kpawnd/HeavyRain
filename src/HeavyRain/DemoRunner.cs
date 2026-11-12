using HeavyRain.Core;

namespace HeavyRain;

internal sealed class DemoRunner : ICommandRunner
{
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        => Task.FromResult(arguments[1] switch
        {
            "/ISFROZEN" => new ProcessResult(1),
            "/VERSION" => new ProcessResult(0, Output: "Demo 9.0.0"),
            _ => new ProcessResult(0)
        });
}
