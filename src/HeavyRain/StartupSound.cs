using System.IO;
using System.Runtime.InteropServices;

namespace HeavyRain;

internal sealed class StartupSound : IDisposable
{
    private GCHandle buffer;
    private bool playing;

    public static StartupSound? TryPlay(bool enabled)
    {
        if (!enabled) return null;
        var sound = new StartupSound();
        try
        {
            using var resource = typeof(StartupSound).Assembly.GetManifestResourceStream("HeavyRain.StartupChime.wav");
            if (resource is null) return null;
            using var memory = new MemoryStream();
            resource.CopyTo(memory);
            sound.buffer = GCHandle.Alloc(memory.ToArray(), GCHandleType.Pinned);
            sound.playing = PlaySound(sound.buffer.AddrOfPinnedObject(), IntPtr.Zero, 0x0001 | 0x0002 | 0x0004 | 0x0010);
            if (sound.playing) return sound;
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        sound.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (playing)
        {
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            playing = false;
        }
        if (buffer.IsAllocated) buffer.Free();
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
