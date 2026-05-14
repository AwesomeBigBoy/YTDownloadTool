using System.Text.Json.Serialization;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Domain.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(StateJournalEvent))]
[JsonSerializable(typeof(JobSnapshot))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(ManifestFileEntry))]
public partial class AppJsonContext : JsonSerializerContext
{
}
