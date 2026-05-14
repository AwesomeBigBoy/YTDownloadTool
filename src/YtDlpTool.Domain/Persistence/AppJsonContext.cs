using System.Text.Json.Serialization;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
public partial class AppJsonContext : JsonSerializerContext
{
}
