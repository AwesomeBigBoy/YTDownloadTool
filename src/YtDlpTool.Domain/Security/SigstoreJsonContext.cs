using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Security;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SigstoreBundle))]
public partial class SigstoreJsonContext : JsonSerializerContext
{
}
