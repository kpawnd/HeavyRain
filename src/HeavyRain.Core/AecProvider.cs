using System.Text.Json;

namespace HeavyRain.Core;

/// <summary>
/// A reading from the Faronics Anti-Executable WMI provider
/// (<c>root\Faronics</c>, class <c>AntiExecutable_v2</c>). This is the live state the Faronics
/// service reports, independent of the AEC.exe command line. On Cloud-managed installs AEC.exe
/// can print "Unprotected" while protection is actually enforced, so this reading is used to
/// correct that false negative.
/// </summary>
public sealed record AecProviderReading(int? Protection, string? DriverStatus = null, long? StatusFlags = null, bool Present = true);

/// <summary>Reads the live Anti-Executable state from its WMI provider.</summary>
public interface IAecProviderReader
{
    Task<AecProviderReading?> ReadAsync(CancellationToken cancellationToken = default);
}

public static class AecProvider
{
    // The provider reports the Anti-Executable "Protection" feature as a numeric code in the
    // 1100-1105 block. Only 1105 is confirmed: on Cloud Anti-Executable 6.11.2112.730 the provider
    // reported Protection=1105 while the Faronics console showed the machine Protected and AEC.exe
    // status still printed "Unprotected". The other codes in that block are not yet confirmed, so
    // they are deliberately left unmapped rather than guessed. A confirmed mapping can therefore
    // only ever rescue a false "Unprotected"; it never invents a protected-off state.
    public static AecState? MapProtection(int? code) => code switch
    {
        1105 => AecState.Enabled,
        _ => null
    };

    /// <summary>
    /// Reconciles the AEC.exe command-line verdict with the live provider reading. When the
    /// provider maps to a definite state it is authoritative, because the command line is known to
    /// misreport on Cloud installs; otherwise the command-line result stands unchanged.
    /// </summary>
    public static AecStatusResult Reconcile(AecStatusResult commandLine, AecProviderReading? provider)
    {
        if (provider is null || !provider.Present) return commandLine;
        if (MapProtection(provider.Protection) is not { } state) return commandLine;
        if (commandLine.State == state) return commandLine;

        var observed = commandLine.State switch
        {
            AecState.Enabled => "Protected",
            AecState.Disabled => "Unprotected",
            AecState.Maintenance => "Maintenance",
            _ => "no readable state"
        };
        var reading = state switch
        {
            AecState.Enabled => "Anti-Executable is enforcing its execution rules. The Faronics provider reports protection on",
            AecState.Disabled => "Anti-Executable execution protection is off. The Faronics provider reports protection off",
            _ => "Maintenance is active. The Faronics provider reports maintenance"
        };
        var detail = commandLine.State == AecState.Unknown
            ? $"{reading}; the AEC.exe command line returned {observed}."
            : $"{reading}, overriding the AEC.exe command line, which reported {observed}.";
        return new(state, detail, commandLine.ExitCode);
    }

    /// <summary>
    /// Parses the compact JSON the provider query emits. Returns <c>null</c> when the provider
    /// could not be read (so the command-line verdict stands), a reading with
    /// <see cref="AecProviderReading.Present"/> set to <c>false</c> when the class exists but has
    /// no instance, or a populated reading otherwise.
    /// </summary>
    public static AecProviderReading? ParseProviderJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                root = root.GetArrayLength() > 0 ? root[0] : default;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("present", out var present) && present.ValueKind == JsonValueKind.False)
                return new(null, null, Present: false);
            return new(ReadInt(root, "Protection"), ReadString(root, "DriverStatus"), ReadLong(root, "StatusFlags"));
        }
        catch (JsonException) { return null; }
    }

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
