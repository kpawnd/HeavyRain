namespace HeavyRain.Core;

/// <summary>
/// Reads the Anti-Executable provider state by running a single read-only CIM query through
/// Windows PowerShell and parsing its JSON. This avoids an extra runtime dependency and uses the
/// same guarded process runner as the rest of the toolkit. Any failure (no provider, access
/// denied, PowerShell missing, timeout) yields <c>null</c>, which leaves the AEC.exe verdict in
/// place.
/// </summary>
public sealed class AecProviderReader(ICommandRunner runner, string powerShellPath) : IAecProviderReader
{
    // Emits exactly one compact JSON object: {"present":false}, {"error":true}, or the reading.
    private const string Query =
        "$ErrorActionPreference='Stop'; " +
        "try { $i = Get-CimInstance -Namespace 'root/Faronics' -ClassName 'AntiExecutable_v2' -ErrorAction Stop | " +
        "Select-Object -First 1 Protection,DriverStatus,StatusFlags; " +
        "if ($null -eq $i) { '{\"present\":false}' } " +
        "else { [pscustomobject]@{present=$true;Protection=$i.Protection;DriverStatus=[string]$i.DriverStatus;StatusFlags=$i.StatusFlags} | ConvertTo-Json -Compress } } " +
        "catch { '{\"error\":true}' }";

    public async Task<AecProviderReading?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var result = await runner.RunAsync(powerShellPath,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", Query], cancellationToken);
        if (result.TimedOut || result.Error is not null || result.ExitCode is not 0) return null;
        return AecProvider.ParseProviderJson(result.Output);
    }
}
