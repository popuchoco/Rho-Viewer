namespace RhoViewerPortable;

internal static class ImageDecodeLimits
{
    internal const int MaxDimension = 16_384;
    internal const int MaxDecodedBytes = 128 * 1024 * 1024;

    internal static void ValidateDimensions(long width, long height, string format)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new InvalidDataException($"{format} 圖片尺寸無效或超過 {MaxDimension} × {MaxDimension} 上限。");

        if (width * height > MaxDecodedBytes / 4)
            throw new InvalidDataException($"{format} 展開後圖片超過 {MaxDecodedBytes / (1024 * 1024)} MiB 上限。");
    }
}
