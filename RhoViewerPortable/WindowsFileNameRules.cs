using System.Text;

namespace RhoViewerPortable;

internal static class WindowsFileNameRules
{
    private const string WindowsInvalidCharacters = "<>:\"/\\|?*";

    internal static string SanitizeFileName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var invalid = character < 32 || WindowsInvalidCharacters.Contains(character) ||
                          Path.GetInvalidFileNameChars().Contains(character);
            builder.Append(invalid ? '_' : character);
        }

        var sanitized = builder.ToString().TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
            return "unnamed";
        return IsReservedDeviceName(sanitized) ? "_" + sanitized : sanitized;
    }

    internal static string CreatePlaybackTempFileName(string name, string extension)
    {
        var fileName = SanitizeFileName(name);
        return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + extension;
    }

    internal static bool IsSafePathSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment is "." or ".." ||
            segment.EndsWith(' ') || segment.EndsWith('.'))
            return false;

        foreach (var character in segment)
        {
            if (character < 32 || WindowsInvalidCharacters.Contains(character) ||
                Path.GetInvalidFileNameChars().Contains(character))
                return false;
        }

        return !IsReservedDeviceName(segment);
    }

    internal static bool IsReservedDeviceName(string segment)
    {
        var baseName = segment.Split('.')[0].TrimEnd(' ', '.');
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (baseName.Length == 4 &&
                (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                baseName[3] is >= '1' and <= '9');
    }
}
