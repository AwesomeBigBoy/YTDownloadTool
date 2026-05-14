namespace YtDlpTool.Process.Tests.Helpers;

public static class FakeYtDlpLocator
{
    public static string Path()
    {
        // Walks up from test bin to repo root, then to the FakeYtDlp output.
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir, "tests", "FakeYtDlp", "bin", "Debug", "net8.0", "fake-yt-dlp.exe");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new FileNotFoundException("FakeYtDlp executable not found — build tests/FakeYtDlp first.");
    }
}
