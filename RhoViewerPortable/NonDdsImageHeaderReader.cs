using System.Buffers.Binary;

namespace RhoViewerPortable;

internal readonly record struct ImageDimensions(int Width, int Height);

/// <summary>
/// Reads only enough structure from common raster image headers to enforce the
/// decoded image limits before handing the bytes to the platform decoder.
/// </summary>
internal static class NonDdsImageHeaderReader
{
    public static ImageDimensions ReadDimensions(byte[] data, string extension)
    {
        ArgumentNullException.ThrowIfNull(data);

        var dimensions = extension.ToLowerInvariant() switch
        {
            ".png" => ReadPng(data),
            ".jpg" or ".jpeg" => ReadJpeg(data),
            ".bmp" => ReadBmp(data),
            ".gif" => ReadGif(data),
            _ => throw new InvalidDataException("不支援此圖片格式。")
        };

        ImageDecodeLimits.ValidateDimensions(dimensions.Width, dimensions.Height, extension.TrimStart('.').ToUpperInvariant());
        return dimensions;
    }

    private static ImageDimensions ReadPng(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 33 || !data[..8].SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(data[8..12]) != 13 ||
            !data[12..16].SequenceEqual("IHDR"u8))
            throw InvalidHeader("PNG");

        var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(data[29..33]);
        if (ComputePngCrc(data[12..29]) != expectedCrc)
            throw InvalidHeader("PNG");

        var width = BinaryPrimitives.ReadUInt32BigEndian(data[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[20..24]);
        var bitDepth = data[24];
        var colorType = data[25];
        if (!IsValidPngBitDepth(colorType, bitDepth) || data[26] != 0 || data[27] != 0 || data[28] > 1)
            throw InvalidHeader("PNG");

        return new ImageDimensions(ToDimension(width, "PNG"), ToDimension(height, "PNG"));
    }

    private static ImageDimensions ReadJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
            throw InvalidHeader("JPEG");

        var index = 2;
        while (index < data.Length)
        {
            if (data[index++] != 0xff)
                throw InvalidHeader("JPEG");

            while (index < data.Length && data[index] == 0xff)
                index++;
            if (index >= data.Length)
                throw InvalidHeader("JPEG");

            var marker = data[index++];
            if (marker == 0x00)
                throw InvalidHeader("JPEG");
            if (marker is 0xd9 or 0xda)
                throw InvalidHeader("JPEG");
            if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7)
                continue;

