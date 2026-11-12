using System.Runtime.InteropServices;

namespace YtDlpTool.Interop;

/// <summary>
/// v1.5.2: what a second launch from the same folder does instead of failing on state.log
/// (YtDlpTool.Process.SingleInstance): bring the open copy's window to the front, restored if
/// it was minimized. Best effort — false when there is no such window yet (that copy is still
/// starting and shows its window by itself), or when Windows only flashes its taskbar button
/// because this launch may not take the foreground.
/// </summary>
internal static class OtherInstance
{
    private const int SwRestore = 9;

    public static bool BringToFront()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var other in System.Diagnostics.Process.GetProcessesByName(self.ProcessName))
        {
            using (other)
            {
                if (other.Id == self.Id) continue;
                try
                {
                    if (!string.Equals(other.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    var window = other.MainWindowHandle;
                    if (window == IntPtr.Zero) continue;
                    if (IsIconic(window)) ShowWindow(window, SwRestore);
                    return SetForegroundWindow(window);
                }
                catch (Exception)
                {
                    // Exited meanwhile, or not ours to inspect.
                }
            }
        }
        return false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
