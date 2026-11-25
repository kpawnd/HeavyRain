using System.IO;
using System.Runtime.InteropServices;

namespace HeavyRain;

internal sealed class EmbeddedSound : IDisposable
{
    private GCHandle buffer;
    private bool playing;

    public static EmbeddedSound? TryPlay(string resourceName, bool enabled)
    {
        if (!enabled) return null;
        var sound = new EmbeddedSound();
        if (sound.Play(resourceName, asynchronous: true)) return sound;
        sound.Dispose();
        return null;
    }

    public static Task PlayToEndAsync(string resourceName) => Task.Run(() =>
    {
        using var sound = new EmbeddedSound();
        sound.Play(resourceName, asynchronous: false);
    });

    private bool Play(string resourceName, bool asynchronous)
    {
        try
        {
            using var resource = typeof(EmbeddedSound).Assembly.GetManifestResourceStream(resourceName);
            if (resource is null) return false;
            using var memory = new MemoryStream();
            resource.CopyTo(memory);
            buffer = GCHandle.Alloc(memory.ToArray(), GCHandleType.Pinned);
            const uint memoryFlags = 0x0002 | 0x0004 | 0x0010;
            var played = PlaySound(buffer.AddrOfPinnedObject(), IntPtr.Zero, asynchronous ? memoryFlags | 0x0001 : memoryFlags);
            playing = asynchronous && played;
            return played;
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        return false;
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
