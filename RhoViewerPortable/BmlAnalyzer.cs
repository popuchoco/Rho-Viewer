using System.Buffers.Binary;
using System.Text;
using System.Xml;

namespace RhoViewerPortable;

/// <summary>
/// Bounded reader for KartRider's length-prefixed binary XML-like resource format.
/// The wire layout is referenced from KartLibrary's BinaryXmlTag reader, but this
/// implementation validates every count and boundary before decoding or rendering.
/// </summary>
internal static class BmlAnalyzer
{
    internal const int MaxBmlBytes = 64 * 1024 * 1024;
    internal const int MaxDepth = 128;
    internal const int MaxNodes = 100_000;
    internal const int MaxAttributesPerNode = 4_096;
    internal const int MaxTotalAttributes = 100_000;
    internal const int MaxPreviewChars = 12_000;
    private const int MaxStringCodeUnits = 8 * 1024 * 1024;

    private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, true);

    public static string Analyze(byte[] data)
    {
        if (data.Length > MaxBmlBytes)
            return $"BML 資源過大，略過結構分析（上限 {MaxBmlBytes / (1024 * 1024)} MiB；目前 {data.Length / (1024 * 1024)} MiB）。可匯出原始資源後使用外部工具。";

        try
        {
            var parser = new Parser(data);
            parser.ReadDocument();
            var output = new StringBuilder();
            output.AppendLine("BML 二進位 XML-like 結構解析成功");
            output.AppendLine("格式：每個字串以 little-endian int32 UTF-16 code unit 數量開頭，字串內容為 UTF-16LE；後續欄位包含文字、屬性與子節點。");
            output.AppendLine($"根節點：{MakeSingleLine(parser.RootName)}");
            output.AppendLine($"節點：{parser.NodeCount:N0}；屬性：{parser.AttributeCount:N0}；最大深度：{parser.MaxObservedDepth:N0}");
            output.AppendLine($"原始大小：{data.Length:N0} bytes；解析已消耗全部資料，無尾隨位元組。");
            output.AppendLine();
            output.AppendLine("轉換後 XML 預覽（由二進位結構產生，並非檔案原始內容）：");

            var preview = new BoundedStringWriter(MaxPreviewChars);
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = true,
                IndentChars = "  ",
                NewLineChars = Environment.NewLine,
                NewLineHandling = NewLineHandling.Replace,
                ConformanceLevel = ConformanceLevel.Fragment,
                CheckCharacters = true,
                CloseOutput = false
            };
            using (var writer = XmlWriter.Create(preview, settings))
                parser.WritePreview(writer);

            output.Append(preview.ToString());
            if (preview.WasTruncated || parser.HadPreviewValueTruncation)
                output.AppendLine().AppendLine("…（XML 預覽已截斷；完整資源可匯出後檢視）");
            if (parser.HadXmlNameEncoding || parser.HadDuplicateAttributeNames || parser.HadInvalidXmlCharacters)
            {
                output.AppendLine();
                output.Append("預覽轉換提示：");
                if (parser.HadXmlNameEncoding)
                    output.Append("不符合 XML 名稱規則的名稱已編碼；");
                if (parser.HadDuplicateAttributeNames)
                    output.Append("重複屬性名稱已加上後綴；");
                if (parser.HadInvalidXmlCharacters)
                    output.Append("XML 不允許的控制字元以 U+FFFD 代替；");
                output.AppendLine();
            }
            return output.ToString();
        }
        catch (BmlFormatException ex)
        {
            return $"BML 二進位 XML-like 結構無法解析。\n原因：{ex.Message}\n位置：offset {ex.Offset:N0} / {data.Length:N0} bytes。\n\n目前保留原始資源供 HEX 預覽或匯出。";
        }
        catch (DecoderFallbackException)
        {
            return "BML 結構無法解析：字串不是有效的 UTF-16LE。\n目前保留原始資源供 HEX 預覽或匯出。";
        }
    }

    private static string MakeSingleLine(string value)
    {
        var length = Math.Min(value.Length, 160);
        var clean = new StringBuilder(length + (value.Length > length ? 1 : 0));
        for (var index = 0; index < length; index++)
            clean.Append(char.IsControl(value[index]) ? '�' : value[index]);
        if (value.Length > length)
            clean.Append('…');
        return clean.ToString();
    }

    private sealed class Parser(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _offset;
        private Node? _root;

        public string RootName => _root?.Name ?? string.Empty;
        public int NodeCount { get; private set; }
        public int AttributeCount { get; private set; }
        public int MaxObservedDepth { get; private set; }
        public bool HadXmlNameEncoding { get; private set; }
        public bool HadDuplicateAttributeNames { get; private set; }
        public bool HadInvalidXmlCharacters { get; private set; }
        public bool HadPreviewValueTruncation { get; private set; }

        public void ReadDocument()
        {
            _root = ReadNode(1);
            if (_offset != _data.Length)
                throw Invalid("節點結束後仍有未解析的尾隨資料");
        }

        public void WritePreview(XmlWriter writer) => _root!.WriteXml(writer, this);

        private Node ReadNode(int depth)
        {
            if (depth > MaxDepth)
                throw Invalid($"節點深度超過上限 {MaxDepth}");
            if (++NodeCount > MaxNodes)
                throw Invalid($"節點數超過上限 {MaxNodes}");
            MaxObservedDepth = Math.Max(MaxObservedDepth, depth);

            var name = ReadString();
            var text = ReadString();
            var node = new Node(name, text);

            var attributeCount = ReadCount("屬性數", MaxAttributesPerNode);
            if (attributeCount > (_data.Length - _offset) / 8)
                throw Invalid("屬性數量超過剩餘資料可容納範圍");
            if (attributeCount > MaxTotalAttributes - AttributeCount)
                throw Invalid($"總屬性數超過上限 {MaxTotalAttributes}");

            for (var index = 0; index < attributeCount; index++)
            {
                var attributeName = ReadString();
                var attributeValue = ReadString();
                node.Attributes.Add(new Attribute(attributeName, attributeValue));
                AttributeCount++;
            }

            var childCount = ReadCount("子節點數", MaxNodes);
            if (childCount > (_data.Length - _offset) / 16)
                throw Invalid("子節點數量超過剩餘資料可容納範圍");
            if (childCount > MaxNodes - NodeCount)
                throw Invalid($"節點數超過上限 {MaxNodes}");

            for (var index = 0; index < childCount; index++)
                node.Children.Add(ReadNode(depth + 1));
            return node;
        }

        private string ReadString()
        {
            var codeUnits = ReadInt32("字串長度");
            if (codeUnits < 0 || codeUnits > MaxStringCodeUnits)
                throw Invalid($"字串長度無效或超過上限（{codeUnits} UTF-16 code units）");

            var byteLength = (long)codeUnits * 2;
            if (byteLength > _data.Length - _offset)
                throw Invalid("字串內容超出檔案邊界");

            var value = StrictUtf16Le.GetString(_data, _offset, (int)byteLength);
            _offset += (int)byteLength;
            return value;
        }

        private int ReadCount(string field, int maximum)
        {
            var count = ReadInt32(field);
            if (count < 0 || count > maximum)
                throw Invalid($"{field}無效或超過上限（{count}）");
            return count;
        }

        private int ReadInt32(string field)
        {
            if (_offset > _data.Length - sizeof(int))
                throw Invalid($"缺少{field}欄位");
            var value = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_offset, sizeof(int)));
            _offset += sizeof(int);
            return value;
        }

        private BmlFormatException Invalid(string message) => new(message, _offset);

        private sealed class Node(string name, string text)
        {
            public string Name { get; } = name;
            public string Text { get; } = text;
            public List<Attribute> Attributes { get; } = [];
            public List<Node> Children { get; } = [];

            public void WriteXml(XmlWriter writer, Parser parser)
            {
                var elementName = parser.GetXmlSafeName(Name);
                writer.WriteStartElement(elementName);

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var attribute in Attributes)
                {
                    var name = parser.GetXmlSafeName(attribute.Name);
                    var uniqueName = name;
                    var suffix = 2;
                    while (!names.Add(uniqueName))
                    {
                        parser.HadDuplicateAttributeNames = true;
                        uniqueName = $"{name}_duplicate{suffix++}";
                    }
                    writer.WriteAttributeString(uniqueName, parser.MakeXmlSafe(attribute.Value));
                }

                if (Text.Length > 0)
                    writer.WriteString(parser.MakeXmlSafe(Text));
                foreach (var child in Children)
                    child.WriteXml(writer, parser);
                writer.WriteEndElement();
            }
        }

        private string GetXmlSafeName(string name)
        {
            try
            {
                XmlConvert.VerifyNCName(name);
                if (!string.Equals(name, "xmlns", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            catch (XmlException)
            {
            }

            HadXmlNameEncoding = true;
            if (name.Length == 0 || string.Equals(name, "xmlns", StringComparison.OrdinalIgnoreCase))
                return "_bml_" + Convert.ToHexString(Encoding.UTF8.GetBytes(name));
            var encodedName = XmlConvert.EncodeLocalName(name);
            return encodedName.Length == 0 ? "_bml_empty" : encodedName;
        }

        private string MakeXmlSafe(string value)
        {
            var limit = Math.Min(value.Length, MaxPreviewChars + 1);
            if (limit < value.Length && char.IsHighSurrogate(value[limit - 1]) && char.IsLowSurrogate(value[limit]))
                limit++;
            if (limit < value.Length)
                HadPreviewValueTruncation = true;

            StringBuilder? replacement = null;
            for (var index = 0; index < limit; index++)
            {
                var character = value[index];
                var valid = XmlConvert.IsXmlChar(character);
                if (char.IsHighSurrogate(character) && index + 1 < limit && char.IsLowSurrogate(value[index + 1]))
                {
                    index++;
                    valid = true;
                }

                if (valid)
                {
                    if (replacement is not null)
                    {
                        replacement.Append(character);
                        if (char.IsHighSurrogate(character))
                            replacement.Append(value[index]);
                    }
                    continue;
                }

                HadInvalidXmlCharacters = true;
                replacement ??= new StringBuilder(value.Length).Append(value, 0, index);
                replacement.Append('\uFFFD');
            }
            var output = replacement?.ToString() ?? (limit == value.Length ? value : value[..limit]);
            return limit == value.Length ? output : output + "…";
        }
    }

    private sealed record Attribute(string Name, string Value);

    private sealed class BmlFormatException(string message, int offset) : Exception(message)
    {
        public int Offset { get; } = offset;
    }

    private sealed class BoundedStringWriter(int maximumChars) : TextWriter
    {
        private readonly StringBuilder _builder = new(Math.Min(maximumChars, 4096));

        public bool WasTruncated { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (_builder.Length < maximumChars)
                _builder.Append(value);
            else
                WasTruncated = true;
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            var remaining = maximumChars - _builder.Length;
            if (remaining > 0)
                _builder.Append(value.AsSpan(0, Math.Min(remaining, value.Length)));
            if (value.Length > remaining)
                WasTruncated = true;
        }

        public override void Write(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (index < 0 || count < 0 || index > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(index));
            var remaining = maximumChars - _builder.Length;
            if (remaining > 0)
                _builder.Append(buffer, index, Math.Min(remaining, count));
            if (count > remaining)
                WasTruncated = true;
        }

        public override string ToString() => _builder.ToString();
    }
}
