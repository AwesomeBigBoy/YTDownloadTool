using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace YtDlpTool.Interop;

public static class WindowChromeHelper
{
    public static void ApplyAuroraBackdrop(Window window)
    {
        window.Background = Brushes.Transparent;
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero) helper.EnsureHandle();
        var hwnd = helper.Handle;

        if (WindowsVersion.Current.SupportsMica)
            TryApplyMica(hwnd);
        else if (WindowsVersion.Current.SupportsAcrylic)
            TryApplyAcrylic(hwnd);
        // else: stays transparent → MainWindow shows the Aurora gradient layer directly.
    }

    private static void TryApplyMica(IntPtr hwnd)
    {
        // DWMWA_SYSTEMBACKDROP_TYPE = 38; value 2 = Mica (main window)
        int backdropType = 2;
        DwmSetWindowAttribute(hwnd, 38, ref backdropType, sizeof(int));
        // Optional: extend frame into client area for full coverage
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private static void TryApplyAcrylic(IntPtr hwnd)
    {
        var accent = new ACCENT_POLICY
        {
            AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
            GradientColor = 0x99_F0_F0_F0,
        };
        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, fDeleteOld: false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                SizeOfData = size,
                Data = ptr
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }
    [StructLayout(LayoutKind.Sequential)] private struct ACCENT_POLICY { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWCOMPOSITIONATTRIBDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }
}
