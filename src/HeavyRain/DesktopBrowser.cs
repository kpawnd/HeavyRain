using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace HeavyRain;

internal static class DesktopBrowser
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private const int DesktopFolder = 0;
    private const int DesktopWindow = 8;
    private const int NeedDispatch = 1;
    private const int ShowNormal = 1;

    public static bool TryOpen(Uri address)
    {
        try
        {
            var shellWindowsType = Type.GetTypeFromCLSID(ShellWindowsClass);
            if (shellWindowsType is null || Activator.CreateInstance(shellWindowsType) is not { } instance) return false;

            // Use the desktop's shell so the default browser runs as the signed-in Windows user.
            dynamic windows = instance;
            object location = DesktopFolder;
            object? reserved = null;
            dynamic? desktop = windows.FindWindowSW(ref location, ref reserved, DesktopWindow, out int _, NeedDispatch);
            if (desktop is null) return false;
            desktop.Document.Application.ShellExecute(address.AbsoluteUri, "", "", "open", ShowNormal);
            return true;
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
