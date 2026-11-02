using System.Buffers.Binary;
using System.Text;
using KartLibrary.Record;

namespace RhoViewerPortable;

internal static class ResourceAnalyzers
{
    internal const int MaxAnalysisBytes = 64 * 1024 * 1024;
    private const int MaxOggHeaderPacketBytes = 2 * 1024 * 1024;
    private const int MaxOggHeaderPackets = 4;
    private const int MaxFlacMetadataBlocks = 4096;
    private const int MaxCommentEntries = 10_000;

    public static string Analyze(RhoItem item, byte[] data)
    {
        return AnalyzeExtension(item.Extension, data);
    }

    internal static string AnalyzeExtension(string extension, byte[] data)
    {
        var normalizedExtension = extension.ToLowerInvariant();
        if ((normalizedExtension is ".ksv" or ".ogg" or ".flac" or ".1s") && data.Length > MaxAnalysisBytes)
            return $"資源過大，略過內容分析（上限 {MaxAnalysisBytes / (1024 * 1024)} MiB；目前 {data.Length / (1024 * 1024)} MiB）。可匯出原始資源後使用外部工具。";

        return normalizedExtension switch
        {
            ".ksv" => AnalyzeKsv(data),
            ".ogg" => AnalyzeOgg(data),
            ".flac" => AnalyzeFlac(data),
            ".1s" => OneSAnalyzer.Analyze(data),
            _ => ""
        };
    }

