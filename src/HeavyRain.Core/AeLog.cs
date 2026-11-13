using System.Text;
using System.Text.RegularExpressions;

namespace HeavyRain.Core;

public enum AeLogKind { Other, Allowed, Blocked, ProtectionChange }

/// <summary>One event from Anti-Executable's own log.</summary>
/// <param name="Time">The timestamp exactly as Anti-Executable wrote it. It isn't parsed, because the
/// date order depends on how Anti-Executable formats it on that PC.</param>
public sealed record AeLogEntry(string Time, string? User, string Event, string Message, AeLogKind Kind, string? FilePath = null,
    string? Product = null, string? Publisher = null, string? Version = null, string? Hash = null)
{
    public string? FileName => FilePath is null ? null : FilePath[(FilePath.LastIndexOf('\\') + 1)..];
}

/// <summary>
/// Reads Anti-Executable's log (AE.log; its folder and name are published in the
/// <c>AELogLocation</c> and <c>AELogFileName</c> settings).
/// </summary>
/// <remarks>
/// Observed format on 6.11.2112.730, one event per line, fields separated by <c>#</c>:
/// <code>11/12/2026 17:23:40#PC#PC\User#Unknown File Allowed#Unknown File Allowed( C:\path\app.exe )</code>
/// A file event is followed by a detail line for the same path:
/// <code>0#&lt;hash&gt;#C:\path\app.exe#11/12/2026 17:23:40# #.exe#&lt;publisher&gt;#&lt;product&gt;#&lt;original name&gt;#&lt;version&gt;#...</code>
/// Lines in any other shape are skipped, never guessed at.
/// </remarks>
public static class AeLog
{
    public const int DefaultTailBytes = 256 * 1024;

    private static readonly Regex EventTime = new(@"\A\d{1,2}/\d{1,2}/\d{4} \d{1,2}:\d{2}:\d{2}\z", RegexOptions.CultureInvariant);
    private static readonly Regex HashField = new(@"\A[0-9A-Fa-f]{32,128}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Subject = new(@"\A[^(]*\((?<path>.*)\)\s*\z", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex LocalPath = new(@"\A(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)", RegexOptions.CultureInvariant);

    /// <summary>Parses log lines in file order. Entries are returned oldest first.</summary>
    public static IReadOnlyList<AeLogEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<AeLogEntry>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var fields = line.Split('#');
            if (fields.Length >= 5 && EventTime.IsMatch(fields[0].Trim()))
            {
                var name = fields[3].Trim();
                var message = string.Join("#", fields.Skip(4)).Trim();
                if (name.Length == 0) continue;
                entries.Add(new(fields[0].Trim(), Blank(fields[2]), name, message, Classify(name), ExtractPath(message)));
                continue;
            }
            // A detail line only ever completes the file event directly before it.
            if (fields.Length >= 10 && HashField.IsMatch(fields[1].Trim()) && entries.Count > 0)
            {
                var last = entries[^1];
                if (last.FilePath is not null && last.Hash is null && string.Equals(last.FilePath, fields[2].Trim(), StringComparison.OrdinalIgnoreCase))
                    entries[^1] = last with { Hash = fields[1].Trim(), Publisher = Blank(fields[6]), Product = Blank(fields[7]), Version = Blank(fields[9]) };
            }
        }
        return entries;
    }

    /// <summary>The newest entries first, at most <paramref name="count"/>.</summary>
    public static IReadOnlyList<AeLogEntry> Newest(IReadOnlyList<AeLogEntry> entries, int count) =>
        entries.Skip(Math.Max(0, entries.Count - count)).Reverse().ToList();

    /// <summary>
    /// Reads the complete lines in the last <paramref name="maxBytes"/> of the log. The file is
    /// opened read-only and shared, so Anti-Executable can keep writing to it.
    /// </summary>
    public static IReadOnlyList<string> ReadTailLines(string path, int maxBytes = DefaultTailBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var bom = new byte[3];
        var bomLength = stream.Read(bom, 0, 3);
        var utf16 = bomLength >= 2 && bom[0] == 0xFF && bom[1] == 0xFE;
        var utf8Bom = bomLength == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;

        long dataStart = utf16 ? 2 : utf8Bom ? 3 : 0;
        var start = Math.Max(dataStart, length - maxBytes);
        if (utf16 && start % 2 == 1) start++;
        var startsMidFile = start > dataStart;
        if (start >= length) return [];
        stream.Position = start;
        var buffer = new byte[length - start];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var bytes = buffer.AsSpan(0, read);

        // Starting mid-file lands inside a line: drop everything up to the first line break so a
        // partial line (or a split multi-byte character) is never parsed.
        if (startsMidFile)
        {
            var cut = utf16 ? IndexOfUtf16LineBreak(bytes) : bytes.IndexOf((byte)'\n');
            if (cut < 0) return [];
            bytes = bytes[(cut + (utf16 ? 2 : 1))..];
        }
        var text = utf16 ? Encoding.Unicode.GetString(bytes) : Decode(bytes);
        return text.Split('\n');
    }

    private static int IndexOfUtf16LineBreak(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i + 1 < bytes.Length; i += 2)
            if (bytes[i] == (byte)'\n' && bytes[i + 1] == 0) return i;
        return -1;
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static AeLogKind Classify(string name) =>
        name.Contains("block", StringComparison.OrdinalIgnoreCase) ? AeLogKind.Blocked
        : name.Contains("allow", StringComparison.OrdinalIgnoreCase) ? AeLogKind.Allowed
        : name.Contains("protection", StringComparison.OrdinalIgnoreCase) || name.Contains("maintenance", StringComparison.OrdinalIgnoreCase) ? AeLogKind.ProtectionChange
        : AeLogKind.Other;

    private static string? ExtractPath(string message)
    {
        var match = Subject.Match(message);
        if (!match.Success) return null;
        var path = match.Groups["path"].Value.Trim();
        return LocalPath.IsMatch(path) ? path : null;
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
