using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Updates;

public sealed class ManifestFileEntry
{
    [JsonPropertyName("name")]               public string Name { get; set; } = "";
    [JsonPropertyName("component")]          public UpdateComponent Component { get; set; }
    [JsonPropertyName("version")]            public string Version { get; set; } = "";
    [JsonPropertyName("downloadUrl")]        public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("sha256")]             public string Sha256 { get; set; } = "";
    [JsonPropertyName("signatureUrl")]       public string SignatureUrl { get; set; } = "";
    [JsonPropertyName("targetRelativePath")] public string TargetRelativePath { get; set; } = "";
}
