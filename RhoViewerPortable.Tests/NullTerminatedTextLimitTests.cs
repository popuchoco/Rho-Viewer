using KartLibrary.File;
using KartLibrary.IO;

namespace RhoViewerPortable.Tests;

internal static class NullTerminatedTextLimitTests
{
    public static void Run()
    {
        TestExactLimitIsAccepted();
        TestOverLimitNarrowAndWideStringsAreRejected();
    }

    private static void TestExactLimitIsAccepted()
    {
        var bytes = new byte[ArchiveReadLimits.MaxNullTerminatedTextLength + 1];
        Array.Fill(bytes, (byte)'a', 0, bytes.Length - 1);
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));
        var text = reader.ReadNullTerminatedText(wideString: false);
        Assert(text.Length == ArchiveReadLimits.MaxNullTerminatedTextLength,
            "Null-terminated text at the configured character limit was rejected.");
    }

    private static void TestOverLimitNarrowAndWideStringsAreRejected()
    {
        var narrowBytes = new byte[ArchiveReadLimits.MaxNullTerminatedTextLength + 1];
        Array.Fill(narrowBytes, (byte)'a');
        using (var reader = new BinaryReader(new MemoryStream(narrowBytes, writable: false)))
            ExpectInvalid(() => reader.ReadNullTerminatedText(wideString: false), "over-limit narrow text");

        var wideBytes = new byte[(ArchiveReadLimits.MaxNullTerminatedTextLength + 1) * sizeof(char)];
        for (var offset = 0; offset < wideBytes.Length; offset += sizeof(char))
            wideBytes[offset] = (byte)'a';
        using var wideReader = new BinaryReader(new MemoryStream(wideBytes, writable: false));
        ExpectInvalid(() => wideReader.ReadNullTerminatedText(wideString: true), "over-limit wide text");
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
