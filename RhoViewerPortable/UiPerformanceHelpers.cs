namespace RhoViewerPortable;

internal sealed class DebouncePolicy
{
    private readonly int _delayMilliseconds;
    private long _requestVersion;

    public DebouncePolicy(int delayMilliseconds)
    {
        if (delayMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(delayMilliseconds));
        _delayMilliseconds = delayMilliseconds;
    }

    public long BeginRequest() => Interlocked.Increment(ref _requestVersion);

    public bool IsCurrent(long requestVersion) => requestVersion == Volatile.Read(ref _requestVersion);

    public async Task<bool> WaitForQuietPeriodAsync(long requestVersion, CancellationToken cancellationToken)
    {
        await Task.Delay(_delayMilliseconds, cancellationToken);
        return IsCurrent(requestVersion);
    }
}

internal readonly record struct IndexRange(int Start, int Count)
{
    public int EndExclusive => Start + Count;
}

internal sealed record InvertSelectionPlan(
    bool SelectAllFirst,
    bool SelectedValueForRanges,
    IReadOnlyList<IndexRange> Ranges);

internal static class IndexSelectionPolicy
{
    public static InvertSelectionPlan CreateInvertPlan(int rowCount, IEnumerable<int> selectedIndices)
    {
        if (rowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount));

        var selected = selectedIndices
            .Where(index => index >= 0 && index < rowCount)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        var unselectedCount = rowCount - selected.Length;

        if (selected.Length <= unselectedCount)
        {
            // Selecting all is cheap in DataGridView. Then only touch the
            // previously selected indexes to produce the inverted selection.
            return new InvertSelectionPlan(true, false, ToRanges(selected));
        }

        // When most rows are selected, clearing all and selecting the gaps
        // touches only the smaller set of previously unselected indexes.
        return new InvertSelectionPlan(false, true, GetComplementRanges(rowCount, selected));
    }

    private static IReadOnlyList<IndexRange> GetComplementRanges(int rowCount, IReadOnlyList<int> sortedIndexes)
    {
        var ranges = new List<IndexRange>();
        var next = 0;
        foreach (var index in sortedIndexes)
        {
            if (index > next)
                ranges.Add(new IndexRange(next, index - next));
            next = index + 1;
        }

        if (next < rowCount)
            ranges.Add(new IndexRange(next, rowCount - next));
        return ranges;
    }

    private static IReadOnlyList<IndexRange> ToRanges(IReadOnlyList<int> sortedIndexes)
    {
        var ranges = new List<IndexRange>();
        if (sortedIndexes.Count == 0)
            return ranges;

        var start = sortedIndexes[0];
        var previous = start;
        for (var i = 1; i < sortedIndexes.Count; i++)
        {
            var index = sortedIndexes[i];
            if (index == previous + 1)
            {
                previous = index;
                continue;
            }

            ranges.Add(new IndexRange(start, previous - start + 1));
            start = previous = index;
        }

        ranges.Add(new IndexRange(start, previous - start + 1));
        return ranges;
    }
}

internal sealed record FolderTreeSource<TContext>(
    string DisplayName,
    TContext Context,
    Func<IEnumerable<string>> GetRelativePaths)
    where TContext : class;

internal sealed class FolderTreeNode<TTag>
{
    public FolderTreeNode(string text, TTag tag)
    {
        Text = text;
        Tag = tag;
    }

    public string Text { get; }
    public TTag Tag { get; }
    public List<FolderTreeNode<TTag>> Children { get; } = new();
}

internal static class FolderTreeBuilder
{
    public static FolderTreeNode<TTag> Build<TContext, TTag>(
        string rootText,
        TContext? selectedContext,
        IReadOnlyList<FolderTreeSource<TContext>> contexts,
        Func<TContext?, string, TTag> createTag,
        CancellationToken cancellationToken)
        where TContext : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = new FolderTreeNode<TTag>(rootText, createTag(selectedContext, string.Empty));
        foreach (var source in contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contextRoot = selectedContext is null
                ? new FolderTreeNode<TTag>(source.DisplayName, createTag(source.Context, string.Empty))
                : root;
            if (selectedContext is null)
                root.Children.Add(contextRoot);

            AddFolderNodes(source, contextRoot, createTag, cancellationToken);
        }

        return root;
    }

    private static void AddFolderNodes<TContext, TTag>(
        FolderTreeSource<TContext> source,
        FolderTreeNode<TTag> contextRoot,
        Func<TContext?, string, TTag> createTag,
        CancellationToken cancellationToken)
        where TContext : class
    {
        var nodes = new Dictionary<string, FolderTreeNode<TTag>>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = contextRoot
        };

        foreach (var relativePath in source.GetRelativePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
                continue;

            var path = string.Empty;
            var parent = contextRoot;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                path = string.IsNullOrEmpty(path) ? parts[i] : $"{path}/{parts[i]}";
                if (!nodes.TryGetValue(path, out var child))
                {
                    child = new FolderTreeNode<TTag>(parts[i], createTag(source.Context, path));
                    parent.Children.Add(child);
                    nodes[path] = child;
                }
                parent = child;
            }
        }
    }
}

internal static class SupersededWorkGate
{
    public static async Task<TResult> RunWorkThenCommitAsync<TWork, TResult>(
        SemaphoreSlim gate,
        Func<CancellationToken, Task<TWork>> work,
        Func<TWork, TResult> commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(commit);

        // Work may be a synchronous archive read wrapped in Task.Run and may
        // finish after cancellation. Keep it outside the gate so a newer
        // preview or archive cleanup is never queued behind a stale read.
        var result = await work(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return commit(result);
        }
        finally
        {
            gate.Release();
        }
    }
}
