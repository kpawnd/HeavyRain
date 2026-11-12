using System.Runtime.InteropServices;
using System.Text;

sealed class PolicyCacheFixture : IDisposable
{
    private IntPtr database;
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeavyRain-policy-" + Guid.NewGuid().ToString("N") + ".dat");

    public PolicyCacheFixture(params (string Key, string? Value)[] rows)
    {
        if (sqlite3_open_v2(Utf8(Path), out database, 6, IntPtr.Zero) != 0) throw new Exception("Could not create cache fixture.");
        Execute("CREATE TABLE CoreInfo (TimeStamp TEXT, Key TEXT, Value TEXT)");
        foreach (var (key, value) in rows)
            Execute($"INSERT INTO CoreInfo(Key,Value) VALUES('{Escape(key)}', {(value is null ? "NULL" : "'" + Escape(value) + "'")})");
    }

    public void Execute(string sql)
    {
        if (sqlite3_exec(database, Utf8(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) throw new Exception("Fixture SQL failed.");
    }

    public void Dispose()
    {
        if (database != IntPtr.Zero) { sqlite3_close(database); database = IntPtr.Zero; }
        File.Delete(Path);
    }

    private static string Escape(string value) => value.Replace("'", "''");
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_open_v2(byte[] path, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_exec(IntPtr database, byte[] sql, IntPtr callback, IntPtr context, IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_close(IntPtr database);
}