            if (index > data.Length - 2)
                throw InvalidHeader("JPEG");
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[index..]);
            if (segmentLength < 2 || segmentLength > data.Length - index)
                throw InvalidHeader("JPEG");

            if (IsJpegStartOfFrame(marker))
            {
                if (segmentLength < 8)
                    throw InvalidHeader("JPEG");

                var payload = data.Slice(index + 2, segmentLength - 2);
                var precision = payload[0];
                var height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..3]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..5]);
                var componentCount = payload[5];
                if (precision == 0 || precision > 16 || componentCount == 0 ||
                    segmentLength < 8 + 3 * componentCount)
                    throw InvalidHeader("JPEG");

                return new ImageDimensions(width, height);
            }

            index += segmentLength;
        }

        throw InvalidHeader("JPEG");
    }

    private static ImageDimensions ReadBmp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 26 || data[0] != (byte)'B' || data[1] != (byte)'M')
            throw InvalidHeader("BMP");

        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[10..14]);
        var dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(data[14..18]);
        long width;
        long height;
        int headerEnd;

        if (dibHeaderSize == 12)
        {
            headerEnd = 26;
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[18..20]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[20..22]);
            if (BinaryPrimitives.ReadUInt16LittleEndian(data[22..24]) != 1)
                throw InvalidHeader("BMP");
            var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data[24..26]);
            if (!IsValidBmpBitDepth(bitsPerPixel))
                throw InvalidHeader("BMP");
        }
        else
        {
            if (dibHeaderSize is not (40 or 52 or 56 or 64 or 108 or 124) ||
                dibHeaderSize > data.Length - 14)
                throw InvalidHeader("BMP");

            headerEnd = checked(14 + (int)dibHeaderSize);
            var signedWidth = BinaryPrimitives.ReadInt32LittleEndian(data[18..22]);
            var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data[22..26]);
            width = signedWidth;
            height = signedHeight == int.MinValue ? long.MaxValue : Math.Abs((long)signedHeight);
            if (BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]) != 1 || signedHeight == 0)
                throw InvalidHeader("BMP");

            var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data[28..30]);
            if (!IsValidBmpBitDepth(bitsPerPixel))
                throw InvalidHeader("BMP");

            var compression = BinaryPrimitives.ReadUInt32LittleEndian(data[30..34]);
            if (compression is 4 or 5) // BI_JPEG / BI_PNG can contain a second, independent image header.
                throw new NotSupportedException("不支援包含內嵌 JPEG 或 PNG 資料的 BMP 圖片。");
        }

        if (pixelOffset < headerEnd || pixelOffset > data.Length)
            throw InvalidHeader("BMP");

        return new ImageDimensions(ToDimension(width, "BMP"), ToDimension(height, "BMP"));
    }

    private static ImageDimensions ReadGif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 13 ||
            !(data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
            throw InvalidHeader("GIF");

        var width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..10]);
        var packedFields = data[10];

        var canvasWidth = ToDimension(width, "GIF");
        var canvasHeight = ToDimension(height, "GIF");
        ImageDecodeLimits.ValidateDimensions(canvasWidth, canvasHeight, "GIF");

        var index = 13;
        if ((packedFields & 0x80) != 0)
        {
            var colorTableBytes = 3 * (1 << ((packedFields & 0x07) + 1));
            if (colorTableBytes > data.Length - index)
                throw InvalidHeader("GIF");
            index += colorTableBytes;
        }

        var hasFrame = false;
        while (index < data.Length)
        {
            var blockType = data[index++];
            if (blockType == 0x3b) // Trailer
            {
                if (!hasFrame)
                    throw InvalidHeader("GIF");
                return new ImageDimensions(canvasWidth, canvasHeight);
            }

            if (blockType == 0x21) // Extension
            {
                if (index >= data.Length)
                    throw InvalidHeader("GIF");
                index++; // Extension label
                SkipGifSubBlocks(data, ref index);
                continue;
            }

            if (blockType != 0x2c || data.Length - index < 9) // Image descriptor
                throw InvalidHeader("GIF");

            var frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(data[(index + 4)..(index + 6)]);
            var frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(data[(index + 6)..(index + 8)]);
            var framePackedFields = data[index + 8];
            ImageDecodeLimits.ValidateDimensions(frameWidth, frameHeight, "GIF frame");
            if ((framePackedFields & 0x18) != 0)
                throw InvalidHeader("GIF");
            index += 9;

            if ((framePackedFields & 0x80) != 0)
            {
                var colorTableBytes = 3 * (1 << ((framePackedFields & 0x07) + 1));
                if (colorTableBytes > data.Length - index)
                    throw InvalidHeader("GIF");
                index += colorTableBytes;
            }

            if (index >= data.Length || data[index] is < 2 or > 8)
                throw InvalidHeader("GIF");
            index++; // LZW minimum code size
            SkipGifSubBlocks(data, ref index);
            hasFrame = true;
        }

        throw InvalidHeader("GIF");
    }

    private static void SkipGifSubBlocks(ReadOnlySpan<byte> data, ref int index)
    {
        while (index < data.Length)
        {
            var blockLength = data[index++];
            if (blockLength == 0)
                return;
            if (blockLength > data.Length - index)
                throw InvalidHeader("GIF");
            index += blockLength;
        }

        throw InvalidHeader("GIF");
    }

    private static bool IsValidPngBitDepth(byte colorType, byte bitDepth) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        4 => bitDepth is 8 or 16,
        6 => bitDepth is 8 or 16,
        _ => false
    };

    private static bool IsValidBmpBitDepth(ushort bitsPerPixel) => bitsPerPixel is 1 or 2 or 4 or 8 or 16 or 24 or 32;

    private static bool IsJpegStartOfFrame(byte marker) => marker is
        0xc0 or 0xc1 or 0xc2 or 0xc3 or
        0xc5 or 0xc6 or 0xc7 or
        0xc9 or 0xca or 0xcb or
        0xcd or 0xce or 0xcf;

    private static int ToDimension(long value, string format)
    {
        if (value <= 0 || value > int.MaxValue)
            throw InvalidHeader(format);
        return (int)value;
    }

    private static int ToDimension(uint value, string format)
    {
        if (value == 0 || value > int.MaxValue)
            throw InvalidHeader(format);
        return (int)value;
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
        }
        return ~crc;
    }

    private static InvalidDataException InvalidHeader(string format) =>
        new($"{format} 圖片標頭無效或遭截斷。");
}
