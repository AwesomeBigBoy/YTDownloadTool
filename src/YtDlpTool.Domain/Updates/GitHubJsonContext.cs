using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Updates;

[JsonSerializable(typeof(GitHubReleaseDto))]
[JsonSerializable(typeof(List<GitHubReleaseDto>))]
public partial class GitHubJsonContext : JsonSerializerContext
{
}
