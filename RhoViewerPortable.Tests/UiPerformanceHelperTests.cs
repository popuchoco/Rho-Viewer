using RhoViewerPortable;

namespace RhoViewerPortable.Tests;

internal static class UiPerformanceHelperTests
{
    public static void Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await TestDebounceLatestRequestWinsAsync();
        TestIndexBasedSelectionInversion();
        TestFolderTreeSnapshotBuildAndCancellation();
        await TestArchiveReadsAreSerializedAndCanceledWaitersAreSkippedAsync();
    }

    private static async Task TestDebounceLatestRequestWinsAsync()
    {
        var policy = new DebouncePolicy(delayMilliseconds: 35);
        var firstRequest = policy.BeginRequest();
        var firstWait = policy.WaitForQuietPeriodAsync(firstRequest, CancellationToken.None);
        await Task.Delay(5);
        var latestRequest = policy.BeginRequest();
        var latestWait = policy.WaitForQuietPeriodAsync(latestRequest, CancellationToken.None);

        Assert(!await firstWait, "an older search request must not apply after a newer text change");
        Assert(await latestWait, "the latest search request must apply after the quiet period");
    }

    private static void TestIndexBasedSelectionInversion()
    {
        var sparse = IndexSelectionPolicy.CreateInvertPlan(10, new[] { 5, 2, 1, 2, -1, 10 });
        Assert(sparse.SelectAllFirst && !sparse.SelectedValueForRanges, "sparse selection should select all, then clear its old indexes");
        AssertRanges(sparse.Ranges, new IndexRange(1, 2), new IndexRange(5, 1));

        var dense = IndexSelectionPolicy.CreateInvertPlan(10, Enumerable.Range(0, 8));
        Assert(!dense.SelectAllFirst && dense.SelectedValueForRanges, "dense selection should clear all, then select the old gaps");
        AssertRanges(dense.Ranges, new IndexRange(8, 2));

        var empty = IndexSelectionPolicy.CreateInvertPlan(4, Array.Empty<int>());
        Assert(empty.SelectAllFirst && empty.Ranges.Count == 0, "inverting an empty selection should use SelectAll without row iteration");
        var full = IndexSelectionPolicy.CreateInvertPlan(4, Enumerable.Range(0, 4));
        Assert(!full.SelectAllFirst && full.Ranges.Count == 0, "inverting a full selection should clear it without row iteration");
    }

    private static void TestFolderTreeSnapshotBuildAndCancellation()
    {
        var firstContext = new object();
        var secondContext = new object();
        var firstPaths = new[] { "root/a.txt", "root/sub/one.bin", "ROOT/SUB/two.bin", "top.txt" };
        var secondPaths = new[] { "other/file.txt" };
        var sources = new[]
        {
            new FolderTreeSource<object>("First", firstContext, () => firstPaths),
            new FolderTreeSource<object>("Second", secondContext, () => secondPaths)
        };
        // A tag factory can identify the owning context while the builder keeps
        // the tree shape independent of WinForms controls.
        string Tag(object? context, string path) =>
            $"{(ReferenceEquals(context, firstContext) ? "first" : ReferenceEquals(context, secondContext) ? "second" : "all")}/{path}";

        var root = FolderTreeBuilder.Build<object, string>("All", null, sources, Tag, CancellationToken.None);
        Assert(root.Children.Count == 2, "the all-archives root should group each archive");
        var firstRoot = root.Children[0];
        Assert(firstRoot.Text == "First" && firstRoot.Tag == "first/", "archive root should retain its context tag");
        Assert(firstRoot.Children.Count == 1 && firstRoot.Children[0].Text == "root", "repeated folder paths should share a node case-insensitively");
        var sub = firstRoot.Children[0].Children.Single();
        Assert(sub.Text == "sub" && sub.Tag == "first/root/sub", "folder tags should carry the complete relative path");

        var selectedRoot = FolderTreeBuilder.Build<object, string>("First", firstContext, new[] { sources[0] }, Tag, CancellationToken.None);
        Assert(selectedRoot.Children.Single().Text == "root", "a selected archive should place folders directly below its root");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            FolderTreeBuilder.Build<object, string>("All", null, sources, Tag, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidDataException("folder tree builder should honor cancellation before scanning paths");
    }

    private static async Task TestArchiveReadsAreSerializedAndCanceledWaitersAreSkippedAsync()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var oldCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var oldReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldRead = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeReads = 0;
        var maximumActiveReads = 0;
        var oldCommitCount = 0;

        var oldWork = SupersededWorkGate.RunWorkThenCommitAsync(
            gate,
            async _ =>
            {
                var active = Interlocked.Increment(ref activeReads);
                UpdateMaximum(ref maximumActiveReads, active);
                oldReadStarted.TrySetResult(true);
                try
                {
                    return await releaseOldRead.Task;
                }
                finally
                {
                    Interlocked.Decrement(ref activeReads);
                }
            },
            value =>
            {
                oldCommitCount++;
                return value;
            },
            oldCancellation.Token);

        await oldReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var queuedWorkStarted = false;
        var canceledWaiter = SupersededWorkGate.RunWorkThenCommitAsync(
            gate,
            _ =>
            {
                queuedWorkStarted = true;
                return Task.FromResult("must not run");
            },
            value => value,
            queuedCancellation.Token);
        queuedCancellation.Cancel();
        try
        {
            await canceledWaiter;
            throw new InvalidDataException("a canceled preview waiting for the archive read gate should not run");
        }
        catch (OperationCanceledException)
        {
            Assert(!queuedWorkStarted, "a canceled queued preview must be skipped before reading");
        }

        var latestReadCommitted = false;
        var latestWork = SupersededWorkGate.RunWorkThenCommitAsync(
            gate,
            _ =>
            {
                var active = Interlocked.Increment(ref activeReads);
                UpdateMaximum(ref maximumActiveReads, active);
                latestReadStarted.TrySetResult(true);
                Interlocked.Decrement(ref activeReads);
                return Task.FromResult("latest");
            },
            value =>
            {
                latestReadCommitted = true;
                return value;
            },
            CancellationToken.None);

        await Task.Delay(100);
        Assert(!latestReadStarted.Task.IsCompleted, "a newer preview must wait while the stale synchronous archive read is active");
        oldCancellation.Cancel();
        releaseOldRead.TrySetResult("stale");
        try
        {
            await oldWork;
            throw new InvalidDataException("a superseded preview should discard its completed read");
        }
        catch (OperationCanceledException)
        {
            Assert(oldCommitCount == 0, "a superseded read must not commit stale preview bytes");
        }

        var latest = await latestWork;
        Assert(latest == "latest", "the latest preview should run after an older read releases the gate");
        Assert(latestReadStarted.Task.IsCompleted && latestReadCommitted, "the latest preview must run and commit after the old read releases the gate");
        Assert(maximumActiveReads == 1, "archive reads must never overlap");
        Assert(gate.CurrentCount == 1, "the gate must be released after latest and canceled work");
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (value <= current || Interlocked.CompareExchange(ref maximum, value, current) == current)
                return;
        }
    }

    private static void AssertRanges(IReadOnlyList<IndexRange> actual, params IndexRange[] expected)
    {
        Assert(actual.SequenceEqual(expected), $"expected index ranges [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
