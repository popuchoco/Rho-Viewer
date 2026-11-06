using RhoViewerPortable;

namespace RhoViewerPortable.Tests;

internal static class LowFixPortableTests
{
    public static void Run()
    {
        TestShellAssociationDefersCleanup();
        TestSharedWindowsFileNameRules();
        TestBackupDirectoryNamesAreUnique();
        TestPortableDirectoryLeaseRejectsSymlink();
    }

    private static void TestShellAssociationDefersCleanup()
    {
        var shellHandoff = PlaybackTempCleanupPolicy.ForPlayerProcess(realPlayerProcessIsTracked: false);
        Assert(shellHandoff == PlaybackTempCleanupMode.RetainedShellAssociation,
            "an untracked shell association must be treated as a retained handoff");
        Assert(!PlaybackTempCleanupPolicy.CanCleanup(shellHandoff),
            "a shell helper exit must not trigger playback temp cleanup");
        Assert(!PlaybackTempCleanupPolicy.ShouldTrackForApplicationCleanup(shellHandoff),
            "a shell-associated file must not enter application-exit cleanup");

        var directPlayer = PlaybackTempCleanupPolicy.ForPlayerProcess(realPlayerProcessIsTracked: true);
        Assert(directPlayer == PlaybackTempCleanupMode.TrackedPlayerProcess && PlaybackTempCleanupPolicy.CanCleanup(directPlayer),
            "a directly tracked player should retain normal cleanup behavior");
        Assert(PlaybackTempCleanupPolicy.ShouldTrackForApplicationCleanup(directPlayer),
            "a tracked player directory should remain under application cleanup control");
    }

    private static void TestSharedWindowsFileNameRules()
    {
        foreach (var deviceName in new[] { "CON", "NUL.txt", "PRN.log", "AUX.", "COM1.wav", "LPT9 .ogg", "CON .txt" })
        {
            var sanitizedExportName = WindowsFileNameRules.SanitizeFileName(deviceName);
            var playbackName = WindowsFileNameRules.CreatePlaybackTempFileName(deviceName, ".ogg");
            Assert(WindowsFileNameRules.IsSafePathSegment(sanitizedExportName),
                $"export sanitizer returned a Windows-invalid name for {deviceName}");
            Assert(WindowsFileNameRules.IsSafePathSegment(playbackName),
                $"playback temp sanitizer returned a Windows-invalid name for {deviceName}");
        }

        Assert(WindowsFileNameRules.SanitizeFileName("audio. ") == "audio",
            "export names must trim Windows-ignored trailing dots and spaces");
        Assert(WindowsFileNameRules.CreatePlaybackTempFileName("NUL. ", ".ogg") == "_NUL.ogg",
            "playback names must sanitize the device basename before adding the extension");
        Assert(!WindowsFileNameRules.IsSafePathSegment("folder."), "updater path rules must reject trailing dots");
        Assert(!WindowsFileNameRules.IsSafePathSegment("folder "), "updater path rules must reject trailing spaces");
        Assert(!WindowsFileNameRules.IsSafePathSegment("NUL.txt"), "updater path rules must reject device names with extensions");
    }

    private static void TestBackupDirectoryNamesAreUnique()
    {
        var sameTimestamp = new DateTime(2026, 11, 6, 12, 34, 56, DateTimeKind.Local);
        var first = KartPatchPathRules.CreateBackupDirectoryName(sameTimestamp);
        var second = KartPatchPathRules.CreateBackupDirectoryName(sameTimestamp);
        Assert(first != second, "backup directory names must remain unique within the same second");
        Assert(first.StartsWith("20261106_123456_", StringComparison.Ordinal) && first.Length == "20261106_123456_".Length + 32,
            "backup directory name must contain the timestamp and a full guid");
    }

    private static void TestPortableDirectoryLeaseRejectsSymlink()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "RhoViewerPortable.L3." + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
            var rejected = false;
            try
            {
                using var ignored = WindowsDirectoryPathLease.Acquire(link);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            Assert(rejected, "the portable updater directory lease accepted a symlink");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
