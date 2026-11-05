using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YtDlpTool.Interop;

/// <summary>
/// Gives a window the system's dark title bar, and on Windows 11 colours the caption to
/// match the app background so the bar and the content read as one surface (v1.5.0).
///
/// Replaces the pre-v1.5.0 custom chrome (WindowStyle=None + hand-drawn min/max/close),
/// which lost snap layouts and window shadows, and the Mica/Acrylic backdrop helper.
/// Call from SourceInitialized, i.e. before the window is first shown — Windows 10 does
/// not repaint an already-visible caption when the attribute changes. Best-effort: an
/// older build simply keeps its light title bar.
/// </summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            // .NET 5+ reports the real OS build (no compatibility-manifest lie).
            var build = Environment.OSVersion.Version.Build;

            var on = 1;
            DwmSetWindowAttribute(hwnd,
                build >= 18985 ? DwmwaUseImmersiveDarkMode : DwmwaUseImmersiveDarkModeBefore20H1,
                ref on, sizeof(int));

            if (build >= 22000)
            {
                // COLORREF is 0x00BBGGRR. Values mirror Color.Bg / Color.Border / Color.Text.
                var caption = ColorRef(0x0F, 0x11, 0x15);
                var border = ColorRef(0x2A, 0x2F, 0x3A);
                var text = ColorRef(0xEC, 0xEE, 0xF2);
                DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
            }
        }
        catch
        {
            // Cosmetic only — never let it stop a window from opening.
        }
    }

    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
