using System.Text.Json.Serialization;

namespace YtDlpTool.Process;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(YtDlpMetadataDto))]
public partial class YtDlpJsonContext : JsonSerializerContext { }
