using System.Globalization;

namespace HeavyRain.Core;

public enum ProviderProblem { None, NamespaceMissing, ClassMissing, NoInstance, AccessDenied, TimedOut, Unavailable }

/// <summary>
/// What Deep Freeze's own WMI provider reports (<c>root\Faronics</c>, class <c>DeepFreeze</c>).
/// </summary>
/// <param name="Status">The <c>DFStatus</c> bits.</param>
/// <param name="StatusFlags">The product flags every Faronics provider class shares.</param>
/// <param name="ThawedSeconds"><c>ThawedTime</c>: seconds since the PC was thawed.</param>
/// <remarks>
/// The bit meanings come from the definition files Deep Freeze 9.00.520.5760 installs next to
/// itself (<c>DeepFreezeWorkstation.mof</c>) and the Cloud agent's <c>FaronicsWebProduct_v2.mof</c>.
/// 1 (Frozen) and 2 (Thawed) were also seen live. <c>ThawedTime</c> was watched for a minute and
/// matched the first Thawed boot of the run ten days earlier, across four starts and a power-off,
/// so it doesn't restart at each boot. The other bits haven't been seen set.
/// </remarks>
public sealed record DfProviderReading(long? Status, long? StatusFlags, long? ThawedSeconds, DateOnly? LicenceExpiry, DateTimeOffset ReadAt,
    ProviderProblem Problem = ProviderProblem.None, int? ErrorCode = null)
{
    public const long Frozen = 0x1, Thawed = 0x2, Error = 0x8;
    public const long InMaintenance = 0x01000000, InputOff = 0x02000000, LicenceExpiredThaw = 0x04000000, ThawedLocked = 0x08000000;

    private bool Has(long bit) => Problem == ProviderProblem.None && Status is { } status && (status & bit) != 0;

    /// <summary>Frozen or Thawed when the bits say exactly one of them; otherwise Unknown.</summary>
    public ProtectionState State
    {
        get
        {
            if (Has(Error)) return ProtectionState.Unknown;
            var thawed = Has(Thawed | InMaintenance | LicenceExpiredThaw | ThawedLocked);
            return Has(Frozen) == thawed ? ProtectionState.Unknown : thawed ? ProtectionState.Thawed : ProtectionState.Frozen;
        }
    }

    /// <summary>Why Deep Freeze is in this state, when it says; null for an ordinary Frozen or Thawed PC.</summary>
    public string? Reason =>
        Has(LicenceExpiredThaw) ? "Thawed because the Deep Freeze licence expired"
        : Has(InMaintenance) ? "Thawed for maintenance" + (Has(InputOff) ? ", keyboard and mouse off" : "")
        : Has(ThawedLocked) ? "Thawed and locked"
        : Has(Error) ? "Deep Freeze reports an error"
        : Has(InputOff) ? "Keyboard and mouse off"
        : null;

    /// <summary>True when Deep Freeze says the licence has run out, by either set of bits.</summary>
    public bool LicenceExpired => Has(LicenceExpiredThaw) || ProductFlags.LicenceExpired(Problem == ProviderProblem.None ? StatusFlags : null);

    public DateTimeOffset? ThawedSince => State == ProtectionState.Thawed && ThawedSeconds is > 0 and { } seconds ? ReadAt - TimeSpan.FromSeconds(seconds) : null;

    public static DfProviderReading Failed(ProviderProblem problem, DateTimeOffset readAt, int? errorCode = null) =>
        new(null, null, null, null, readAt, problem, errorCode);

    public static DfProviderReading FromValues(object? status, object? flags, object? thawedTime, object? licenceExpiry, DateTimeOffset readAt) =>
        new(ProviderValues.Number(status), ProviderValues.Number(flags), ProviderValues.Number(thawedTime), ProviderValues.Date(licenceExpiry), readAt);
}

/// <summary>Anti-Executable's shared product flags, from class <c>AntiExecutable_v2</c>.</summary>
public sealed record AeProviderFlags(long? StatusFlags, DateOnly? LicenceExpiry)
{
    public bool LicenceExpired => ProductFlags.LicenceExpired(StatusFlags);
}

/// <summary>
/// The <c>StatusFlags</c> bits every Faronics provider class shares, as documented in the Cloud
/// agent's <c>FaronicsWebProduct_v2.mof</c>. Bits 7 (enabled) and 10 (maintenance) were confirmed on
/// Anti-Executable 6.11.2112.730: 0x400100C4 while protected and 0x40010444 in Maintenance.
/// </summary>
public static class ProductFlags
{
    public static bool LicenceExpired(long? flags) => Bit(flags, 4);
    public static bool Installed(long? flags) => Bit(flags, 6);
    public static bool Enabled(long? flags) => Bit(flags, 7);
    public static bool Maintenance(long? flags) => Bit(flags, 10);
    public static bool RestartNeeded(long? flags) => Bit(flags, 11);

    private static bool Bit(long? flags, int bit) => flags is { } value && (value & (1L << bit)) != 0;
}

/// <summary>Converts values as the WMI scripting interface returns them.</summary>
public static class ProviderValues
{
    /// <summary>64-bit integers arrive as strings and unsigned 32-bit ones as signed; both are handled.</summary>
    public static long? Number(object? value) => value switch
    {
        null => null,
        long number => number,
        int number => number,
        uint number => number,
        short number => number,
        ushort number => number,
        byte number => number,
        string text => long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null,
        _ => null
    };

    /// <summary>The date part of a DMTF timestamp, "20270110173001.000000+000".</summary>
    public static DateOnly? Date(object? value) =>
        value is string { Length: >= 8 } text && DateOnly.TryParseExact(text[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year > 1970
            ? date : null;

    /// <summary>What a WMI error code means for reading a class.</summary>
    public static ProviderProblem Problem(int errorCode) => unchecked((uint)errorCode) switch
    {
        0x8004100E => ProviderProblem.NamespaceMissing,
        0x80041010 => ProviderProblem.ClassMissing,
        0x80041003 or 0x80070005 => ProviderProblem.AccessDenied,
        _ => ProviderProblem.Unavailable
    };
}

/// <summary>Both readings from one pass over the provider.</summary>
public sealed record FaronicsProviderReading(DfProviderReading DeepFreeze, AeProviderFlags? AntiExecutable);
