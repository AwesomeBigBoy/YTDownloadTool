using System.Text.Json;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Persistence;

namespace YtDlpTool.Domain.Services;

public sealed class ConfigStore
{
    private readonly string _path;

    public ConfigStore(string path) => _path = path;

    public AppConfig Load()
    {
        if (!File.Exists(_path)) return AppConfig.CreateDefault();
        try
        {
            using var stream = File.OpenRead(_path);
            var cfg = JsonSerializer.Deserialize(stream, AppJsonContext.Default.AppConfig);
            return cfg ?? AppConfig.CreateDefault();
        }
        catch (JsonException)
        {
            return AppConfig.CreateDefault();
        }
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, config, AppJsonContext.Default.AppConfig);
        }
        if (File.Exists(_path)) File.Replace(tmp, _path, destinationBackupFileName: null);
        else File.Move(tmp, _path);
    }
}
