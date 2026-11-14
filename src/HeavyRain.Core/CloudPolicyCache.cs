using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace HeavyRain.Core;

public sealed record PolicyCacheResult(string? Name, string Detail);
public sealed record PolicyCacheCheck(string RegistrySource, string? DatabasePath, PolicyCacheResult Result);

public static class CloudPolicyCache
{
    public const int MaximumXmlLength = 1048576;
    private const string Query = "SELECT Value FROM CoreInfo WHERE Key='CURRENT_PRODUCTS_POLICY' LIMIT 2";

    public static string? ParseName(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaximumXmlLength) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlLength, IgnoreComments = true
            });
            if (reader.MoveToContent() != XmlNodeType.Element || reader.Name != "Policy" || reader.NamespaceURI.Length != 0) return null;
            var name = PolicyRegistryRules.ReadName(reader.GetAttribute("Name"));
            while (reader.Read()) { }
            return name;
        }
        catch (XmlException) { return null; }
    }

    public static PolicyCacheResult Read(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return new(null, "The Cloud cache reader requires Windows.");
        if (!File.Exists(path)) return new(null, "Cloud policy database not found.");
        IntPtr database = IntPtr.Zero, statement = IntPtr.Zero;
        var budget = Stopwatch.StartNew();
        ProgressCallback progress = _ => cancellationToken.IsCancellationRequested || budget.Elapsed > TimeSpan.FromSeconds(2) ? 1 : 0;
        try
        {
            var code = sqlite3_open_v2(Utf8(path), out database, 1, IntPtr.Zero);
            if (code != 0) return Failure(code);
            sqlite3_busy_timeout(database, 150);
            sqlite3_limit(database, 0, MaximumXmlLength + 4096);
            sqlite3_progress_handler(database, 1000, progress, IntPtr.Zero);
            code = sqlite3_prepare_v2(database, Utf8(Query), -1, out statement, IntPtr.Zero);
            if (code != 0) return Failure(code);
            code = sqlite3_step(statement);
            if (code == 101) return new(null, "The cache has no current product policy.");
            if (code != 100) return Failure(code);
            if (sqlite3_column_type(statement, 0) != 3) return new(null, "The cached policy is not XML text.");
            var pointer = sqlite3_column_text(statement, 0);
            var length = sqlite3_column_bytes(statement, 0);
            if (pointer == IntPtr.Zero || length is <= 0 or > MaximumXmlLength) return new(null, "The cached policy is empty or exceeds the read limit.");
            var xml = Marshal.PtrToStringUTF8(pointer, length)!;
            code = sqlite3_step(statement);
            if (code == 100) return new(null, "The cache contains multiple current policies; no name was selected.");
            if (code != 101) return Failure(code);
            cancellationToken.ThrowIfCancellationRequested();
            var name = ParseName(xml);
            return name is null
                ? new(null, "The cached policy has no valid root Policy Name attribute.")
                : new(name, "Read from the Cloud agent's cached policy; the Cloud console may have newer settings.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return new(null, "The Windows SQLite reader is unavailable."); }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (database != IntPtr.Zero) sqlite3_close(database);
            GC.KeepAlive(progress);
        }

        PolicyCacheResult Failure(int code)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(null, code == 9 ? "The cache query exceeded its time limit." : $"Could not read the Cloud policy cache (SQLite {code}).");
        }
    }

    /// <summary>
    /// The non-secret settings Heavy Rain shows from the cached policy (schedules, check-in interval,
    /// Anti-Executable user lists), or null when the cache can't be read or holds no single policy.
    /// The policy XML itself is parsed and dropped; it is never returned or stored.
    /// </summary>
    public static CloudPolicyDetails? ReadDetails(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = WinSqlite.Query(path, "SELECT Value FROM CoreInfo WHERE Key='CURRENT_PRODUCTS_POLICY' LIMIT 2", 2, MaximumXmlLength, cancellationToken);
            return rows.Count == 1 && rows[0]["Value"] is { } xml ? CloudPolicyParser.Parse(xml) : null;
        }
        catch (WinSqlite.SqliteReadException) { return null; }
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProgressCallback(IntPtr context);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_open_v2(byte[] path, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_close(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_limit(IntPtr database, int id, int value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void sqlite3_progress_handler(IntPtr database, int instructions, ProgressCallback callback, IntPtr context);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_column_type(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);
}
