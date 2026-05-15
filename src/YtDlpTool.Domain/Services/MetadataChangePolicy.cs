using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

/// <summary>
/// Pure decision helper for "did the user just switch to a different video?" used by
/// MainViewModel.OnCurrentMetadataChanged. Extracted so the rule can be unit-tested
/// without spinning up WPF / AppHost.
/// </summary>
public static class MetadataChangePolicy
{
    /// <summary>
    /// Returns true only when both old and new metadata represent real videos AND their
    /// VideoIds differ. Transitions null→meta (first paste) and meta→null (cleared / parse
    /// error) intentionally return false so the user's last-selected options survive.
    /// </summary>
    public static bool ShouldResetOptions(VideoMetadata? oldMeta, VideoMetadata? newMeta)
    {
        var oldId = oldMeta?.VideoId;
        var newId = newMeta?.VideoId;
        if (oldId is null || newId is null) return false;
        return !string.Equals(oldId, newId, StringComparison.Ordinal);
    }
}
