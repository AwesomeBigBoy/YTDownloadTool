using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Updates;

[JsonSerializable(typeof(GitHubReleaseDto))]
public partial class GitHubJsonContext : JsonSerializerContext
{
}
