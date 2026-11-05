namespace YtDlpTool.Domain.Services;

/// <summary>
/// v1.5.0: the output names (folder + file stem) of the downloads in flight. Two downloads in
/// one folder never share a name while they run: the same video queued twice (1080p, then
/// 720p — the composer keeps its options on purpose), two videos with the same title, or
/// two 「自動改名」 answered at once. Before, they shared every work file and the final name:
/// one could delete the other's finished stream, take its file as "already downloaded", or
/// replace it. The 檔案已存在 probe only sees files that are finished.
/// </summary>
public sealed class OutputNames
{
    private readonly object _gate = new();
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Claims <paramref name="stem"/> in <paramref name="directory"/>; null when another download holds it.</summary>
    public IDisposable? TryClaim(string directory, string stem)
    {
        var key = Key(directory, stem);
        lock (_gate)
        {
            return _claimed.Add(key) ? new Claim(this, key) : null;
        }
    }

    /// <summary>
    /// Claims the first name among stem, stem_2, stem_3 … that no other download holds and
    /// for which <paramref name="exists"/> (a finished file under that stem) is false.
    /// </summary>
    public (string Stem, IDisposable Claim) ClaimFree(string directory, string stem, Func<string, bool> exists)
    {
        lock (_gate)
        {
            for (var i = 1; i < 1000; i++)
            {
                var candidate = i == 1 ? stem : $"{stem}_{i}";
                var key = Key(directory, candidate);
                if (_claimed.Contains(key) || exists(candidate)) continue;
                _claimed.Add(key);
                return (candidate, new Claim(this, key));
            }
            // As NextAvailableStem did before: a time suffix past 999.
            var stamped = $"{stem}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var stampedKey = Key(directory, stamped);
            _claimed.Add(stampedKey);
            return (stamped, new Claim(this, stampedKey));
        }
    }

    private void Release(string key)
    {
        lock (_gate) _claimed.Remove(key);
    }

    private static string Key(string directory, string stem)
    {
        string folder;
        try { folder = Path.GetFullPath(directory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { folder = directory; }
        return folder.TrimEnd('\\', '/') + "\\" + stem;
    }

    private sealed class Claim(OutputNames owner, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(key);
        }
    }
}
