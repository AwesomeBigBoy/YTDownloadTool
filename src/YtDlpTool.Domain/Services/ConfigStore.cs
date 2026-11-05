using System.Text.Json;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;

namespace YtDlpTool.Domain.Services;

/// <summary>
/// config.json. Saved from the UI thread (settings, and the remembered 格式/畫質 at every
/// 下載) and from the background update check, so saves are serialised.
///
/// v1.5.0: written in place, never renamed over. On PCs with endpoint security a rename over
/// an existing file is held for ~3 s — File.Replace, two renames, ~6 s — while writing into
/// the file takes ~2 ms (measured 2026-11-04 in %TEMP% and next to the exe). With
/// File.Replace every 下載 froze the window for 6 s. A copy is written first, so a crash in
/// the middle of either write still leaves one complete file for Load.
/// </summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public ConfigStore(string path) => _path = path;

    private string BackupPath => _path + ".bak";

    public AppConfig Load()
    {
        lock (_gate)
        {
            return TryRead(_path) ?? TryRead(BackupPath) ?? AppConfig.CreateDefault();
        }
    }

    public void Save(AppConfig config)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(config, AppJsonContext.Default.AppConfig);
            WriteInPlace(BackupPath, json);
            WriteInPlace(_path, json);
        }
    }

    private static AppConfig? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, AppJsonContext.Default.AppConfig);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Truncate-and-write keeps the file (and its id); FileShare.Read lets an editor or a
    // scanner that has it open carry on.
    private static void WriteInPlace(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(content);
    }
}
