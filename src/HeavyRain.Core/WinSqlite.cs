using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HeavyRain.Core;

/// <summary>
/// Read-only queries against a SQLite file through Windows' own winsqlite3.dll. The file is opened
/// read-only, waits at most 150 ms for a writer's lock, and a query is stopped after two seconds or
/// on cancellation. Only text and integer columns are returned; anything else comes back as null.
/// </summary>
public static class WinSqlite
{
    public sealed class SqliteReadException(string message) : Exception(message);

    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> Query(string path, string sql, int maxRows, int maxTextLength,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new SqliteReadException("The SQLite reader requires Windows.");
        if (!File.Exists(path)) throw new SqliteReadException("The database was not found.");
        IntPtr database = IntPtr.Zero, statement = IntPtr.Zero;
        var budget = Stopwatch.StartNew();
        ProgressCallback progress = _ => cancellationToken.IsCancellationRequested || budget.Elapsed > TimeSpan.FromSeconds(2) ? 1 : 0;
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        try
        {
            Check(sqlite3_open_v2(Utf8(path), out database, 1, IntPtr.Zero));
            sqlite3_busy_timeout(database, 150);
            sqlite3_limit(database, 0, maxTextLength + 4096);
            sqlite3_progress_handler(database, 1000, progress, IntPtr.Zero);
            Check(sqlite3_prepare_v2(database, Utf8(sql), -1, out statement, IntPtr.Zero));
            var columns = sqlite3_column_count(statement);
            var names = Enumerable.Range(0, columns).Select(i => Marshal.PtrToStringUTF8(sqlite3_column_name(statement, i)) ?? $"column{i}").ToArray();
            while (rows.Count < maxRows)
            {
                var code = sqlite3_step(statement);
                if (code == 101) break;
                Check(code == 100 ? 0 : code);
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns; i++)
                {
                    var type = sqlite3_column_type(statement, i);
                    if (type is not (1 or 3)) { row[names[i]] = null; continue; }
                    var pointer = sqlite3_column_text(statement, i);
                    var length = sqlite3_column_bytes(statement, i);
                    row[names[i]] = pointer == IntPtr.Zero || length < 0 ? null
                        : length > maxTextLength ? null : Marshal.PtrToStringUTF8(pointer, length);
                }
                rows.Add(row);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return rows;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { throw new SqliteReadException("The Windows SQLite reader is unavailable."); }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (database != IntPtr.Zero) sqlite3_close(database);
            GC.KeepAlive(progress);
        }

        void Check(int code)
        {
            if (code == 0) return;
            cancellationToken.ThrowIfCancellationRequested();
            throw new SqliteReadException(code == 9 ? "The query exceeded its time limit." : $"SQLite error {code}.");
        }
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
    private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_column_type(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);
}
