using System;
using System.Linq;
using System.Net;
using System.Text;

internal sealed class PacketRecord
{
    internal long Number;
    internal PacketFrame Frame;
    internal string Source = "—", Destination = "—", Protocol = "未知", Info = "";
}

internal static class PacketDecoder
{
    static int U16(byte[] bytes, int offset) { return bytes[offset] * 256 + bytes[offset + 1]; }
    static string Address(byte[] bytes, int offset, int size) { var value = new byte[size]; Buffer.BlockCopy(bytes, offset, value, 0, size); return new IPAddress(value).ToString(); }
    static string Mac(byte[] bytes, int offset) { return BitConverter.ToString(bytes, offset, 6).Replace('-', ':'); }
    static bool Has(byte[] bytes, int offset, int count) { return offset >= 0 && count >= 0 && offset <= bytes.Length - count; }
    static bool Need(PacketRecord record, int offset, int count)
    {
        if (Has(record.Frame.Data, offset, count)) return true;
        record.Info = "包头不完整 / 已截断"; return false;
    }
    internal static PacketRecord Decode(PacketFrame frame, int linkType, long number)
    {
        var record = new PacketRecord { Number = number, Frame = frame };
        byte[] bytes = frame.Data;
        int offset = 0, type = 0;
        if (linkType == 1)
        {
            record.Protocol = "Ethernet";
            if (!Need(record, 0, 14)) return record;
            record.Source = Mac(bytes, 6); record.Destination = Mac(bytes, 0);
            type = U16(bytes, 12); offset = 14;
            int tags = 0;
            while (type == 0x8100 || type == 0x88a8 || type == 0x9100)
            {
                if (++tags > 4 || !Need(record, offset, 4)) { record.Info = "VLAN 头不完整或层数过多"; return record; }
                type = U16(bytes, offset + 2); offset += 4;
            }
        }
        else if (linkType == 0 || linkType == 108)
        {
            record.Protocol = "Loopback";
            if (!Need(record, 0, 4)) return record;
            uint family = linkType == 0 ? BitConverter.ToUInt32(bytes, 0) : (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            type = family == 2 ? 0x0800 : family == 23 || family == 24 || family == 28 || family == 30 ? 0x86dd : 0; offset = 4;
        }
        else if (linkType == 101 || linkType == 228 || linkType == 229)
        {
            record.Protocol = "IP";
            if (!Need(record, 0, 1)) return record;
            type = (bytes[0] >> 4) == 4 ? 0x0800 : (bytes[0] >> 4) == 6 ? 0x86dd : 0;
        }
        else { record.Protocol = "链路 " + linkType; record.Info = "此链路仅显示原始数据"; return record; }
        if (type == 0x0800) IPv4(record, offset);
        else if (type == 0x86dd) IPv6(record, offset);
        else if (type == 0x0806) Arp(record, offset);
        else record.Info = "链路协议 0x" + type.ToString("X4");
        if (frame.Data.Length < frame.OriginalLength) record.Info += " [捕获 " + frame.Data.Length + "/" + frame.OriginalLength + " 字节]";
        return record;
    }
    static void IPv4(PacketRecord record, int offset)
    {
        record.Protocol = "IPv4";
        if (!Need(record, offset, 20)) return;
        var bytes = record.Frame.Data;
        int header = (bytes[offset] & 15) * 4, total = U16(bytes, offset + 2);
        if (bytes[offset] >> 4 != 4 || header < 20 || total < header) { record.Info = "IPv4 头长度无效"; return; }
        record.Source = Address(bytes, offset + 12, 4); record.Destination = Address(bytes, offset + 16, 4);
        if (!Need(record, offset, header)) return;
        int fragment = U16(bytes, offset + 6);
        if ((fragment & 0x1fff) != 0) { record.Info = "IPv4 分片，偏移 " + ((fragment & 0x1fff) * 8); return; }
        Transport(record, offset + header, Math.Min(bytes.Length, offset + total), bytes[offset + 9]);
        if ((fragment & 0x2000) != 0) record.Info += " [首片，后续仍有分片]";
    }
    static void IPv6(PacketRecord record, int offset)
    {
        record.Protocol = "IPv6";
        if (!Need(record, offset, 40)) return;
        byte[] bytes = record.Frame.Data;
        if (bytes[offset] >> 4 != 6) { record.Info = "IPv6 版本无效"; return; }
        record.Source = Address(bytes, offset + 8, 16); record.Destination = Address(bytes, offset + 24, 16);
        int length = U16(bytes, offset + 4), protocol = bytes[offset + 6], end = Math.Min(bytes.Length, offset + 40 + length);
        if (length == 0) { record.Info = "空载荷或 IPv6 Jumbo 扩展（未解析）"; return; }
        offset += 40; int extensions = 0;
        while (protocol == 0 || protocol == 43 || protocol == 60 || protocol == 44 || protocol == 51)
        {
            if (++extensions > 8 || offset > end - 8) { record.Info = "IPv6 扩展头不完整或层数过多"; return; }
            int next = bytes[offset], size = protocol == 44 ? 8 : protocol == 51 ? (bytes[offset + 1] + 2) * 4 : (bytes[offset + 1] + 1) * 8;
            if (size < 8 || offset > end - size) { record.Info = "IPv6 扩展头长度无效"; return; }
            if (protocol == 44 && (U16(bytes, offset + 2) & 0xfff8) != 0) { record.Info = "IPv6 分片，偏移 " + (U16(bytes, offset + 2) & 0xfff8); return; }
            offset += size; protocol = next;
        }
        Transport(record, offset, end, protocol);
    }
    static void Transport(PacketRecord record, int offset, int end, int protocol)
    {
        byte[] bytes = record.Frame.Data;
        record.Protocol = protocol == 6 ? "TCP" : protocol == 17 ? "UDP" : protocol == 1 ? "ICMP" : protocol == 58 ? "ICMPv6" : protocol == 50 ? "ESP" : record.Protocol;
        int minimum = protocol == 6 ? 20 : protocol == 17 ? 8 : protocol == 1 || protocol == 58 ? 4 : 0;
        if (offset > end - minimum) { record.Info = "传输层头不完整 / 已截断"; return; }
        if (protocol == 6 || protocol == 17)
        {
            int source = U16(bytes, offset), destination = U16(bytes, offset + 2);
            int header = protocol == 6 ? (bytes[offset + 12] >> 4) * 4 : 8;
            if (header < minimum || offset > end - header) { record.Info = "传输层头长度无效 / 已截断"; return; }
            record.Source = Endpoint(record.Source, source); record.Destination = Endpoint(record.Destination, destination);
            if (protocol == 17)
            {
                int length = U16(bytes, offset + 4);
                if (length < 8) { record.Info = "UDP 长度无效"; return; }
                end = Math.Min(end, offset + length);
            }
            record.Info = source + " → " + destination + "，负载 " + (end - offset - header) + " 字节";
            if (protocol == 6)
            {
                string[] names = { "FIN", "SYN", "RST", "PSH", "ACK", "URG", "ECE", "CWR" };
                record.Info += " [" + String.Join(" ", names.Where((name, index) => (bytes[offset + 13] & (1 << index)) != 0)) + "]";
            }
        }
        else if (protocol == 1 || protocol == 58) record.Info = "Type " + bytes[offset] + "，Code " + bytes[offset + 1];
        else record.Info = "IP 协议号 " + protocol + "，" + (end - offset) + " 字节";
    }
    static string Endpoint(string address, int port) { return (address.Contains(":") ? "[" + address + "]" : address) + ":" + port; }
    static void Arp(PacketRecord record, int offset)
    {
        record.Protocol = "ARP";
        if (!Need(record, offset, 8)) return;
        var bytes = record.Frame.Data; int hardware = bytes[offset + 4], address = bytes[offset + 5];
        if (!Need(record, offset, 8 + 2 * (hardware + address))) return;
        if (U16(bytes, offset + 2) == 0x0800 && address == 4)
        {
            record.Source = Address(bytes, offset + 8 + hardware, 4);
            record.Destination = Address(bytes, offset + 8 + hardware + address + hardware, 4);
        }
        int operation = U16(bytes, offset + 6);
        record.Info = operation == 1 ? "ARP 请求" : operation == 2 ? "ARP 应答" : "ARP 操作 " + operation;
    }
    internal static string Detail(PacketRecord packet)
    {
        var text = new StringBuilder(); byte[] bytes = packet.Frame.Data;
        text.Append('#').Append(packet.Number).Append("  ").Append(packet.Frame.At.ToString("yyyy-MM-dd HH:mm:ss.ffffff")).AppendLine();
        text.Append(packet.Protocol).Append("  ").Append(packet.Source).Append(" → ").Append(packet.Destination).AppendLine();
        text.Append(packet.Info).AppendLine().AppendLine();
        int shown = Math.Min(bytes.Length, 4096);
        for (int offset = 0; offset < shown; offset += 16)
        {
            text.Append(offset.ToString("X4")).Append("  ");
            for (int i = 0; i < 16; i++) text.Append(offset + i < shown ? bytes[offset + i].ToString("X2") + " " : "   ");
            text.Append(" ");
            for (int i = offset; i < Math.Min(shown, offset + 16); i++) text.Append(bytes[i] >= 32 && bytes[i] <= 126 ? (char)bytes[i] : '.');
            text.AppendLine();
        }
        if (shown < bytes.Length) text.AppendLine("仅预览前 4,096 字节，完整捕获内容保存在 PCAP 文件。");
        return text.ToString();
    }
}
