using System.Buffers.Binary;
using System.Text;

namespace RhoViewerPortable;

/// <summary>
/// Read-only fingerprinting for KartRider .1s resources. This intentionally does
/// not claim to fully decode the format or validate every game/version variant.
/// </summary>
internal static class OneSAnalyzer
{
    // Public format-marker references documented by VT-Tuzki's MIT-licensed
    // kartrider_model_1s_to_obj project; this implementation does not copy its parser.
    private static readonly Marker[] KnownMarkers =
    {
        new("FILE_HEADER", new byte[] { 0xAA, 0x47, 0x49, 0x02, 0x8C, 0x07 }),
        new("BASE_MODEL_HEADER", new byte[] { 0xAA, 0x47, 0x3C, 0x03, 0x07, 0x0E }),
        new("MODEL_HEADER", new byte[] { 0xAA, 0x47, 0x46, 0x04, 0x2A, 0x19 }),
        new("VERTEX_COORDINATES_HEADER", new byte[] { 0xAA, 0x27 })
    };

    public static string Analyze(byte[] data)
    {
        var builder = new StringBuilder("1S 資源初步結構探測（唯讀）");
        builder.AppendLine();
        builder.AppendLine($"資料大小：{data.Length:N0} bytes");
        builder.AppendLine("已知標記候選位置：");

        var fileHeaders = Find(KnownMarkers[0].Bytes, data);
        var anyMarker = false;
        foreach (var marker in KnownMarkers)
        {
            var result = Find(marker.Bytes, data);
            anyMarker |= result.Count > 0;
            builder.AppendLine($"  {marker.Name}：{result.Count:N0} 個" + FormatOffsets(result.Offsets));
        }

        foreach (var offset in fileHeaders.Offsets)
        {
            if (offset > data.Length - 16)
                continue;

            var declaredModules = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12, 4));
            builder.AppendLine($"FILE_HEADER @ 0x{offset:X8} 候選宣告模組數：{declaredModules:N0}");
        }

        if (!anyMarker)
            builder.AppendLine("未找到上述已知標記；這可能是不同版本或其他用途的 .1s 資料，不能僅憑此結果判定檔案無效。");

        builder.AppendLine();
        builder.Append("解析範圍：只列出公開研究中使用的標記與候選模組數；尚未驗證模型拓樸、材質、動畫或所有版本差異。來源：VT-Tuzki/kartrider_model_1s_to_obj（MIT，僅參考標記定義，未移植程式碼）。");
        return builder.ToString();
    }

    private static MarkerHits Find(byte[] marker, byte[] data)
    {
        const int maxOffsetsToDisplay = 8;
        var offsets = new List<int>(maxOffsetsToDisplay);
        var count = 0;
        var position = 0;
        while (position <= data.Length - marker.Length)
        {
            var relativeOffset = data.AsSpan(position).IndexOf(marker);
            if (relativeOffset < 0)
                break;

            var offset = position + relativeOffset;
            count++;
            if (offsets.Count < maxOffsetsToDisplay)
                offsets.Add(offset);
            position = offset + 1;
        }

        return new MarkerHits(count, offsets);
    }

    private static string FormatOffsets(IReadOnlyList<int> offsets) => offsets.Count == 0
        ? string.Empty
        : $"；offset：{string.Join(", ", offsets.Select(offset => $"0x{offset:X8}"))}";

    private sealed record Marker(string Name, byte[] Bytes);
    private sealed record MarkerHits(int Count, IReadOnlyList<int> Offsets);
}
