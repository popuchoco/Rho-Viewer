namespace RhoViewerPortable;

internal enum UiMutationKind
{
    FilterControls,
    ResourceTreeRebuild,
    SelectionBatch,
    LayoutResize
}

/// <summary>
/// Tracks short-lived programmatic UI mutations and restores state reliably when
/// the operation exits, including nested updates and exceptions.
/// </summary>
internal sealed class UiMutationTracker
{
    private readonly int[] _depths = new int[Enum.GetValues<UiMutationKind>().Length];

    public bool IsActive(UiMutationKind kind) => Volatile.Read(ref _depths[(int)kind]) > 0;

    public IDisposable Enter(UiMutationKind kind)
    {
        Interlocked.Increment(ref _depths[(int)kind]);
        return new MutationScope(this, kind);
    }

    private void Exit(UiMutationKind kind)
    {
        if (Interlocked.Decrement(ref _depths[(int)kind]) < 0)
            throw new InvalidOperationException($"UI mutation scope underflow: {kind}.");
    }

    private sealed class MutationScope : IDisposable
    {
        private UiMutationTracker? _owner;
        private readonly UiMutationKind _kind;

        public MutationScope(UiMutationTracker owner, UiMutationKind kind)
        {
            _owner = owner;
            _kind = kind;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Exit(_kind);
        }
    }
}
