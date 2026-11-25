using System.Collections;
using System.Runtime.InteropServices;
using HeavyRain.Core;

namespace HeavyRain;

/// <summary>
/// Reads the Faronics WMI provider through Windows' own scripting interface, read-only. Only the
/// named properties are asked for; the licence key properties are never selected.
/// </summary>
public static class FaronicsProviderReader
{
    private const string Namespace = @"root\Faronics";
    private const string DeepFreezeQuery = "SELECT DFStatus, StatusFlags, ThawedTime, LicenseExpiryDate FROM DeepFreeze";
    private const string AntiExecutableQuery = "SELECT StatusFlags, LicenseExpiryDate FROM AntiExecutable_v2";
    private const int ReturnImmediately = 0x10;   // not forward-only: .NET can't enumerate that kind of set
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(8);
    private static Task<FaronicsProviderReading>? running;

    /// <summary>
    /// Reads both classes, giving up after eight seconds. A read that Windows never answers is left
    /// behind rather than started again on every refresh.
    /// </summary>
    public static async Task<FaronicsProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        if (running is not { IsCompleted: false }) running = Task.Run(Read, CancellationToken.None);
        try { return await running.WaitAsync(Limit, cancellationToken); }
        catch (TimeoutException) { return new(DfProviderReading.Failed(ProviderProblem.TimedOut, DateTimeOffset.Now), null); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        { return new(DfProviderReading.Failed(ProviderProblem.Unavailable, DateTimeOffset.Now, ex.HResult), null); }
    }

    private static FaronicsProviderReading Read()
    {
        var now = DateTimeOffset.Now;
        object? locator = null, services = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null || (locator = Activator.CreateInstance(type)) is null)
                return new(DfProviderReading.Failed(ProviderProblem.Unavailable, now), null);
            services = ((dynamic)locator).ConnectServer(".", Namespace);
            var (values, problem, code) = First(services!, DeepFreezeQuery, ["DFStatus", "StatusFlags", "ThawedTime", "LicenseExpiryDate"]);
            var deepFreeze = values is null ? DfProviderReading.Failed(problem, now, code)
                : DfProviderReading.FromValues(values[0], values[1], values[2], values[3], DateTimeOffset.Now);
            var (ae, _, _) = First(services!, AntiExecutableQuery, ["StatusFlags", "LicenseExpiryDate"]);
            return new(deepFreeze, ae is null ? null : new(ProviderValues.Number(ae[0]), ProviderValues.Date(ae[1])));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        { return new(DfProviderReading.Failed(ProviderValues.Problem(ex.HResult), now, ex.HResult), null); }
        finally
        {
            if (services is not null && Marshal.IsComObject(services)) Marshal.ReleaseComObject(services);
            if (locator is not null && Marshal.IsComObject(locator)) Marshal.ReleaseComObject(locator);
        }
    }

    private static (object?[]? Values, ProviderProblem Problem, int? Code) First(object services, string query, string[] names)
    {
        try
        {
            object set = ((dynamic)services).ExecQuery(query, "WQL", ReturnImmediately);
            // Asking for the count waits for the query and reports its real error code; enumerating hides it.
            if ((int)((dynamic)set).Count == 0) return (null, ProviderProblem.NoInstance, null);
            foreach (object item in (IEnumerable)set)
            {
                dynamic properties = ((dynamic)item).Properties_;
                return (names.Select(name => (object?)properties.Item(name).Value).ToArray(), ProviderProblem.None, null);
            }
            return (null, ProviderProblem.NoInstance, null);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        { return (null, ProviderValues.Problem(ex.HResult), ex.HResult); }
    }
}
