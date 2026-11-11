using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HeavyRain.Core;

public sealed class DfcProcessRunner(TimeSpan? timeout = null) : IDfcRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            return new(null, Error: "DFC.exe was not found at the selected absolute path.");
        if (cancellationToken.IsCancellationRequested) return new(null, Error: "Operation was cancelled.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
            var stdout = DrainAsync(process.StandardOutput, deadline.Token);
            var stderr = DrainAsync(process.StandardError, deadline.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
                return new(process.ExitCode, Output: await stdout);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                return new(null, TimedOut: !cancellationToken.IsCancellationRequested,
                    Error: cancellationToken.IsCancellationRequested ? "Operation was cancelled; verify the actual state before retrying." : null);
            }
        }
        catch (Win32Exception ex)
        {
            return new(null, Error: ex.NativeErrorCode switch
            {
                5 or 740 => "Windows denied access. Reopen this utility as administrator.",
                2 or 3 => "DFC.exe could not be found.",
                193 => "The selected file is not a compatible Windows executable.",
                _ => $"Windows could not run DFC (error {ex.NativeErrorCode})."
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(null, Error: "DFC could not be executed or its result could not be read.");
        }
        finally
        {
            process.StartInfo.ArgumentList.Clear();
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (output.Length < 4096) output.Append(buffer, 0, Math.Min(count, 4096 - output.Length));
        }
        return output.ToString();
    }
}
