using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;

namespace RhoViewerPortable;

/// <summary>
/// Read-only reader for the Nexon KartRider LPK v4 layout (flags 6/7, no sections).
/// Keys are derived from the original package filename, so archives are opened in place.
/// </summary>
internal sealed class LpkArchive : IDisposable
{
    private const string Salt = "NhucnUGGZIHvxdga";
    private static readonly byte[] Mask = Convert.FromHexString(
        "7B2F3548439502B9AE91A6E1D8D624B433101D3DC1BBC6F4A5FEB3696B56E475");

    private readonly string _path;
    private readonly long _dataBegin;
    private readonly byte[] _nonce;
    private readonly LpkEntry[] _entries;

    private LpkArchive(string path, long dataBegin, byte[] nonce, LpkEntry[] entries)
    {
        _path = path;
        _dataBegin = dataBegin;
        _nonce = nonce;
        _entries = entries;
    }

    public IReadOnlyList<LpkEntry> Entries => _entries;

    public static LpkArchive Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < 64)
            throw new InvalidDataException("LPK 檔案太短，缺少 v4 標頭。");

        var fileName = Path.GetFileName(fullPath).ToLowerInvariant();
        var nameSum = 0;
        foreach (var character in fileName)
            nameSum = checked(nameSum + character);

        var headerOffset = (nameSum % 312) + 30;
        var headerBytes = new byte[checked(headerOffset + 5)];
        stream.Position = 0;
        stream.ReadExactly(headerBytes);

        var firstByte = unchecked((sbyte)headerBytes[0]);
        var signedShift = firstByte >> 1;
        var signatureMask = signedShift & 0xff;
        if ((headerBytes[headerOffset] ^ signatureMask) != 4)
            throw new InvalidDataException("不是受支援的 KartRider LPK v4 檔案。");

        // The original v4 header applies the sign-extended arithmetic shift to its
        // 32-bit length field; keep that behavior distinct from the byte signature mask.
        var rawStringLength = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            headerBytes.AsSpan(headerOffset + 1, 4)));
        var stringLength = rawStringLength ^ signedShift;
        if (stringLength is < 1 or > 1024)
            throw new InvalidDataException("LPK v4 標頭字串長度無效。");

        var headerEnd = checked((long)headerOffset + 5L + (stringLength * 2L));
        if (headerEnd + 8 > stream.Length)
            throw new InvalidDataException("LPK v4 標頭或加密檔案表遭截斷。");

        var text = fileName + Salt;
        var headerKey = DeriveHeaderKey(text);
        var headerReader = new LpkNativeReader(stream, headerEnd, headerKey);
        _ = headerReader.ReadUInt32();
        var count = headerReader.ReadUInt32();
        if (count is 0 or > 100_000)
            throw new InvalidDataException("LPK v4 檔案數量無效。");

        var tableStart = checked(headerEnd + 8L + ((nameSum * 3L) % 212L) + 64L);
        if (tableStart >= stream.Length)
            throw new InvalidDataException("LPK v4 檔案表位置超出封包範圍。");

        var tableReader = new LpkNativeReader(stream, tableStart, DeriveTableKey(text));
        var entries = new LpkEntry[checked((int)count)];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < entries.Length; index++)
        {
            var pathLength = tableReader.ReadUInt32();
            if (pathLength is 0 or > 4096)
                throw new InvalidDataException($"LPK v4 第 {index} 筆路徑長度無效。");

            var relativePath = Encoding.Unicode.GetString(tableReader.Read(checked((int)pathLength * 2)));
            if (!IsSafeRelativePath(relativePath))
                throw new InvalidDataException($"LPK v4 含有不安全的資源路徑：{relativePath}");
            if (!paths.Add(relativePath))
                throw new InvalidDataException($"LPK v4 含有重複的資源路徑：{relativePath}");

            var checksum = tableReader.ReadUInt32();
            var flags = tableReader.ReadUInt32();
            var blocks = tableReader.ReadUInt32();
            var size = tableReader.ReadUInt32();
            var stored = tableReader.ReadUInt32();
            _ = tableReader.ReadUInt32(); // Extra
            var sections = tableReader.ReadUInt32();
            var md5 = tableReader.Read(16);
            _ = tableReader.ReadUInt32(); // TagLow
            _ = tableReader.ReadUInt32(); // TagHigh

            var expectedChecksum = unchecked(flags + blocks + size + stored + md5.Aggregate(0u, (sum, value) => sum + value));
            if (checksum != expectedChecksum)
                throw new InvalidDataException($"LPK v4 資源表校驗失敗：{relativePath}");
            if (flags is not (6 or 7) || sections != 0)
                throw new NotSupportedException($"LPK v4 暫不支援此資源記錄（flags={flags}, sections={sections}）：{relativePath}");
            if (size > 256 * 1024 * 1024 || stored > int.MaxValue)
                throw new InvalidDataException($"LPK v4 資源大小超出支援範圍：{relativePath}");

            entries[index] = new LpkEntry(relativePath, flags, blocks, (int)size, (int)stored, md5);
        }

        // LpkToolkit v1.x's reader uses the next raw 64-byte buffer boundary, then
        // rounds it to the 1 KiB resource-data area. The table reader intentionally
        // preserves its boundary-counter semantics in LpkNativeReader.
        var dataBegin = AlignUp(tableReader.RawPosition, 1024);
        foreach (var entry in entries)
        {
            var offset = checked(dataBegin + (long)entry.Blocks * 1024L);
            if (offset > stream.Length || entry.StoredSize > stream.Length - offset)
                throw new InvalidDataException($"LPK v4 資源資料遭截斷：{entry.RelativePath}");
        }

        return new LpkArchive(fullPath, dataBegin, DeriveDataNonce(), entries);
    }

    public byte[] Read(LpkEntry entry)
    {
        var offset = checked(_dataBegin + (long)entry.Blocks * 1024L);
        var stored = new byte[entry.StoredSize];
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.Position = offset;
            stream.ReadExactly(stored);
        }

        var encryptedLength = Math.Min(stored.Length, 1024);
        if (encryptedLength != 0)
        {
            var key = DeriveFileKey(entry);
            var clear = Transform(stored.AsSpan(0, encryptedLength), key, _nonce);
            clear.CopyTo(stored, 0);
        }

        byte[] output;
        if ((entry.Flags & 1) != 0)
        {
            output = InflateExactly(stored, entry.Size, entry.RelativePath);
        }
        else
        {
            if (entry.Size > stored.Length)
                throw new InvalidDataException($"LPK v4 未壓縮資源長度無效：{entry.RelativePath}");
            output = stored.AsSpan(0, entry.Size).ToArray();
        }

        if (output.Length != entry.Size || !CryptographicOperations.FixedTimeEquals(MD5.HashData(output), entry.Md5))
            throw new InvalidDataException($"LPK v4 資源大小或 MD5 校驗失敗：{entry.RelativePath}");

        return output;
    }

    internal static byte[] InflateExactly(byte[] compressedData, int expectedSize, string resourceName)
    {
        if (expectedSize < 0 || expectedSize > 256 * 1024 * 1024)
            throw new InvalidDataException($"LPK v4 資源大小超出支援範圍：{resourceName}");

        var output = new byte[expectedSize];
        using var compressed = new MemoryStream(compressedData, writable: false);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var position = 0;
        while (position < output.Length)
        {
            var read = zlib.Read(output, position, output.Length - position);
            if (read == 0)
                throw new InvalidDataException($"LPK v4 resource ended before its declared size ({position}/{output.Length} bytes): {resourceName}");
            position += read;
        }

        if (zlib.ReadByte() != -1)
            throw new InvalidDataException($"LPK v4 resource expands beyond its declared size: {resourceName}");
        return output;
    }

    public void Dispose()
    {
        // Streams are opened only for parsing or individual resource reads.
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\\', ':', '\0']) >= 0 || path.StartsWith('/'))
            return false;
        return path.Split('/').All(part => part.Length != 0 && part is not ("." or ".."));
    }

    private static long AlignUp(long value, int alignment)
        => checked(((value + alignment - 1) / alignment) * alignment);

    private static byte[] DeriveHeaderKey(string text)
    {
        var key = new byte[32];
        for (var index = 0; index < key.Length; index++)
            key[index] = (byte)(((text[index % text.Length] + index) & 0xff) ^ Mask[index]);
        return key;
    }

    private static byte[] DeriveTableKey(string text)
    {
        var key = new byte[32];
        for (var index = 0; index < key.Length; index++)
        {
            var reverseIndex = text.Length - 1 - (index % text.Length);
            key[index] = (byte)(((text[reverseIndex] * (2 + (index % 3)) + index) & 0xff) ^ Mask[index]);
        }
        return key;
    }

    private static uint GetSaltHash()
    {
        var hash = 2166136261u;
        foreach (var character in Salt)
            hash = unchecked((hash ^ character) * 16777619u);
        return hash;
    }

    private static byte[] DeriveDataNonce()
    {
        var hash = GetSaltHash();
        Span<byte> seed = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(seed, hash);
        BinaryPrimitives.WriteUInt32LittleEndian(seed[4..], hash >> 1);
        BinaryPrimitives.WriteUInt32LittleEndian(seed[8..], (hash >> 1) ^ 108u);
        for (var index = 0; index < seed.Length; index++)
        {
            var mix = ((17 * index) ^ (8 * index)) + ((index >> 2) ^ (90 + 43 * index)) - (index % 11);
            seed[index] ^= (byte)mix;
        }

        var nonce = new byte[16];
        seed[8..12].CopyTo(nonce);
        seed[..8].CopyTo(nonce.AsSpan(8));
        return nonce;
    }

    private static byte[] DeriveFileKey(LpkEntry entry)
    {
        var digits = GetSaltHash().ToString(CultureInfo.InvariantCulture).Select(character => character - '0').ToArray();
        var key = new byte[32];
        for (var index = 0; index < key.Length; index++)
        {
            var value = (digits[(index + 1) % digits.Length] + index) % 5;
            value += entry.Md5[(digits[(index + 2) % digits.Length] + index) & 15];
            value += digits[index % digits.Length] & 1;
            var pathCharacter = entry.RelativePath[index % entry.RelativePath.Length];
            key[index] = (byte)((((value * (byte)pathCharacter) + index) & 0xff) ^ Mask[index]);
        }
        return key;
    }

    private static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != 32 || nonce.Length != 16)
            throw new ArgumentException("LPK v4 ChaCha key/nonce length is invalid.");

        var output = new byte[input.Length];
        var counter = BinaryPrimitives.ReadUInt64LittleEndian(nonce);
        var position = 0;
        Span<uint> initial = stackalloc uint[16];
        Span<uint> state = stackalloc uint[16];
        Span<byte> keyStream = stackalloc byte[64];
        while (position < input.Length)
        {
            initial[0] = 0x61707865;
            initial[1] = 0x3320646e;
            initial[2] = 0x79622d32;
            initial[3] = 0x6b206574;
            for (var index = 0; index < 8; index++)
                initial[4 + index] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(index * 4, 4));
            initial[12] = (uint)counter;
            initial[13] = (uint)(counter >> 32);
            initial[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[8..]);
            initial[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[12..]);

            initial.CopyTo(state);
            for (var round = 0; round < 10; round++)
            {
                QuarterRound(state, 0, 4, 8, 12);
                QuarterRound(state, 1, 5, 9, 13);
                QuarterRound(state, 2, 6, 10, 14);
                QuarterRound(state, 3, 7, 11, 15);
                QuarterRound(state, 0, 5, 10, 15);
                QuarterRound(state, 1, 6, 11, 12);
                QuarterRound(state, 2, 7, 8, 13);
                QuarterRound(state, 3, 4, 9, 14);
            }

            for (var index = 0; index < state.Length; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(keyStream.Slice(index * 4, 4), unchecked(state[index] + initial[index]));

            var take = Math.Min(64, input.Length - position);
            for (var index = 0; index < take; index++)
                output[position + index] = (byte)(input[position + index] ^ keyStream[index]);
            position += take;
            counter = unchecked(counter + 1);
        }
        return output;
    }

    private static void QuarterRound(Span<uint> state, int a, int b, int c, int d)
    {
        state[a] = unchecked(state[a] + state[b]);
        state[d] = RotateLeft(state[d] ^ state[a], 16);
        state[c] = unchecked(state[c] + state[d]);
        state[b] = RotateLeft(state[b] ^ state[c], 12);
        state[a] = unchecked(state[a] + state[b]);
        state[d] = RotateLeft(state[d] ^ state[a], 8);
        state[c] = unchecked(state[c] + state[d]);
        state[b] = RotateLeft(state[b] ^ state[c], 7);
    }

    private static uint RotateLeft(uint value, int count)
        => (value << count) | (value >> (32 - count));

    internal sealed record LpkEntry(string RelativePath, uint Flags, uint Blocks, int Size, int StoredSize, byte[] Md5);

    private sealed class LpkNativeReader
    {
        private readonly Stream _stream;
        private readonly byte[] _key;
        private byte[] _buffer = Array.Empty<byte>();
        private int _position;
        private ulong _counter;

        public LpkNativeReader(Stream stream, long start, byte[] key)
        {
            _stream = stream;
            _stream.Position = start;
            RawPosition = start;
            _key = key;
        }

        public long RawPosition { get; private set; }

        public uint ReadUInt32()
            => BinaryPrimitives.ReadUInt32LittleEndian(Read(4));

        public byte[] Read(int length)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));
            using var result = new MemoryStream(length);
            var remaining = length;
            while (remaining > 0)
            {
                if (_position == _buffer.Length)
                {
                    var blockCount = checked((remaining + 63) / 64);
                    var bufferLength = checked(blockCount * 64);
                    if (RawPosition > _stream.Length || bufferLength > _stream.Length - RawPosition)
                        throw new EndOfStreamException("LPK v4 加密檔案表遭截斷。");

                    var encrypted = new byte[bufferLength];
                    _stream.Position = RawPosition;
                    _stream.ReadExactly(encrypted);
                    var nonce = new byte[16];
                    BinaryPrimitives.WriteUInt64LittleEndian(nonce, _counter);
                    _buffer = Transform(encrypted, _key, nonce);
                    _counter = checked(_counter + (ulong)blockCount);
                    RawPosition = checked(RawPosition + bufferLength);
                    _position = 0;
                }

                var take = Math.Min(remaining, _buffer.Length - _position);
                result.Write(_buffer, _position, take);
                _position += take;
                remaining -= take;
                if (_position == _buffer.Length && remaining == 0)
                    _counter = 0;
            }
            return result.ToArray();
        }
    }
}
