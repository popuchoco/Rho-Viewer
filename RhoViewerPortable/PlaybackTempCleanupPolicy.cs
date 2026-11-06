namespace RhoViewerPortable;

internal enum PlaybackTempCleanupMode
{
    TrackedPlayerProcess,
    RetainedShellAssociation
}

internal static class PlaybackTempCleanupPolicy
{
    internal static PlaybackTempCleanupMode ForPlayerProcess(bool realPlayerProcessIsTracked)
        => realPlayerProcessIsTracked
            ? PlaybackTempCleanupMode.TrackedPlayerProcess
            : PlaybackTempCleanupMode.RetainedShellAssociation;

    internal static bool CanCleanup(PlaybackTempCleanupMode mode)
        => mode == PlaybackTempCleanupMode.TrackedPlayerProcess;

    internal static bool ShouldTrackForApplicationCleanup(PlaybackTempCleanupMode mode)
        => mode == PlaybackTempCleanupMode.TrackedPlayerProcess;
}
