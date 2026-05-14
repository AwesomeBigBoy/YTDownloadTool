namespace YtDlpTool.Process.Tests.Helpers;

public static class FakeYtDlpLocator
{
    public static string Path()
    {
        // Walks up from test bin to repo root, then to the FakeYtDlp output.
        // Tries the test runner's own configuration (Debug or Release) first,
        // falling back to the other one.
        var preferred = AppContext.BaseDirectory.Contains(
            $"{System.IO.Path.DirectorySeparatorChar}Release{System.IO.Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var fallback = preferred == "Release" ? "Debug" : "Release";

        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            foreach (var config in new[] { preferred, fallback })
            {
                var candidate = System.IO.Path.Combine(
                    dir, "tests", "FakeYtDlp", "bin", config, "net8.0", "fake-yt-dlp.exe");
                if (File.Exists(candidate)) return candidate;
            }
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new FileNotFoundException(
            "FakeYtDlp executable not found — ensure tests/FakeYtDlp is built in either Debug or Release.");
    }
}
