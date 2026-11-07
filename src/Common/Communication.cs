using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal sealed class DataRecord
{
    public DateTime At = DateTime.Now; public string Kind, Message, Peer; public byte[] Bytes;
    public bool Datagram;
}
internal static class DataCodec
{
    internal static Encoding EncodingFor(string name)
    { return name == "GBK" ? Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback) : name == "ASCII" ? Encoding.GetEncoding(20127, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback) : new UTF8Encoding(false, false); }
    internal static byte[] Encode(string text, bool hex, string encoding, string newline)
    {
        byte[] result;
        if (hex)
        {
            var output = new List<byte>();
            foreach (var raw in Regex.Split(text.Trim(), "[\\s,;]+"))
            {
                if (raw.Length == 0) continue;
                string part = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw.Substring(2) : raw;
                if (part.Length == 0 || part.Length % 2 != 0) throw new FormatException("HEX 必须是完整字节，例如 AA BB 0D 0A。");
                for (int i = 0; i < part.Length; i += 2)
                {
                    byte value; if (!Byte.TryParse(part.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) throw new FormatException("HEX 中存在非十六进制字符。");
                    output.Add(value); if (output.Count > 65536) throw new FormatException("单次发送最多 65,536 字节。");
                }
            }
            result = output.ToArray();
        }
        else result = EncodingFor(encoding).GetBytes(text + (newline == "CRLF" ? "\r\n" : newline == "LF" ? "\n" : newline == "CR" ? "\r" : ""));
        if (result.Length == 0 || result.Length > 65536) throw new FormatException("单次发送应为 1–65,536 字节。");
        return result;
    }
    internal static string Render(IEnumerable<DataRecord> records, bool hex, string encoding, bool timestamps, bool showTx)
    {
        var output = new StringBuilder(); var codec = EncodingFor(encoding); var decoder = codec.GetDecoder();
        foreach (var item in records)
        {
            string content;
            if (item.Bytes == null) { content = item.Message; if (item.Kind == "OPEN" || item.Kind == "CLOSE") decoder.Reset(); }
            else
            {
                if (item.Kind == "TX" && !showTx) continue;
                if (hex) content = BitConverter.ToString(item.Bytes).Replace('-', ' ');
                else if (item.Kind == "RX")
                {
                    char[] chars = new char[codec.GetMaxCharCount(item.Bytes.Length)];
                    int n = decoder.GetChars(item.Bytes, 0, item.Bytes.Length, chars, 0, false); content = new string(chars, 0, n);
                }
                else content = codec.GetString(item.Bytes);
            }
            if (String.IsNullOrEmpty(content)) continue;
            if (timestamps) output.Append('[').Append(item.At.ToString("HH:mm:ss.fff")).Append("] ");
            output.Append(item.Kind).Append("  ").Append(content);
            if (!content.EndsWith("\n")) output.AppendLine();
        }
        return output.ToString();
    }

    internal static string ExpandEscapes(string text)
    {
        var result = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { result.Append(text[i]); continue; }
            if (++i == text.Length) throw new FormatException("转义符末尾缺少字符。");
            switch (text[i])
            {
                case '\\': result.Append('\\'); break;
                case 'r': result.Append('\r'); break;
                case 'n': result.Append('\n'); break;
                case 't': result.Append('\t'); break;
                case '0': result.Append('\0'); break;
                default: throw new FormatException("仅支持 \\\\, \\r, \\n, \\t, \\0；原始字节请使用 HEX。");
            }
        }
        return result.ToString();
    }

    internal static string RenderNetwork(IEnumerable<DataRecord> records, bool hex, string encoding, bool logging, bool newlines)
    {
        var result = new StringBuilder();
        var codec = EncodingFor(encoding);
        var decoders = new Dictionary<string, Decoder>();
        foreach (var item in records)
        {
            if (!logging && item.Kind != "RX") continue;
            string text;
            if (item.Bytes == null)
            {
                if (!String.IsNullOrEmpty(item.Peer) && item.Kind == "CLOSE") decoders.Remove(item.Peer);
                text = item.Message;
                if (!logging) continue;
            }
            else if (hex) text = BitConverter.ToString(item.Bytes).Replace('-', ' ');
            else if (item.Kind == "RX" && !item.Datagram)
            {
                string key = item.Peer ?? "";
                Decoder decoder;
                if (!decoders.TryGetValue(key, out decoder)) decoders[key] = decoder = codec.GetDecoder();
                var chars = new char[codec.GetMaxCharCount(item.Bytes.Length)];
                text = new string(chars, 0, decoder.GetChars(item.Bytes, 0, item.Bytes.Length, chars, 0, false));
            }
            else text = codec.GetString(item.Bytes);
            if (logging && item.Datagram && item.Bytes != null && item.Bytes.Length == 0) text = "（空数据报）";
            if (String.IsNullOrEmpty(text)) continue;
            if (logging) result.Append('[').Append(item.At.ToString("HH:mm:ss.fff")).Append("] ").Append(item.Kind).Append(' ').Append(item.Peer).Append("  ");
            result.Append(text);
            if ((logging || newlines) && !text.EndsWith("\n")) result.AppendLine();
        }
        return result.ToString();
    }
}
internal sealed class DataHistory
{
    internal const int Limit = 262144;
    readonly Queue<DataRecord> entries = new Queue<DataRecord>(); int bytes;
    internal bool Trimmed;
    internal IEnumerable<DataRecord> Entries { get { return entries; } }
    internal int ByteCount { get { return bytes; } }
    static int Size(DataRecord item) { return item.Bytes == null ? (item.Message ?? "").Length * 2 + 32 : item.Bytes.Length; }
    internal void Add(DataRecord item)
    {
        entries.Enqueue(item); bytes += Size(item);
        while (bytes > Limit || entries.Count > 4000) { bytes -= Size(entries.Dequeue()); Trimmed = true; }
    }
    internal void Clear() { entries.Clear(); bytes = 0; Trimmed = false; }
}
