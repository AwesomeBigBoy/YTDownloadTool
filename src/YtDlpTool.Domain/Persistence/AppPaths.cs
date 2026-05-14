namespace YtDlpTool.Domain.Persistence;

public sealed class AppPaths
{
    public string AppDirectory { get; }
    public string DataRoot { get; }
    public string ConfigFile => Path.Combine(DataRoot, "config.json");
    public string LogsDirectory => Path.Combine(DataRoot, "logs");
    public string StateLog => Path.Combine(DataRoot, "state.log");
    public string UpdateStaging => Path.Combine(DataRoot, ".update");
    public string BinDirectory => Path.Combine(AppDirectory, "bin");

    private AppPaths(string appDir, string dataRoot)
    {
        AppDirectory = appDir;
        DataRoot = dataRoot;
    }

    public static AppPaths ResolveForAppDirectory(
        string appDir,
        Func<string, bool>? isWritable = null)
    {
        isWritable ??= TestWritable;
        var dataRoot = isWritable(appDir)
            ? appDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YtDlpTool");
        return new AppPaths(appDir, dataRoot);
    }

    public static AppPaths ResolveForCurrentProcess() =>
        ResolveForAppDirectory(AppContext.BaseDirectory);

    private static bool TestWritable(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid()}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public void EnsureDataDirectoriesExist()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(UpdateStaging);
    }
}
