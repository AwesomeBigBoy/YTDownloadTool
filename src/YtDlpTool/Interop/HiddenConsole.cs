using System.Runtime.InteropServices;

namespace YtDlpTool.Interop;

/// <summary>
/// Allocates a console for the WPF parent process and immediately hides its
/// window. Spawned child processes (yt-dlp, ffmpeg) that we launch without
/// <c>CreateNoWindow</c> inherit this console, so they see real TTY stdio
/// handles (their <c>isatty</c> probe returns true, <c>GetConsoleWindow</c>
/// returns a non-zero HWND) without a visible window flashing up for the user.
///
/// Why this matters: v1.1.23-v1.1.25 fixed the managed-network endpoint security software block by
/// switching yt-dlp to TTY mode (no pipe redirection) — but the cost was a
/// 1-2 s visible console window per spawn. Allocating the console in the
/// parent and hiding it gives yt-dlp the same TTY environment without the
/// flash, satisfying both the AV heuristic and UX.
///
/// Edge cases:
/// - If the parent process was launched from a console (e.g., <c>cmd /c
///   YtDlpTool.exe</c>), <c>AllocConsole</c> returns false because a console
///   is already attached. We accept that — the existing console becomes the
///   one children inherit; it's already visible to the launching user, no
///   regression.
/// - The hidden console accepts our own <c>Console.WriteLine</c> writes too.
///   We do not rely on them; if any slip through they are silently consumed.
/// </summary>
internal static class HiddenConsole
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;

    private static bool _allocated;

    /// <summary>
    /// Allocates a hidden console for this process. Safe to call multiple
    /// times; only the first effective call does anything. No-op if the
    /// process already has a console (launched from a terminal).
    /// </summary>
    public static void Allocate()
    {
        if (_allocated) return;
        if (!AllocConsole()) return; // already attached (cmd-launched) — leave it alone
        _allocated = true;
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SW_HIDE);
        }
    }

    public static void Free()
    {
        if (!_allocated) return;
        FreeConsole();
        _allocated = false;
    }
}
