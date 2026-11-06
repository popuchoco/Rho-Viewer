using System.Buffers.Binary;
using System.Text;
using RhoViewerPortable;

namespace RhoViewerPortable.Tests;

internal static class NonDdsImageHeaderTests
{
    private static readonly string[] Formats =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".gif"
    ];

    public static void Run()
    {
        TestTinyHeadersAndDecodedCaps();
        TestOversizedDimensions();
        TestMalformedAndTruncatedHeaders();
    }

    private static void TestTinyHeadersAndDecodedCaps()
    {
        foreach (var extension in Formats)
        {
            var dimensions = NonDdsImageHeaderReader.ReadDimensions(BuildHeader(extension, 1, 1), extension);
            Assert(dimensions == new ImageDimensions(1, 1), $"{extension} 1 × 1 header dimensions were not read.");

            dimensions = NonDdsImageHeaderReader.ReadDimensions(BuildHeader(extension, 8192, 4096), extension);
            Assert(dimensions == new ImageDimensions(8192, 4096), $"{extension} at the decoded-byte limit was rejected.");
            ExpectInvalid(
                () => NonDdsImageHeaderReader.ReadDimensions(BuildHeader(extension, 8192, 4097), extension),
                $"{extension} decoded-byte cap");

            dimensions = NonDdsImageHeaderReader.ReadDimensions(BuildHeader(extension, ImageDecodeLimits.MaxDimension, 1), extension);
            Assert(dimensions.Width == ImageDecodeLimits.MaxDimension, $"{extension} maximum side length was rejected.");
        }
    }

    private static void TestOversizedDimensions()
    {
        foreach (var extension in Formats)
        {
            ExpectInvalid(
                () => NonDdsImageHeaderReader.ReadDimensions(BuildHeader(extension, ImageDecodeLimits.MaxDimension + 1, 1), extension),
                $"{extension} oversized side length");
        }

        var oversizedGifFrame = BuildGif(1, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(oversizedGifFrame.AsSpan(18), ImageDecodeLimits.MaxDimension + 1);
        ExpectInvalid(() => NonDdsImageHeaderReader.ReadDimensions(oversizedGifFrame, ".gif"), "oversized GIF frame dimensions");

        var oversizedGifFrameArea = BuildGif(1, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(oversizedGifFrameArea.AsSpan(18), 8192);
        BinaryPrimitives.WriteUInt16LittleEndian(oversizedGifFrameArea.AsSpan(20), 4097);
        ExpectInvalid(() => NonDdsImageHeaderReader.ReadDimensions(oversizedGifFrameArea, ".gif"), "oversized GIF frame decoded bytes");
    }

    private static void TestMalformedAndTruncatedHeaders()
    {
        ExpectInvalid(() => NonDdsImageHeaderReader.ReadDimensions(new byte[32], ".png"), "truncated PNG IHDR");
        var badPngCrc = BuildPng(1, 1);
        badPngCrc[32] ^= 0x01;
        ExpectInvalid(() => NonDdsImageHeaderReader.ReadDimensions(badPngCrc, ".png"), "invalid PNG IHDR checksum");
        ExpectInvalid(() => NonDdsImageHeaderReader.ReadDimensions(new byte[] { 0xff, 0xd8, 0xff, 0xc0, 0, 2 }, ".jpg"), "truncated JPEG frame header");
        ExpectInvalid(() => NonDdsImageHeaderReader.ReadDimensions(new byte[25], ".bmp"), "truncated BMP header");
        ExpectInvalid(() => NonDdsImageHeaderReader.ReadDimensions(Encoding.ASCII.GetBytes("GIF89a\0\0\0\0\0\0"), ".gif"), "truncated GIF logical screen descriptor");
        var gifWithColorResolution = BuildGif(1, 1);
        gifWithColorResolution[10] = 0x70;
        Assert(NonDdsImageHeaderReader.ReadDimensions(gifWithColorResolution, ".gif") == new ImageDimensions(1, 1),
            "GIF logical-screen color resolution bits were rejected.");
        var truncatedGifPalette = BuildGif(1, 1);
        Array.Resize(ref truncatedGifPalette, 15);
        truncatedGifPalette[10] = 0x80;
        ExpectInvalid(() => NonDdsImageHeaderReader.ReadDimensions(truncatedGifPalette, ".gif"), "truncated GIF color table");
        ExpectInvalid(() => NonDdsImageHeaderReader.ReadDimensions(BuildHeader(".gif", 1, 1), ".png"), "extension and image signature mismatch");
    }

    private static byte[] BuildHeader(string extension, int width, int height) => extension switch
    {
        ".png" => BuildPng(width, height),
        ".jpg" or ".jpeg" => BuildJpeg(width, height),
        ".bmp" => BuildBmp(width, height),
        ".gif" => BuildGif(width, height),
        _ => throw new ArgumentOutOfRangeException(nameof(extension))
    };

    private static byte[] BuildPng(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        bytes[24] = 8;
        bytes[25] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29), PngCrc(bytes.AsSpan(12, 17)));
        return bytes;
    }

    private static byte[] BuildJpeg(int width, int height)
    {
        var bytes = new byte[15];
        bytes[0] = 0xff;
        bytes[1] = 0xd8;
        bytes[2] = 0xff;
        bytes[3] = 0xc0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 11);
        bytes[6] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(7), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(9), (ushort)width);
        bytes[11] = 1;
        bytes[12] = 1;
        bytes[13] = 0x11;
        bytes[14] = 0;
        return bytes;
    }

    private static byte[] BuildBmp(int width, int height)
    {
        var bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        return bytes;
    }

    private static byte[] BuildGif(int width, int height)
    {
        var bytes = new byte[28];
        "GIF89a"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)height);
        bytes[13] = 0x2c;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)height);
        bytes[23] = 2; // LZW minimum code size
        bytes[24] = 1; // one image data byte
        bytes[26] = 0; // image data terminator
        bytes[27] = 0x3b; // GIF trailer
        return bytes;
    }

    private static uint PngCrc(ReadOnlySpan<byte> data)
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

    private static void ExpectInvalid(Action action, string label)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidDataException($"Expected invalid-data rejection for {label}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
