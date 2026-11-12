using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HeavyRain.Core;

public sealed class AecDiagnosticProbe(TimeSpan? timeout = null) : IAecDiagnosticReader
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? lastPath;
    private AecDiagnostic lastDiagnostic;
    private long lastCheck;

    public async Task<AecDiagnostic> DiagnoseAsync(string executable, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !Path.IsPathFullyQualified(executable) || !File.Exists(executable)) return AecDiagnostic.None;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(lastPath, executable, StringComparison.OrdinalIgnoreCase) && Stopwatch.GetElapsedTime(lastCheck) < TimeSpan.FromMinutes(1)) return lastDiagnostic;
            var diagnostic = await Task.Run(() => ReadStatusDiagnostic(executable, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lastPath = executable;
            lastCheck = Stopwatch.GetTimestamp();
            return lastDiagnostic = diagnostic;
        }
        finally { gate.Release(); }
    }

    private AecDiagnostic ReadStatusDiagnostic(string executable, CancellationToken cancellationToken)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100 };
        if (cancellationToken.IsCancellationRequested) return AecDiagnostic.None;
        if (!CreateProcessW(executable, new StringBuilder('"' + executable + "\" status"), IntPtr.Zero, IntPtr.Zero, false,
                0x08000002, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out var process)) return AecDiagnostic.None;
        var eventBuffer = IntPtr.Zero;
        var exited = false;
        var diagnostic = AecDiagnostic.None;
        var clock = Stopwatch.StartNew();
        try
        {
            eventBuffer = Marshal.AllocHGlobal(176);
            while (!cancellationToken.IsCancellationRequested && clock.Elapsed < (timeout ?? TimeSpan.FromSeconds(5)))
            {
                if (!WaitForDebugEventEx(eventBuffer, 50))
                {
                    if (Marshal.GetLastWin32Error() != 121) return AecDiagnostic.None;
                    continue;
                }
                var code = Marshal.ReadInt32(eventBuffer);
                var pid = Marshal.ReadInt32(eventBuffer, 4);
                var tid = Marshal.ReadInt32(eventBuffer, 8);
                uint continuation = 0x10002;
                try
                {
                    if (code == 1 && unchecked((uint)Marshal.ReadInt32(eventBuffer, 16)) != 0x80000003) continuation = 0x80010001;
                    if (code is 3 or 6)
                    {
                        var file = Marshal.ReadIntPtr(eventBuffer, 16);
                        if (file != IntPtr.Zero && file != new IntPtr(-1)) CloseHandle(file);
                    }
                    if (pid == process.ProcessId && code == 8)
                    {
                        var found = ReadMessage(process.Process, eventBuffer);
                        if (found != AecDiagnostic.None)
                        {
                            if (diagnostic != AecDiagnostic.None && diagnostic != found) return AecDiagnostic.None;
                            diagnostic = found;
                        }
                    }
                    if (pid == process.ProcessId && code == 5) exited = true;
                }
                finally { ContinueDebugEvent(pid, tid, continuation); }
                if (exited) return diagnostic;
            }
            return AecDiagnostic.None;
        }
        finally
        {
            if (!exited)
            {
                TerminateProcess(process.Process, 1);
                DebugActiveProcessStop(process.ProcessId);
                WaitForSingleObject(process.Process, 1000);
            }
            if (eventBuffer != IntPtr.Zero) Marshal.FreeHGlobal(eventBuffer);
            CloseHandle(process.Thread);
            CloseHandle(process.Process);
        }
    }

    private static AecDiagnostic ReadMessage(IntPtr process, IntPtr eventBuffer)
    {
        var unicode = Marshal.ReadInt16(eventBuffer, 24) != 0;
        var length = (ushort)Marshal.ReadInt16(eventBuffer, 26);
        if (length == 0 || length > 512) return AecDiagnostic.None;
        var buffer = new byte[length * (unicode ? 2 : 1)];
        try
        {
            if (!ReadProcessMemory(process, Marshal.ReadIntPtr(eventBuffer, 16), buffer, (nuint)buffer.Length, out var read)) return AecDiagnostic.None;
            var count = (int)read;
            if (unicode)
            {
                count -= count % 2;
                for (var index = 0; index < count; index += 2)
                    if (buffer[index] == 0 && buffer[index + 1] == 0) { count = index; break; }
            }
            else
            {
                var terminator = Array.IndexOf(buffer, (byte)0, 0, count);
                if (terminator >= 0) count = terminator;
            }
            var message = (unicode ? Encoding.Unicode : Encoding.ASCII).GetString(buffer, 0, count);
            return AecDiagnostics.Classify(message);
        }
        finally { Array.Clear(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, Width, Height, Columns, Rows, Fill, Flags;
        public short Show, ReservedLength;
        public IntPtr ReservedData, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitForDebugEventEx(IntPtr debugEvent, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ContinueDebugEvent(int processId, int threadId, uint status);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DebugActiveProcessStop(int processId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
