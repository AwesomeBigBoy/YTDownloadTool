using System.Collections.Concurrent;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Domain.Services;

/// <summary>
/// v1.5.0: 「檔案已存在」 one question at a time, and an answer given "for the rest of this
/// batch" kept for that batch. Downloads run side by side (設定 → 同時下載數), so several
/// can reach an existing file together; each used to open its own dialog, stacked on the
/// first, and re-adding a partly downloaded playlist asked once per video already there.
/// </summary>
public sealed class ConflictDecisions
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly ConcurrentDictionary<Guid, FilenameConflictResolution> _forBatch = new();

    /// <param name="batchId">The playlist / multi-link batch the download came from; null for a single video.</param>
    /// <param name="ask">Shows 「檔案已存在」; its argument says whether to offer "the same for the rest of this batch".</param>
    /// <param name="cancellationToken">The download's: cancelling it stops the wait for its turn.</param>
    public async Task<FilenameConflictResolution> DecideAsync(Guid? batchId,
        Func<bool, Task<(FilenameConflictResolution Choice, bool ForTheRest)>> ask,
        CancellationToken cancellationToken = default)
    {
        if (batchId is { } known && _forBatch.TryGetValue(known, out var early)) return early;

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Answered for this batch while this one waited its turn.
            if (batchId is { } id && _forBatch.TryGetValue(id, out var answered)) return answered;

            var (choice, forTheRest) = await ask(batchId is not null).ConfigureAwait(false);
            if (forTheRest && batchId is { } batch) _forBatch[batch] = choice;
            return choice;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
