using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HeavyRain.Core;
using MahApps.Metro.Controls;

namespace HeavyRain;

public partial class ActionNoticeWindow : MetroWindow
{
    private static readonly int[] ShakeOffsets = [0, -16, 16, -14, 14, -10, 10, -6, 6, -2, 0];
    private readonly DispatcherTimer shakeTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch shakeClock = new();
    private EmbeddedSound? errorSound;
    private IntPtr windowHandle;
    private NativeRectangle origin;
    private double pixelScale;
    private bool feedbackStarted;

    public ActionNoticeWindow(ActionBlocker blocker)
    {
        InitializeComponent();
        NoticeTitle.Text = blocker.Title;
        NoticeMessage.Text = blocker.Message;
        shakeTimer.Tick += Shake_Tick;
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (feedbackStarted) return;
        feedbackStarted = true;
        DismissButton.Focus();
        errorSound = EmbeddedSound.TryPlay("HeavyRain.ErrorChime.wav", !Environment.GetCommandLineArgs().Contains("--silent", StringComparer.OrdinalIgnoreCase));
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out origin)) return;
        windowHandle = handle;
        pixelScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        shakeClock.Restart();
        shakeTimer.Start();
    }

    private void Shake_Tick(object? sender, EventArgs e)
    {
        var elapsed = shakeClock.Elapsed.TotalMilliseconds - 100;
        if (elapsed < 0) return;
        var frame = elapsed / 50;
        var index = (int)frame;
        if (index >= ShakeOffsets.Length - 1) { StopShake(); return; }
        var offset = ShakeOffsets[index] + (ShakeOffsets[index + 1] - ShakeOffsets[index]) * (frame - index);
        if (!SetWindowPos(windowHandle, IntPtr.Zero, origin.Left + (int)Math.Round(offset * pixelScale), origin.Top, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200))
            StopShake();
    }

    private void StopShake(bool restorePosition = true)
    {
        shakeTimer.Stop();
        shakeClock.Stop();
        if (windowHandle == IntPtr.Zero) return;
        if (restorePosition) SetWindowPos(windowHandle, IntPtr.Zero, origin.Left, origin.Top, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200);
        windowHandle = IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        StopShake();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopShake(false);
        shakeTimer.Tick -= Shake_Tick;
        errorSound?.Dispose();
        errorSound = null;
        base.OnClosed(e);
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);
    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
