using System.Text.Json.Serialization;

namespace YtDlpTool.Process;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(YtDlpMetadataDto))]
[JsonSerializable(typeof(YtDlpPlaylistDto))]
public partial class YtDlpJsonContext : JsonSerializerContext { }
