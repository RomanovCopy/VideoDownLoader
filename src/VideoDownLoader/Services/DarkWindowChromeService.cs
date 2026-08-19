using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VideoDownLoader.Services;

public static class DarkWindowChromeService
{
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    public static void Enable(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));

        var darkSurface = ToColorRef(0x11, 0x18, 0x27);
        var lightText = ToColorRef(0xF9, 0xFA, 0xFB);
        DwmSetWindowAttribute(handle, BorderColor, ref darkSurface, sizeof(int));
        DwmSetWindowAttribute(handle, CaptionColor, ref darkSurface, sizeof(int));
        DwmSetWindowAttribute(handle, TextColor, ref lightText, sizeof(int));
    }

    private static int ToColorRef(byte red, byte green, byte blue) =>
        red | (green << 8) | (blue << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
