using YtDlpTool.Domain.Services;

namespace YtDlpTool.Domain.Tests.Services;

/// <summary>
/// v1.5.0: two downloads in one folder never share an output name while they run — the
/// same video queued twice (1080p, then 720p), or two videos with the same title.
/// </summary>
public class OutputNamesTests
{
    private const string Dir = @"C:\Downloads";
    private static bool None(string _) => false;

    [Fact]
    public void TryClaim_TheSameNameTwice_TheSecondIsRefused()
    {
        var names = new OutputNames();
        using var first = names.TryClaim(Dir, "Me at the zoo");

        Assert.NotNull(first);
        Assert.Null(names.TryClaim(Dir, "Me at the zoo"));
        Assert.Null(names.TryClaim(@"c:\downloads\", "ME AT THE ZOO"));   // Windows names
    }

    [Fact]
    public void AReleasedName_CanBeClaimedAgain()
    {
        var names = new OutputNames();
        names.TryClaim(Dir, "X")!.Dispose();

        using var again = names.TryClaim(Dir, "X");
        Assert.NotNull(again);
    }

    [Fact]
    public void OtherFolders_AreIndependent()
    {
        var names = new OutputNames();
        using var a = names.TryClaim(Dir, "X");
        using var b = names.TryClaim(@"D:\Other", "X");
        Assert.NotNull(b);
    }

    [Fact]
    public void ClaimFree_SkipsNamesInUseAndNamesOnDisk()
    {
        var names = new OutputNames();
        using var running = names.TryClaim(Dir, "X");

        var (stem, claim) = names.ClaimFree(Dir, "X", exists: s => s == "X_2");
        using (claim)
        {
            Assert.Equal("X_3", stem);
            Assert.Null(names.TryClaim(Dir, "X_3"));
        }
    }

    [Fact]
    public void ClaimFree_TheBaseNameWhenItIsFree()
    {
        var names = new OutputNames();
        var (stem, claim) = names.ClaimFree(Dir, "X", None);
        using (claim) Assert.Equal("X", stem);
    }

    [Fact]
    public void ClaimFree_FromManyThreadsAtOnce_NeverHandsOutOneNameTwice()
    {
        // 自動改名 answered for the rest of a batch: several downloads ask at the same time.
        var names = new OutputNames();
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            var (stem, _) = names.ClaimFree(Dir, "X", None);
            results.Add(stem);
        });

        Assert.Equal(16, results.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
