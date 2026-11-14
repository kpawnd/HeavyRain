using System.Globalization;
using HeavyRain.Core;

namespace HeavyRain;

internal sealed class DemoRunner : ICommandRunner
{
    // The sample licence expires in 24 days, so the demo shows the expiry warning.
    private static string Expiry => DateTime.Today.AddDays(24).ToString("dddd, MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));

    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        => Task.FromResult(arguments[1] switch
        {
            "/ISFROZEN" => new ProcessResult(0),   // Thawed, so the demo shows the Thawed run and a pending freeze
            "/VERSION" => new ProcessResult(0, Output: "Demo 9.0.0"),
            "/LICENSETYPE" => new ProcessResult(0, Output: $"License Type: Subscription\r\nExpiry Date: {Expiry}"),
            "/LICENSESTATUS" => new ProcessResult(0, Output: $"License Status: Licensed\r\nExpiry Date: {Expiry}"),
            _ => new ProcessResult(0)
        });
}
