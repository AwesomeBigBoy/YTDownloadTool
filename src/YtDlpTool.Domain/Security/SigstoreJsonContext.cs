using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Security;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SigstoreBundle))]
[JsonSerializable(typeof(LegacyRekorBundle))]
[JsonSerializable(typeof(LegacyRekorPayload))]
public partial class SigstoreJsonContext : JsonSerializerContext
{
}
