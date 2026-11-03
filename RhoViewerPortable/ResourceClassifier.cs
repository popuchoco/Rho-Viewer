namespace RhoViewerPortable;

internal enum ResourceKind
{
    Image,
    Audio,
    Model,
    Recording,
    StructuredData,
    Other
}

/// <summary>
/// Classifies resources for filtering and display without changing their original
/// archive paths or order. Keep this heuristic separate from format parsing.
/// </summary>
internal static class ResourceClassifier
{
    public static ResourceKind Classify(RhoItem item) => Classify(item.Extension);

    internal static ResourceKind Classify(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".dds" or ".tga" or ".webp" => ResourceKind.Image,
        ".ogg" or ".flac" or ".wav" or ".mp3" => ResourceKind.Audio,
        ".1s" => ResourceKind.Model,
        ".ksv" => ResourceKind.Recording,
        ".bml" or ".xml" or ".json" or ".txt" or ".ini" or ".lua" or ".cfg" or ".csv" => ResourceKind.StructuredData,
        _ => ResourceKind.Other
    };

    public static string GetDisplayName(ResourceKind kind) => kind switch
    {
        ResourceKind.Image => LanguageManager.Translate("resource.kind.image", "圖片"),
        ResourceKind.Audio => LanguageManager.Translate("resource.kind.audio", "音訊"),
        ResourceKind.Model => LanguageManager.Translate("resource.kind.model", "模型／場景"),
        ResourceKind.Recording => LanguageManager.Translate("resource.kind.recording", "KSV 記錄"),
        ResourceKind.StructuredData => LanguageManager.Translate("resource.kind.structured", "文字／結構資料"),
        _ => LanguageManager.Translate("resource.kind.other", "其他")
    };

    public static int GetSortOrder(ResourceKind kind) => kind switch
    {
        ResourceKind.Image => 0,
        ResourceKind.Audio => 1,
        ResourceKind.Model => 2,
        ResourceKind.Recording => 3,
        ResourceKind.StructuredData => 4,
        _ => 5
    };
}