    private static string AnalyzeKsv(byte[] data)
    {
        try
        {
            var record = KartRecord.ReadKSVFromBytes(data);
            var builder = new StringBuilder();
            builder.AppendLine("KSV 結構解析成功");
            builder.AppendLine($"Record header version：{record.RecordHeaderVersion}");
            builder.AppendLine($"Record version：{record.RecordVersion}");
            builder.AppendLine($"標題：{record.RecordTitle}");
            builder.AppendLine($"賽事類型：{record.ContestType}");
            builder.AppendLine($"區域：{record.RegionCode}");
            builder.AppendLine($"賽道：{record.TrackName}");
            builder.AppendLine($"錄影者：{record.RecorderName} ({record.RecorderAccount})");
            builder.AppendLine($"錄製時間：{record.RecordingDate:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"最佳時間：{record.BestTime}");
            builder.AppendLine($"玩家數：{record.Players.Length}");
            for (var i = 0; i < record.Players.Length; i++)
            {
                var player = record.Players[i];
                builder.AppendLine($"  {i + 1}. {player.PlayerName}（車輛 ID {player.Equipment.Kart}）");
            }
            builder.AppendLine($"錄影資料段：{record.Records.Length}");
            builder.AppendLine("\n此摘要依照 KartRider-File-Reader 的 KSV 結構定義解碼；若版本識別碼不在已知版本表，會保留原始檔案供匯出。");
            return builder.ToString();
        }
        catch (Exception ex)
        {
            return $"KSV 外層可讀取，但結構解析失敗。\n原因：{ex.Message}\n\n建議先匯出原始 .ksv，再依其遊戲版本補充格式定義。";
        }
    }

    private static string AnalyzeOgg(byte[] data)
    {
        if (data.Length < 27 || !data.AsSpan(0, 4).SequenceEqual("OggS"u8))
            return "不是標準 OggS 容器，可能是自訂音訊封裝或資源仍需其他轉換。";

        var packets = new List<byte[]>();
        var packet = new List<byte>();
        var packetTooLarge = false;
        var anyPacketTooLarge = false;
        var malformed = false;
        long lastGranule = -1;
        var pageCount = 0;
        var pos = 0;
        uint streamSerial = 0;
        while (pos <= data.Length - 27)
        {
            if (!data.AsSpan(pos, 4).SequenceEqual("OggS"u8) || data[pos + 4] != 0)
            {
                malformed = true;
                break;
            }

            var segmentCount = data[pos + 26];
            var remainingAfterHeader = data.Length - pos - 27;
            if (segmentCount > remainingAfterHeader)
            {
                malformed = true;
                break;
            }

            var granule = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos + 6, 8));
            if (granule >= 0)
                lastGranule = Math.Max(lastGranule, granule);
            if (pageCount == 0)
                streamSerial = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 14, 4));

            var segmentTable = data.AsSpan(pos + 27, segmentCount);
            var payloadStart = pos + 27 + segmentCount;
            var payloadLength = 0;
            foreach (var segmentLength in segmentTable)
            {
                if (segmentLength > data.Length - payloadStart - payloadLength)
                {
                    malformed = true;
                    break;
                }
                payloadLength += segmentLength;
            }
            if (malformed)
                break;

            var payloadPos = payloadStart;
            foreach (var segmentLength in segmentTable)
            {
                if (packets.Count < MaxOggHeaderPackets && !packetTooLarge)
                {
                    if (segmentLength > MaxOggHeaderPacketBytes - packet.Count)
                    {
                        packet.Clear();
                        packetTooLarge = true;
                        anyPacketTooLarge = true;
                    }
                    else
                    {
                        for (var i = 0; i < segmentLength; i++)
                            packet.Add(data[payloadPos + i]);
                    }
                }
                payloadPos += segmentLength;
                if (segmentLength < 255)
                {
                    if (packets.Count < MaxOggHeaderPackets && !packetTooLarge)
                        packets.Add(packet.ToArray());
                    packet.Clear();
                    packetTooLarge = false;
                }
            }

            pageCount++;
            pos = payloadStart + payloadLength;
        }
        if (pageCount > 0 && pos != data.Length)
            malformed = true;
        if (packet.Count > 0 && packets.Count < MaxOggHeaderPackets && !packetTooLarge)
            packets.Add(packet.ToArray());

        var builder = new StringBuilder();
        builder.AppendLine("Ogg 容器分析");
        builder.AppendLine($"串流序號：{streamSerial}");
        builder.AppendLine($"頁數：{pageCount}");
        builder.AppendLine($"已組合封包：{packets.Count}");
        if (malformed)
            builder.AppendLine("警告：遇到截斷或無效的 Ogg 頁面；僅顯示已完整讀取的標頭資訊。");
        if (anyPacketTooLarge)
            builder.AppendLine("警告：有 Ogg 頁面封包超過標頭分析上限，已略過該封包。");
        if (packets.Count > 0 && packets[0].Length >= 30 && packets[0][0] == 1 && packets[0].AsSpan(1, 6).SequenceEqual("vorbis"u8))
        {
            var first = packets[0];
            var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(12, 4));
            var channels = first[11];
            builder.AppendLine("編碼：Vorbis");
            builder.AppendLine($"聲道：{channels}");
            builder.AppendLine($"取樣率：{sampleRate:N0} Hz");
            var maxBitrate = BinaryPrimitives.ReadInt32LittleEndian(first.AsSpan(16, 4));
            var nominalBitrate = BinaryPrimitives.ReadInt32LittleEndian(first.AsSpan(20, 4));
            var minBitrate = BinaryPrimitives.ReadInt32LittleEndian(first.AsSpan(24, 4));
            builder.AppendLine($"位元率：{FormatBitrate(nominalBitrate)}（峰值 {FormatBitrate(maxBitrate)}，最低 {FormatBitrate(minBitrate)}）");
            builder.AppendLine($"Block size：{1 << (first[28] & 0x0F)}–{1 << ((first[28] >> 4) & 0x0F)} samples");
            builder.AppendLine($"Framing flag：{(first[29] == 1 ? "有效" : "異常")}");
            if (lastGranule >= 0 && sampleRate > 0)
                builder.AppendLine($"估算長度：{FormatDuration(lastGranule / (double)sampleRate)}");
            AppendComments(builder, packets, 1, "Vorbis comments");
        }
        else if (packets.Count > 0 && packets[0].Length >= 19 && packets[0].AsSpan(0, 8).SequenceEqual("OpusHead"u8))
        {
            var first = packets[0];
            var channels = first[9];
            var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(12, 4));
            var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(10, 2));
            var outputGain = BinaryPrimitives.ReadInt16LittleEndian(first.AsSpan(16, 2));
            var mappingFamily = first[18];
            builder.AppendLine("編碼：Opus");
            builder.AppendLine($"Opus 版本：{first[8]}");
            builder.AppendLine($"聲道：{channels}");
            builder.AppendLine($"輸入取樣率：{sampleRate:N0} Hz");
            builder.AppendLine($"Pre-skip：{preSkip} samples");
            builder.AppendLine($"Output gain：{outputGain}（Q8 dB）");
            builder.AppendLine($"Channel mapping family：{mappingFamily}");
            if (lastGranule >= 0)
                builder.AppendLine($"估算長度：{FormatDuration(Math.Max(0, lastGranule - preSkip) / 48000d)}");
            AppendComments(builder, packets, 1, "Opus tags");
        }
        else
        {
            builder.AppendLine("編碼：尚未辨識（可能是其他 Ogg codec）");
        }
        return builder.ToString();
    }

    private static void AppendComments(StringBuilder builder, IReadOnlyList<byte[]> packets, int startIndex, string title)
    {
        foreach (var packet in packets.Skip(startIndex).Take(3))
        {
            var offset = packet.AsSpan().IndexOf("OpusTags"u8);
            if (offset < 0)
                offset = packet.AsSpan().IndexOf("vorbis"u8);
            if (offset < 0)
                continue;

            offset += packet.AsSpan(offset).StartsWith("OpusTags"u8) ? 8 : 7;
            if (offset + 4 > packet.Length)
                continue;
            var vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset, 4));
            offset += 4;
            if (vendorLength > packet.Length - offset)
                continue;
            offset += (int)vendorLength;
            if (offset + 4 > packet.Length)
                continue;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset, 4));
            offset += 4;
            if (count > MaxCommentEntries || count > (uint)((packet.Length - offset) / 4))
            {
                builder.AppendLine($"警告：{title} 數量或長度欄位無效。");
                return;
            }
            builder.AppendLine($"{title}：{count} 筆");
            var valid = true;
            for (uint i = 0; i < count; i++)
            {
                if (offset + 4 > packet.Length)
                {
                    valid = false;
                    break;
                }
                var length = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset, 4));
                offset += 4;
                if (length > packet.Length - offset)
                {
                    valid = false;
                    break;
                }
                if (i < 12)
                    builder.AppendLine($"  {Encoding.UTF8.GetString(packet, offset, (int)length)}");
                offset += (int)length;
            }
            if (!valid)
                builder.AppendLine($"警告：{title} 內容長度欄位無效。");
            return;
        }
    }

    private static string AnalyzeFlac(byte[] data)
    {
        if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual("fLaC"u8))
            return "不是標準 fLaC 串流，可能是自訂音訊封裝或資源仍需其他轉換。";

        var builder = new StringBuilder("FLAC metadata 分析\n");
        var pos = 4;
        var blockCount = 0;
        var commentCount = 0;
        var streamInfoFound = false;
        var seekTablePoints = 0;
        var pictureCount = 0;
        var paddingBytes = 0;
        var malformed = false;
        var metadataEnded = false;
        while (pos <= data.Length - 4 && blockCount < MaxFlacMetadataBlocks)
        {
            var header = data[pos];
            var isLast = (header & 0x80) != 0;
            var type = header & 0x7F;
            var length = (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            if (length > data.Length - pos)
            {
                malformed = true;
                break;
            }
            blockCount++;
            if (type == 0)
            {
                if (blockCount != 1 || length != 34)
                {
                    malformed = true;
                    break;
                }
                var block = data.AsSpan(pos, length);
                var minBlockSize = BinaryPrimitives.ReadUInt16BigEndian(block[..2]);
                var maxBlockSize = BinaryPrimitives.ReadUInt16BigEndian(block.Slice(2, 2));
                var sampleRate = (uint)((block[10] << 12) | (block[11] << 4) | (block[12] >> 4));
                var channels = ((block[12] & 0x0E) >> 1) + 1;
                var bits = ((block[12] & 0x01) << 4) | (block[13] >> 4);
                bits++;
                var totalSamples = ((ulong)(block[13] & 0x0F) << 32) |
                                   ((ulong)block[14] << 24) |
                                   ((ulong)block[15] << 16) |
                                   ((ulong)block[16] << 8) |
                                   block[17];
                var md5 = Convert.ToHexString(block.Slice(18, 16)).ToLowerInvariant();
                streamInfoFound = true;
                builder.AppendLine($"取樣率：{sampleRate:N0} Hz");
                builder.AppendLine($"聲道：{channels}");
                builder.AppendLine($"位元深度：{bits} bit");
                builder.AppendLine($"Block size：{minBlockSize:N0}–{maxBlockSize:N0}");
                if (sampleRate > 0)
                    builder.AppendLine($"總 samples：{totalSamples:N0}，長度：{TimeSpan.FromSeconds(totalSamples / (double)sampleRate):hh\\:mm\\:ss\\.fff}");
                builder.AppendLine($"MD5：{md5}");
            }
            else if (type == 1)
            {
                paddingBytes += length;
            }
            else if (type == 3)
            {
                seekTablePoints += length / 18;
            }
            else if (type == 6)
            {
                pictureCount++;
            }
            else if (type == 4)
            {
                var parsedComments = AppendFlacComments(builder, data.AsSpan(pos, length));
                if (parsedComments < 0)
                    malformed = true;
                else
                    commentCount += parsedComments;
            }
            pos += length;
            if (isLast)
            {
                metadataEnded = true;
                break;
            }
            if (blockCount == MaxFlacMetadataBlocks)
            {
                malformed = true;
                break;
            }
        }
        if (!metadataEnded)
            malformed = true;
        if (!streamInfoFound)
            builder.AppendLine("警告：找不到有效 STREAMINFO metadata。");
        if (malformed)
            builder.AppendLine("警告：FLAC metadata 區塊順序、長度或數量無效；已停止解析。");
        builder.AppendLine($"metadata blocks：{blockCount}");
        builder.AppendLine($"Seek points：{seekTablePoints:N0}");
        builder.AppendLine($"Picture blocks：{pictureCount:N0}");
        builder.AppendLine($"Padding：{paddingBytes:N0} bytes");
        if (commentCount > 0)
            builder.AppendLine($"Vorbis comments：{commentCount} 筆");
        return builder.ToString();
    }

    private static string FormatBitrate(int bitsPerSecond)
    {
        return bitsPerSecond <= 0 ? "未設定" : $"{bitsPerSecond / 1000.0:N0} kbps";
    }

    private static string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            return "超出可表示範圍";
        return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff");
    }

    private static int AppendFlacComments(StringBuilder builder, ReadOnlySpan<byte> block)
    {
        if (block.Length < 8)
            return -1;
        var pos = 0;
        var vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(block[pos..]);
        pos += 4;
        if (vendorLength > block.Length - pos)
            return -1;
        pos += (int)vendorLength;
        if (pos + 4 > block.Length)
            return -1;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(block[pos..]);
        pos += 4;
        if (count > MaxCommentEntries || count > (uint)((block.Length - pos) / 4))
            return -1;
        for (uint i = 0; i < count; i++)
        {
            if (pos + 4 > block.Length)
                return -1;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(block[pos..]);
            pos += 4;
            if (length > block.Length - pos)
                return -1;
            if (i < 12)
                builder.AppendLine($"  {Encoding.UTF8.GetString(block.Slice(pos, (int)length))}");
            pos += (int)length;
        }
        return (int)Math.Min(count, 12);
    }
}
