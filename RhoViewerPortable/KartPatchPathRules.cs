using System.Globalization;

namespace RhoViewerPortable;

internal static class KartPatchPathRules
{
    internal static string CreateBackupDirectoryName(DateTime timestamp)
        => $"{timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}_{Guid.NewGuid():N}";
}
