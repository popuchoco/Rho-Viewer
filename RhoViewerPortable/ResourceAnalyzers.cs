using System.Buffers.Binary;
using System.Text;
using KartLibrary.Record;

namespace RhoViewerPortable;

internal static class ResourceAnalyzers
{
    public static string Analyze(RhoItem item, byte[] data)
    {
        return AnalyzeExtension(item.Extension, data);
    }

    internal static string AnalyzeExtension(string extension, byte[] data)
    {
        return extension.ToLowerInvariant() switch
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
        long lastGranule = -1;
        var pageCount = 0;
        var pos = 0;
        while (pos + 27 <= data.Length && data.AsSpan(pos, 4).SequenceEqual("OggS"u8))
        {
            var segmentCount = data[pos + 26];
            if (pos + 27 + segmentCount > data.Length)
                break;
            var granule = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos + 6, 8));
            if (granule >= 0)
                lastGranule = Math.Max(lastGranule, granule);
            var segmentTable = data.AsSpan(pos + 27, segmentCount);
            var payloadStart = pos + 27 + segmentCount;
            var payloadLength = segmentTable.ToArray().Sum(value => value);
            if (payloadStart + payloadLength > data.Length)
                break;

            var payloadPos = payloadStart;
            foreach (var segmentLength in segmentTable)
            {
                packet.AddRange(data.AsSpan(payloadPos, segmentLength).ToArray());
                payloadPos += segmentLength;
                if (segmentLength < 255)
                {
                    packets.Add(packet.ToArray());
                    packet.Clear();
                }
            }

            pageCount++;
            pos = payloadStart + payloadLength;
        }
        if (packet.Count > 0)
            packets.Add(packet.ToArray());

        var builder = new StringBuilder();
        builder.AppendLine("Ogg 容器分析");
        builder.AppendLine($"串流序號：{(pageCount > 0 && data.Length >= 18 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14, 4)) : 0)}");
        builder.AppendLine($"頁數：{pageCount}");
        builder.AppendLine($"已組合封包：{packets.Count}");
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
                builder.AppendLine($"估算長度：{TimeSpan.FromSeconds(lastGranule / (double)sampleRate):hh\\:mm\\:ss\\.fff}");
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
                builder.AppendLine($"估算長度：{TimeSpan.FromSeconds(Math.Max(0, lastGranule - preSkip) / 48000d):hh\\:mm\\:ss\\.fff}");
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
            builder.AppendLine($"{title}：{count} 筆");
            for (var i = 0; i < count && i < 12 && offset + 4 <= packet.Length; i++)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset, 4));
                offset += 4;
                if (length > packet.Length - offset)
                    break;
                builder.AppendLine($"  {Encoding.UTF8.GetString(packet, offset, (int)length)}");
                offset += (int)length;
            }
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
        while (pos + 4 <= data.Length)
        {
            var header = data[pos];
            var isLast = (header & 0x80) != 0;
            var type = header & 0x7F;
            var length = (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            if (pos + length > data.Length)
                break;
            blockCount++;
            if (type == 0 && length >= 34)
            {
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
                commentCount += AppendFlacComments(builder, data.AsSpan(pos, length));
            }
            pos += length;
            if (isLast)
                break;
        }
        if (!streamInfoFound)
            builder.AppendLine("警告：找不到有效 STREAMINFO metadata。");
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

    private static int AppendFlacComments(StringBuilder builder, ReadOnlySpan<byte> block)
    {
        if (block.Length < 8)
            return 0;
        var pos = 0;
        var vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(block[pos..]);
        pos += 4;
        if (vendorLength > block.Length - pos)
            return 0;
        pos += (int)vendorLength;
        if (pos + 4 > block.Length)
            return 0;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(block[pos..]);
        pos += 4;
        var appended = 0;
        for (var i = 0; i < count && i < 12 && pos + 4 <= block.Length; i++)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(block[pos..]);
            pos += 4;
            if (length > block.Length - pos)
                break;
            builder.AppendLine($"  {Encoding.UTF8.GetString(block.Slice(pos, (int)length))}");
            pos += (int)length;
            appended++;
        }
        return appended;
    }
}
