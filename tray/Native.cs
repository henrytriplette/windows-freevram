using System.Runtime.InteropServices;

namespace FreeVram;

internal static class Native
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);

    const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_LWIN = 0x5B, VK_B = 0x42;
    const uint KEYEVENTF_KEYUP = 0x2;

    /// <summary>
    /// Synthesises Win+Ctrl+Shift+B. Windows has no public API for the graphics driver reset;
    /// the hotkey is the only supported trigger.
    /// </summary>
    public static void RestartGraphicsDriver()
    {
        byte[] down = { VK_LWIN, VK_CONTROL, VK_SHIFT, VK_B };
        foreach (var k in down) { keybd_event(k, 0, 0, 0); Thread.Sleep(30); }
        foreach (var k in down.Reverse()) { keybd_event(k, 0, KEYEVENTF_KEYUP, 0); Thread.Sleep(30); }
    }
}
