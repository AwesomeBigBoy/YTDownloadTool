using System.Text.Json.Serialization;

namespace YtDlpTool.Domain.Persistence;

/// <summary>
/// Compact (single-line) JSON source-gen context for JSONL persistence.
/// AppJsonContext uses WriteIndented = true, which is incompatible with one-event-per-line format.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StateJournalEvent))]
[JsonSerializable(typeof(JobSnapshot))]
public partial class JournalJsonContext : JsonSerializerContext
{
}
