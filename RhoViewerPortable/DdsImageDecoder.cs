using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RhoViewerPortable;

internal sealed class DdsPreview : IDisposable
{
    private Bitmap? _bitmap;

    public DdsPreview(Bitmap bitmap, string format)
    {
        _bitmap = bitmap;
        Format = format;
    }

    public Bitmap Bitmap => _bitmap ?? throw new ObjectDisposedException(nameof(DdsPreview));
    public string Format { get; }

    public Bitmap TakeBitmap()
    {
        var bitmap = _bitmap ?? throw new ObjectDisposedException(nameof(DdsPreview));
        _bitmap = null;
        return bitmap;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

/// <summary>
/// Small bounded software decoder for the common 2D DDS formats used by game textures.
/// It intentionally decodes only the top mip and does not implement cubemaps, arrays,
/// volume textures, BC6H, or BC7.
/// </summary>
internal static class DdsImageDecoder
{
    internal const int MaxInputBytes = 128 * 1024 * 1024;
    internal const int MaxDecodedBytes = 128 * 1024 * 1024;
    internal const int MaxDimension = 16_384;
    private const uint DdpfFourCc = 0x4;
    private const uint DdpfRgb = 0x40;
    private const uint DdpfLuminance = 0x2_0000;
    private const uint DdsdRequired = 0x1007;
    private const uint DdsdPitch = 0x8;
    private const uint DdsCapsTexture = 0x1000;
    private const uint DdsCaps2CubeMapMask = 0xFE00;
    private const uint DdsCaps2Volume = 0x20_0000;
    private const uint DdsDx10CubeMap = 0x4;

    private enum PixelKind
    {
        Bc1,
        Bc2,
        Bc3,
        Bc4,
        Bc5,
        Rgba8,
        Bgra8,
        Bgrx8,
        R8,
        Rg8,
        Masked
    }

    private readonly struct PixelFormatInfo
    {
        public PixelFormatInfo(
            PixelKind kind,
            string name,
            int bytesPerPixel = 0,
            bool signed = false,
            bool premultiplied = false,
            bool srgb = false,
            bool luminance = false,
            uint redMask = 0,
            uint greenMask = 0,
            uint blueMask = 0,
            uint alphaMask = 0)
        {
            Kind = kind;
            Name = name;
            BytesPerPixel = bytesPerPixel;
            Signed = signed;
            Premultiplied = premultiplied;
            Srgb = srgb;
            Luminance = luminance;
            RedMask = redMask;
            GreenMask = greenMask;
            BlueMask = blueMask;
            AlphaMask = alphaMask;
        }

        public PixelKind Kind { get; }
        public string Name { get; }
        public int BytesPerPixel { get; }
        public bool Signed { get; }
        public bool Premultiplied { get; }
        public bool Srgb { get; }
        public bool Luminance { get; }
        public uint RedMask { get; }
        public uint GreenMask { get; }
        public uint BlueMask { get; }
        public uint AlphaMask { get; }
    }

    private readonly struct HeaderInfo
    {
        public HeaderInfo(int width, int height, int dataOffset, int rowPitch, PixelFormatInfo format)
        {
            Width = width;
            Height = height;
            DataOffset = dataOffset;
            RowPitch = rowPitch;
            Format = format;
        }

        public int Width { get; }
        public int Height { get; }
        public int DataOffset { get; }
        public int RowPitch { get; }
        public PixelFormatInfo Format { get; }
    }

    public static DdsPreview Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var header = ReadHeader(data);
        var pixelCount = checked((long)header.Width * header.Height);
        var decodedBytes = checked(pixelCount * 4);
        if (decodedBytes > MaxDecodedBytes)
            throw new InvalidDataException($"DDS 展開後圖片過大（上限 {MaxDecodedBytes / (1024 * 1024)} MiB）。");

        var requiredBytes = GetRequiredSurfaceBytes(header);
        if (requiredBytes > data.Length - header.DataOffset)
            throw new InvalidDataException("DDS 像素資料截斷或長度不足。");

        var bgra = new byte[(int)decodedBytes];
        switch (header.Format.Kind)
        {
            case PixelKind.Bc1:
            case PixelKind.Bc2:
            case PixelKind.Bc3:
            case PixelKind.Bc4:
            case PixelKind.Bc5:
                DecodeBlockCompressed(data, header, bgra);
                break;
            case PixelKind.Rgba8:
            case PixelKind.Bgra8:
            case PixelKind.Bgrx8:
            case PixelKind.R8:
            case PixelKind.Rg8:
            case PixelKind.Masked:
                DecodeLinear(data, header, bgra);
                break;
            default:
                throw new NotSupportedException("不支援此 DDS 圖像格式。");
        }

        var bitmap = new Bitmap(header.Width, header.Height, PixelFormat.Format32bppArgb);
        try
        {
            var rect = new Rectangle(0, 0, header.Width, header.Height);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = checked(header.Width * 4);
                for (var y = 0; y < header.Height; y++)
                    Marshal.Copy(bgra, y * rowBytes, IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), rowBytes);
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return new DdsPreview(bitmap, header.Format.Name + (header.Format.Srgb ? " sRGB" : string.Empty));
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static HeaderInfo ReadHeader(byte[] data)
    {
        if (data.Length > MaxInputBytes)
            throw new InvalidDataException($"DDS 檔案超過分析上限 {MaxInputBytes / (1024 * 1024)} MiB。");
        if (data.Length < 128 || !data.AsSpan(0, 4).SequenceEqual("DDS "u8))
            throw new InvalidDataException("不是有效的 DDS 檔案標頭。");
        if (ReadUInt32(data, 4) != 124 || ReadUInt32(data, 76) != 32)
            throw new InvalidDataException("DDS 標頭大小無效。");
        if ((ReadUInt32(data, 8) & DdsdRequired) != DdsdRequired ||
            (ReadUInt32(data, 108) & DdsCapsTexture) == 0)
            throw new InvalidDataException("DDS 缺少必要的 2D 紋理標頭欄位。");

        var heightRaw = ReadUInt32(data, 12);
        var widthRaw = ReadUInt32(data, 16);
        if (widthRaw == 0 || heightRaw == 0 || widthRaw > MaxDimension || heightRaw > MaxDimension)
            throw new InvalidDataException($"DDS 圖片尺寸無效或超過 {MaxDimension} × {MaxDimension} 上限。");

        var width = (int)widthRaw;
        var height = (int)heightRaw;
        var pixelCount = checked((long)width * height);
        if (pixelCount * 4 > MaxDecodedBytes)
            throw new InvalidDataException($"DDS 展開後圖片超過 {MaxDecodedBytes / (1024 * 1024)} MiB 上限。");

        var depth = ReadUInt32(data, 24);
        var caps2 = ReadUInt32(data, 112);
        if (depth > 1 || (caps2 & DdsCaps2Volume) != 0 || (caps2 & DdsCaps2CubeMapMask) != 0)
            throw new NotSupportedException("目前只預覽一般 2D DDS；不支援立方體或體積貼圖。");

        var pixelFlags = ReadUInt32(data, 80);
        var fourCc = ReadUInt32(data, 84);
        var dataOffset = 128;
        PixelFormatInfo format;
        if ((pixelFlags & DdpfFourCc) != 0)
        {
            if (fourCc == FourCc("DX10"u8))
            {
                if (data.Length < 148)
                    throw new InvalidDataException("DDS DX10 延伸標頭遭截斷。");
                var dxgiFormat = ReadUInt32(data, 128);
                var resourceDimension = ReadUInt32(data, 132);
                var miscFlag = ReadUInt32(data, 136);
                var arraySize = ReadUInt32(data, 140);
                if (resourceDimension != 3 || arraySize != 1 || (miscFlag & DdsDx10CubeMap) != 0)
                    throw new NotSupportedException("目前只預覽單一 2D DDS；不支援陣列、立方體或非 2D 資源。");
                format = FromDxgiFormat(dxgiFormat);
                dataOffset = 148;
            }
            else
            {
                format = FromLegacyFourCc(fourCc);
            }
        }
        else
        {
            format = FromLegacyMasks(data, pixelFlags);
        }

        var rowPitch = 0;
        var linearBytesPerPixel = GetLinearBytesPerPixel(format.Kind, format.BytesPerPixel);
        if (linearBytesPerPixel > 0)
        {
            var minimumPitch = checked(width * linearBytesPerPixel);
            var declaredPitch = ReadUInt32(data, 20);
            var hasPitch = (ReadUInt32(data, 8) & DdsdPitch) != 0;
            if (hasPitch && declaredPitch > MaxInputBytes)
                throw new InvalidDataException("DDS 每列像素資料的 pitch 超過上限。");
            rowPitch = hasPitch && declaredPitch != 0 ? (int)declaredPitch : minimumPitch;
            if (rowPitch < minimumPitch || rowPitch > MaxInputBytes)
                throw new InvalidDataException("DDS 每列像素資料的 pitch 無效。");
        }

        return new HeaderInfo(width, height, dataOffset, rowPitch, format);
    }

    private static PixelFormatInfo FromLegacyFourCc(uint fourCc) => fourCc switch
    {
        0x3154_5844 => new(PixelKind.Bc1, "BC1 / DXT1"), // DXT1
        0x3254_5844 => new(PixelKind.Bc2, "BC2 / DXT2", premultiplied: true),
        0x3354_5844 => new(PixelKind.Bc2, "BC2 / DXT3"),
        0x3454_5844 => new(PixelKind.Bc3, "BC3 / DXT4", premultiplied: true),
        0x3554_5844 => new(PixelKind.Bc3, "BC3 / DXT5"),
        0x3149_5441 or 0x5534_4342 => new(PixelKind.Bc4, "BC4 / ATI1"),
        0x5334_4342 => new(PixelKind.Bc4, "BC4 signed"),
        0x3249_5441 or 0x5535_4342 => new(PixelKind.Bc5, "BC5 / ATI2"),
        0x5335_4342 => new(PixelKind.Bc5, "BC5 signed", signed: true),
        _ => throw new NotSupportedException($"不支援 DDS FourCC：{FourCcText(fourCc)}。")
    };

    private static PixelFormatInfo FromDxgiFormat(uint format) => format switch
    {
        28 => new(PixelKind.Rgba8, "R8G8B8A8"),
        29 => new(PixelKind.Rgba8, "R8G8B8A8", srgb: true),
        87 => new(PixelKind.Bgra8, "B8G8R8A8"),
        91 => new(PixelKind.Bgra8, "B8G8R8A8", srgb: true),
        88 => new(PixelKind.Bgrx8, "B8G8R8X8"),
        93 => new(PixelKind.Bgrx8, "B8G8R8X8", srgb: true),
        61 => new(PixelKind.R8, "R8"),
        49 => new(PixelKind.Rg8, "R8G8"),
        71 => new(PixelKind.Bc1, "BC1"),
        72 => new(PixelKind.Bc1, "BC1", srgb: true),
        74 => new(PixelKind.Bc2, "BC2"),
        75 => new(PixelKind.Bc2, "BC2", srgb: true),
        77 => new(PixelKind.Bc3, "BC3"),
        78 => new(PixelKind.Bc3, "BC3", srgb: true),
        80 => new(PixelKind.Bc4, "BC4"),
        81 => new(PixelKind.Bc4, "BC4 signed", signed: true),
        83 => new(PixelKind.Bc5, "BC5"),
        84 => new(PixelKind.Bc5, "BC5 signed", signed: true),
        _ => throw new NotSupportedException($"不支援 DDS DXGI 格式編號：{format}。")
    };

    private static PixelFormatInfo FromLegacyMasks(byte[] data, uint pixelFlags)
    {
        var luminance = (pixelFlags & DdpfLuminance) != 0;
        if ((pixelFlags & (DdpfRgb | DdpfLuminance)) == 0)
            throw new NotSupportedException("不支援此 DDS 像素格式標記。");

        var bits = ReadUInt32(data, 88);
        if (bits is not (8 or 16 or 24 or 32))
            throw new NotSupportedException($"不支援未壓縮 DDS 的 {bits} bpp 像素資料。");

        var red = ReadUInt32(data, 92);
        var green = ReadUInt32(data, 96);
        var blue = ReadUInt32(data, 100);
        var alpha = ReadUInt32(data, 104);
        if (luminance && red != 0 && green == 0 && blue == 0)
            green = blue = red;
        if (red == 0 || green == 0 || blue == 0 ||
            (!luminance && ((red & green) != 0 || (red & blue) != 0 || (green & blue) != 0)) ||
            (alpha & (red | green | blue)) != 0)
            throw new InvalidDataException("DDS RGB 色彩遮罩缺失或互相重疊。");

        var bytesPerPixel = (int)bits / 8;
        var validBits = bits == 32 ? uint.MaxValue : (1u << (int)bits) - 1;
        if (((red | green | blue | alpha) & ~validBits) != 0 ||
            !IsContiguousMask(red) || !IsContiguousMask(green) || !IsContiguousMask(blue) ||
            (alpha != 0 && !IsContiguousMask(alpha)))
            throw new InvalidDataException("DDS RGB 色彩遮罩不符合像素位元寬度。");

        return new PixelFormatInfo(PixelKind.Masked, $"RGB {bits} bpp", bytesPerPixel,
            luminance: luminance, redMask: red, greenMask: green, blueMask: blue, alphaMask: alpha);
    }

    private static bool IsContiguousMask(uint mask)
    {
        if (mask == 0)
            return false;
        var shifted = mask >> BitOperations.TrailingZeroCount(mask);
        return (shifted & (shifted + 1)) == 0;
    }

    private static long GetRequiredSurfaceBytes(HeaderInfo header)
    {
        var linearBytesPerPixel = GetLinearBytesPerPixel(header.Format.Kind, header.Format.BytesPerPixel);
        if (linearBytesPerPixel > 0)
        {
            return checked((long)header.RowPitch * header.Height);
        }

        var blockBytes = header.Format.Kind is PixelKind.Bc1 or PixelKind.Bc4 ? 8 : 16;
        var blocksWide = (header.Width + 3L) / 4;
        var blocksHigh = (header.Height + 3L) / 4;
        return checked(blocksWide * blocksHigh * blockBytes);
    }

    private static void DecodeLinear(byte[] data, HeaderInfo header, byte[] target)
    {
        var format = header.Format;
        if (format.Kind == PixelKind.Masked)
        {
            for (var y = 0; y < header.Height; y++)
            {
                var rowOffset = header.DataOffset + y * header.RowPitch;
                for (var x = 0; x < header.Width; x++)
                {
                    var pixelOffset = rowOffset + x * format.BytesPerPixel;
                    uint value = 0;
                    for (var byteIndex = 0; byteIndex < format.BytesPerPixel; byteIndex++)
                        value |= (uint)data[pixelOffset + byteIndex] << (byteIndex * 8);
                    var red = ExtractChannel(value, format.RedMask);
                    var green = format.Luminance ? red : ExtractChannel(value, format.GreenMask);
                    var blue = format.Luminance ? red : ExtractChannel(value, format.BlueMask);
                    var alpha = format.AlphaMask == 0 ? (byte)255 : ExtractChannel(value, format.AlphaMask);
                    WritePixel(target, header.Width, x, y, red, green, blue, alpha);
                }
            }
            return;
        }

        var bytesPerPixel = GetLinearBytesPerPixel(format.Kind, format.BytesPerPixel);
        for (var y = 0; y < header.Height; y++)
        {
            var cursor = header.DataOffset + y * header.RowPitch;
            for (var x = 0; x < header.Width; x++)
            {
                byte red, green, blue, alpha;
                switch (format.Kind)
                {
                    case PixelKind.Rgba8:
                        red = data[cursor]; green = data[cursor + 1]; blue = data[cursor + 2]; alpha = data[cursor + 3];
                        break;
                    case PixelKind.Bgra8:
                        blue = data[cursor]; green = data[cursor + 1]; red = data[cursor + 2]; alpha = data[cursor + 3];
                        break;
                    case PixelKind.Bgrx8:
                        blue = data[cursor]; green = data[cursor + 1]; red = data[cursor + 2]; alpha = 255;
                        break;
                    case PixelKind.Rg8:
                        red = data[cursor]; green = data[cursor + 1]; blue = 0; alpha = 255;
                        break;
                    default:
                        red = green = blue = data[cursor]; alpha = 255;
                        break;
                }
                WritePixel(target, header.Width, x, y, red, green, blue, alpha);
                cursor += bytesPerPixel;
            }
        }
    }

    private static void DecodeBlockCompressed(byte[] data, HeaderInfo header, byte[] target)
    {
        var format = header.Format;
        var blockBytes = format.Kind is PixelKind.Bc1 or PixelKind.Bc4 ? 8 : 16;
        var blocksWide = (header.Width + 3) / 4;
        var blocksHigh = (header.Height + 3) / 4;
        var cursor = header.DataOffset;
        Span<byte> alphaValues = stackalloc byte[16];
        Span<byte> channelR = stackalloc byte[16];
        Span<byte> channelG = stackalloc byte[16];

        for (var blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0; blockX < blocksWide; blockX++)
            {
                var block = data.AsSpan(cursor, blockBytes);
                switch (format.Kind)
                {
                    case PixelKind.Bc1:
                        DecodeColorBlock(block, target, header.Width, blockX, blockY, allowTransparency: true, alphaOverrides: ReadOnlySpan<byte>.Empty, premultiplied: false);
                        break;
                    case PixelKind.Bc2:
                        for (var i = 0; i < 16; i++)
                        {
                            var packed = (byte)(block[i / 2] >> ((i & 1) * 4));
                            alphaValues[i] = (byte)((packed & 0x0f) * 17);
                        }
                        DecodeColorBlock(block[8..], target, header.Width, blockX, blockY, allowTransparency: false, alphaOverrides: alphaValues, premultiplied: format.Premultiplied);
                        break;
                    case PixelKind.Bc3:
                        DecodeBc4Values(block[..8], signed: false, values: alphaValues);
                        DecodeColorBlock(block[8..], target, header.Width, blockX, blockY, allowTransparency: false, alphaOverrides: alphaValues, premultiplied: format.Premultiplied);
                        break;
                    case PixelKind.Bc4:
                        DecodeBc4Values(block, format.Signed, channelR);
                        WriteSingleChannelBlock(channelR, target, header.Width, header.Height, blockX, blockY);
                        break;
                    case PixelKind.Bc5:
                        DecodeBc4Values(block[..8], format.Signed, channelR);
                        DecodeBc4Values(block[8..], format.Signed, channelG);
                        WriteTwoChannelBlock(channelR, channelG, target, header.Width, header.Height, blockX, blockY);
                        break;
                }
                cursor += blockBytes;
            }
        }
    }

    private static void DecodeColorBlock(ReadOnlySpan<byte> block, byte[] target, int width,
        int blockX, int blockY, bool allowTransparency, ReadOnlySpan<byte> alphaOverrides, bool premultiplied)
    {
        var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block[..2]);
        var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..4]);
        Span<byte> palette = stackalloc byte[16];
        Decode565(color0, palette[..4]);
        Decode565(color1, palette[4..8]);
        palette[3] = palette[7] = 255;
        var fourColor = !allowTransparency || color0 > color1;
        if (fourColor)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                palette[8 + channel] = (byte)((2 * palette[channel] + palette[4 + channel]) / 3);
                palette[12 + channel] = (byte)((palette[channel] + 2 * palette[4 + channel]) / 3);
            }
            palette[11] = palette[15] = 255;
        }
        else
        {
            for (var channel = 0; channel < 3; channel++)
                palette[8 + channel] = (byte)((palette[channel] + palette[4 + channel]) / 2);
            palette[11] = 255;
            palette[12] = palette[13] = palette[14] = 0;
            palette[15] = 0;
        }

        var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..8]);
        for (var i = 0; i < 16; i++)
        {
            var paletteIndex = (int)((indices >> (i * 2)) & 3);
            var paletteOffset = paletteIndex * 4;
            var alpha = alphaOverrides.Length == 16 ? alphaOverrides[i] : palette[paletteOffset + 3];
            var red = palette[paletteOffset];
            var green = palette[paletteOffset + 1];
            var blue = palette[paletteOffset + 2];
            if (premultiplied)
            {
                if (alpha == 0)
                    red = green = blue = 0;
                else
                {
                    red = Unpremultiply(red, alpha);
                    green = Unpremultiply(green, alpha);
                    blue = Unpremultiply(blue, alpha);
                }
            }

            WritePixel(target, width, blockX * 4 + (i & 3), blockY * 4 + (i >> 2), red, green, blue, alpha);
        }
    }

    private static byte Unpremultiply(byte value, byte alpha) => (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);

    private static void Decode565(ushort value, Span<byte> color)
    {
        var red = (value >> 11) & 0x1f;
        var green = (value >> 5) & 0x3f;
        var blue = value & 0x1f;
        color[0] = (byte)((red * 255 + 15) / 31);
        color[1] = (byte)((green * 255 + 31) / 63);
        color[2] = (byte)((blue * 255 + 15) / 31);
    }

    private static void DecodeBc4Values(ReadOnlySpan<byte> block, bool signed, Span<byte> values)
    {
        Span<int> palette = stackalloc int[8];
        if (signed)
        {
            palette[0] = Math.Max(-127, (int)(sbyte)block[0]);
            palette[1] = Math.Max(-127, (int)(sbyte)block[1]);
        }
        else
        {
            palette[0] = block[0];
            palette[1] = block[1];
        }

        if (palette[0] > palette[1])
        {
            for (var i = 1; i <= 6; i++)
                palette[i + 1] = ((7 - i) * palette[0] + i * palette[1]) / 7;
        }
        else
        {
            for (var i = 1; i <= 4; i++)
                palette[i + 1] = ((5 - i) * palette[0] + i * palette[1]) / 5;
            palette[6] = signed ? -127 : 0;
            palette[7] = signed ? 127 : 255;
        }

        ulong indices = 0;
        for (var i = 0; i < 6; i++)
            indices |= (ulong)block[2 + i] << (8 * i);
        for (var i = 0; i < 16; i++)
        {
            var value = palette[(int)((indices >> (i * 3)) & 7)];
            values[i] = signed ? SignedToByte(value) : (byte)value;
        }
    }

    private static byte SignedToByte(int value)
    {
        var clamped = Math.Clamp(value, -127, 127) + 127;
        return (byte)((clamped * 255 + 127) / 254);
    }

    private static void WriteSingleChannelBlock(ReadOnlySpan<byte> values, byte[] target, int width, int height, int blockX, int blockY)
    {
        for (var i = 0; i < 16; i++)
        {
            var x = blockX * 4 + (i & 3);
            var y = blockY * 4 + (i >> 2);
            if (x >= width || y >= height)
                continue;
            var value = values[i];
            WritePixel(target, width, x, y, value, value, value, 255);
        }
    }

    private static void WriteTwoChannelBlock(ReadOnlySpan<byte> red, ReadOnlySpan<byte> green, byte[] target, int width, int height, int blockX, int blockY)
    {
        for (var i = 0; i < 16; i++)
        {
            var x = blockX * 4 + (i & 3);
            var y = blockY * 4 + (i >> 2);
            if (x < width && y < height)
                WritePixel(target, width, x, y, red[i], green[i], 0, 255);
        }
    }

    private static byte ExtractChannel(uint value, uint mask)
    {
        if (mask == 0)
            return 0;
        var shift = BitOperations.TrailingZeroCount(mask);
        var bits = BitOperations.PopCount(mask);
        var raw = (ulong)((value & mask) >> shift);
        var maximum = (1UL << bits) - 1;
        return (byte)((raw * 255 + maximum / 2) / maximum);
    }

    private static void WritePixel(byte[] target, int width, int x, int y, byte red, byte green, byte blue, byte alpha)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)(target.Length / 4 / width))
            return;
        var index = checked((y * width + x) * 4);
        target[index] = blue;
        target[index + 1] = green;
        target[index + 2] = red;
        target[index + 3] = alpha;
    }

    private static int GetLinearBytesPerPixel(PixelKind kind, int maskedBytes) => kind switch
    {
        PixelKind.Rgba8 or PixelKind.Bgra8 or PixelKind.Bgrx8 => 4,
        PixelKind.Rg8 => 2,
        PixelKind.R8 => 1,
        PixelKind.Masked => maskedBytes,
        _ => 0
    };

    private static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static uint FourCc(ReadOnlySpan<byte> text) =>
        (uint)text[0] | (uint)text[1] << 8 | (uint)text[2] << 16 | (uint)text[3] << 24;

    private static string FourCcText(uint fourCc) => new string(new[]
    {
        (char)(fourCc & 0xff), (char)((fourCc >> 8) & 0xff), (char)((fourCc >> 16) & 0xff), (char)((fourCc >> 24) & 0xff)
    });
}
