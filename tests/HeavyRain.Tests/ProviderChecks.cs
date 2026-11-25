using HeavyRain.Core;

/// <summary>
/// Deep Freeze's read-only provider. Values marked "VM" were
/// read on the Windows 11 test VM (Deep Freeze 9.00.520.5760, Anti-Executable 6.11.2112.730); the
/// other status bits come from the definition files installed there and haven't been seen set.
/// </summary>
internal static class ProviderChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 16, 9, 2, 145, TimeSpan.FromHours(-8));
    private static DfProviderReading Df(long? status, long? flags = 0x4B, long? thawed = null) => new(status, flags, thawed, null, Now);

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        foreach (var (status, state, reason) in new (long?, ProtectionState, string?)[]
        {
            (0x1, ProtectionState.Frozen, null), (0x2, ProtectionState.Thawed, null),                                  // VM
            (0x04000002, ProtectionState.Thawed, "Thawed because the Deep Freeze licence expired"),
            (0x04000000, ProtectionState.Thawed, "Thawed because the Deep Freeze licence expired"),
            (0x01000002, ProtectionState.Thawed, "Thawed for maintenance"),
            (0x03000002, ProtectionState.Thawed, "Thawed for maintenance, keyboard and mouse off"),
            (0x08000000, ProtectionState.Thawed, "Thawed and locked"),
            (0x02000001, ProtectionState.Frozen, "Keyboard and mouse off"),
            (0x8, ProtectionState.Unknown, "Deep Freeze reports an error"), (0xA, ProtectionState.Unknown, "Deep Freeze reports an error"),
            (0x0, ProtectionState.Unknown, null), (0x3, ProtectionState.Unknown, null), (0x10, ProtectionState.Unknown, null), (null, ProtectionState.Unknown, null)
        })
        await check($"Provider: DFStatus {(status is null ? "missing" : $"0x{status:X8}")} is {state}", () =>
        {
            var reading = Df(status);
            Assert(reading.State == state && reading.Reason == reason, $"Got {reading.State}, {reading.Reason}");
            return Task.CompletedTask;
        });

        await check("Provider: a failed read is Unknown and explains nothing, whatever it carried", () =>
        {
            var failed = Df(0x04000001, 0x10, 500) with { Problem = ProviderProblem.Unavailable };
            Assert(failed is { State: ProtectionState.Unknown, Reason: null, LicenceExpired: false, ThawedSince: null }, "Failed reading trusted: " + failed);
            Assert(DfProviderReading.Failed(ProviderProblem.ClassMissing, Now, 5) is { State: ProtectionState.Unknown, Problem: ProviderProblem.ClassMissing, ErrorCode: 5 }, "Failure details lost");
            return Task.CompletedTask;
        });

        await check("Provider: an expired licence shows from either set of bits", () =>
        {
            Assert(Df(0x04000000).LicenceExpired && Df(0x2, 0x5B).LicenceExpired && !Df(0x2).LicenceExpired && !Df(0x1, null).LicenceExpired, "Licence bits misread");
            Assert(new AeProviderFlags(0x400100D4, null).LicenceExpired && !new AeProviderFlags(0x400100C4, null).LicenceExpired && !new AeProviderFlags(null, null).LicenceExpired, "Anti-Executable licence bit misread");
            return Task.CompletedTask;
        });

        await check("Provider: ThawedTime gives the VM's thaw ten days earlier, and nothing while Frozen", () =>
        {
            var since = Df(0x2, thawed: 913427).ThawedSince!.Value;   // VM
            Assert(Math.Abs((since - new DateTimeOffset(2026, 11, 14, 2, 25, 15, TimeSpan.FromHours(-8))).TotalSeconds) < 1, "Wrong start: " + since);
            Assert(Df(0x1, thawed: 913427).ThawedSince is null && Df(0x2, thawed: 0).ThawedSince is null && Df(0x2).ThawedSince is null && Df(0x2, thawed: -5).ThawedSince is null, "Start invented");
            return Task.CompletedTask;
        });

        await check("Provider: product flags match the VM's products", () =>
        {
            Assert(ProductFlags.Installed(0x4B) && !ProductFlags.LicenceExpired(0x4B) && !ProductFlags.Enabled(0x4B), "Deep Freeze flags");
            Assert(ProductFlags.Enabled(0x400100C4) && !ProductFlags.Maintenance(0x400100C4), "Protected flags");
            Assert(ProductFlags.Maintenance(0x40010444) && !ProductFlags.Enabled(0x40010444), "Maintenance flags");
            Assert(ProductFlags.RestartNeeded(0x800) && !ProductFlags.RestartNeeded(0x400) && !ProductFlags.Installed(null), "Other flags");
            return Task.CompletedTask;
        });

        await check("Provider: values arrive as the scripting interface sends them", () =>
        {
            object?[] twos = ["2", 2, 2u, 2L, (short)2, (ushort)2, (byte)2, " 2 "];
            Assert(twos.All(value => ProviderValues.Number(value) == 2), "A number form was lost");
            Assert(ProviderValues.Number("x") is null && ProviderValues.Number(null) is null && ProviderValues.Number(new object()) is null && ProviderValues.Number("67108866") == 0x04000002, "Odd values");
            Assert(ProviderValues.Date("20270110173001.000000+000") == new DateOnly(2027, 1, 10), "Expiry date");
            Assert(ProviderValues.Date("19700101000000.000000+000") is null && ProviderValues.Date("2027") is null && ProviderValues.Date(null) is null && ProviderValues.Date("20271399000000") is null, "Bad date accepted");
            var reading = DfProviderReading.FromValues("2", 75, 913427, "20270110173001.000000+000", Now);
            Assert(reading is { State: ProtectionState.Thawed, StatusFlags: 75, ThawedSeconds: 913427, Problem: ProviderProblem.None } && reading.LicenceExpiry == new DateOnly(2027, 1, 10), "Reading: " + reading);
            return Task.CompletedTask;
        });

        foreach (var (code, problem) in new[]
        {
            (0x8004100Eu, ProviderProblem.NamespaceMissing), (0x80041010u, ProviderProblem.ClassMissing), (0x80041003u, ProviderProblem.AccessDenied),
            (0x80070005u, ProviderProblem.AccessDenied), (0x80041013u, ProviderProblem.Unavailable), (0x800706BAu, ProviderProblem.Unavailable)
        })
        await check($"Provider: WMI error 0x{code:X8} is {problem}", () =>
        {
            Assert(ProviderValues.Problem(unchecked((int)code)) == problem, "Misread");
            return Task.CompletedTask;
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
