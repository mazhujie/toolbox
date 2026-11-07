using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class FakePacketSource : IPacketSource
{
    internal readonly ConcurrentQueue<PacketFrame> Frames = new ConcurrentQueue<PacketFrame>();
    internal bool Disposed, Fail;
    public int LinkType { get { return 1; } }
    public long Dropped { get { return 3; } }
    public PacketFrame Read()
    {
        if (Fail) throw new IOException("simulated capture failure");
        PacketFrame frame; return Frames.TryDequeue(out frame) ? frame : null;
    }
    public void Dispose() { Disposed = true; }
}

internal static class CaptureTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Capture: " + message); }
    internal static PacketFrame Frame(byte[] data) { return new PacketFrame { Data = data, OriginalLength = (uint)data.Length, Seconds = 1700000000, Microseconds = 123456 }; }
    internal static PacketFrame Tcp()
    {
        byte[] bytes = new byte[54];
        bytes[12] = 8; bytes[14] = 0x45; bytes[17] = 40; bytes[23] = 6;
        bytes[26] = 192; bytes[27] = 0; bytes[28] = 2; bytes[29] = 1;
        bytes[30] = 198; bytes[31] = 51; bytes[32] = 100; bytes[33] = 2;
        bytes[34] = 0x30; bytes[35] = 0x39; bytes[36] = 1; bytes[37] = 0xbb;
        bytes[46] = 0x50; bytes[47] = 0x12;
        return Frame(bytes);
    }
    internal static int ReadFile(string file)
    {
        using (var reader = new BinaryReader(File.OpenRead(file)))
        {
            Check(reader.ReadUInt32() == 0xa1b2c3d4 && reader.ReadUInt16() == 2 && reader.ReadUInt16() == 4, "PCAP magic and version");
            Check(reader.ReadUInt32() == 0 && reader.ReadUInt32() == 0 && reader.ReadUInt32() == 65535 && reader.ReadUInt32() == 1, "PCAP header");
            int count = 0;
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                Check(reader.ReadUInt32() == 1700000000 && reader.ReadUInt32() == 123456, "microsecond timestamps");
                uint captured = reader.ReadUInt32(), original = reader.ReadUInt32();
                Check(captured <= 65535 && captured <= original, "captured/wire lengths");
                Check(reader.ReadBytes((int)captured).Length == captured, "complete PCAP record"); count++;
            }
            return count;
        }
    }
    internal static async Task Run(string directory, StringBuilder report)
    {
        Directory.CreateDirectory(directory);
        var tcp = Tcp(); var decoded = PacketDecoder.Decode(tcp, 1, 7);
        Check(decoded.Protocol == "TCP" && decoded.Source == "192.0.2.1:12345" && decoded.Destination == "198.51.100.2:443" && decoded.Info.Contains("SYN ACK"), "IPv4 TCP endpoints and flags");
        Check(PacketDecoder.Detail(decoded).Contains("0000") && PacketDecoder.Detail(decoded).Contains("#7"), "HEX detail");
        var vlan = new byte[58]; Array.Copy(tcp.Data, 0, vlan, 0, 12); vlan[12] = 0x81; vlan[16] = 8; Array.Copy(tcp.Data, 14, vlan, 18, 40);
        Check(PacketDecoder.Decode(Frame(vlan), 1, 1).Protocol == "TCP", "VLAN transport");
        var fragment = (byte[])tcp.Data.Clone(); fragment[21] = 1;
        Check(PacketDecoder.Decode(Frame(fragment), 1, 1).Protocol == "IPv4", "noninitial fragment has no invented ports");
        var loop = new byte[44]; loop[0] = 2; Array.Copy(tcp.Data, 14, loop, 4, 40);
        Check(PacketDecoder.Decode(Frame(loop), 0, 1).Protocol == "TCP", "Npcap loopback");
        Check(PacketDecoder.Decode(Frame(tcp.Data.Skip(14).ToArray()), 101, 1).Protocol == "TCP" && PcapFile.FileLinkType(12) == 101, "raw IP and savefile link type");
        var udp6 = new byte[62]; udp6[12] = 0x86; udp6[13] = 0xdd; udp6[14] = 0x60; udp6[19] = 8; udp6[20] = 17; udp6[37] = 1; udp6[53] = 2;
        udp6[55] = 53; udp6[57] = 100; udp6[59] = 8;
        decoded = PacketDecoder.Decode(Frame(udp6), 1, 1);
        Check(decoded.Protocol == "UDP" && decoded.Source == "[::1]:53" && decoded.Destination == "[::2]:100", "IPv6 UDP");
        var padded = new byte[70]; Array.Copy(udp6, padded, udp6.Length); padded[19] = 16;
        Check(PacketDecoder.Decode(Frame(padded), 1, 1).Info.Contains("负载 0 字节"), "UDP payload excludes trailing padding");
        padded[59] = 4;
        Check(PacketDecoder.Decode(Frame(padded), 1, 1).Info.Contains("长度无效"), "invalid UDP length");
        var icmp = (byte[])tcp.Data.Clone(); icmp[23] = 1; icmp[34] = 8; icmp[35] = 0;
        Check(PacketDecoder.Decode(Frame(icmp), 1, 1).Info.Contains("Type 8"), "ICMP echo");
        var arp = new byte[42]; arp[12] = 8; arp[13] = 6; arp[17] = 0; arp[16] = 8; arp[18] = 6; arp[19] = 4; arp[21] = 1; arp[31] = 1; arp[41] = 2;
        decoded = PacketDecoder.Decode(Frame(arp), 1, 1);
        Check(decoded.Protocol == "ARP" && decoded.Source == "0.0.0.1" && decoded.Destination == "0.0.0.2", "ARP addresses");
        // Exercise every truncated prefix plus random malformed inputs; decoding must never read beyond the capture.
        foreach (var sample in new[] { tcp.Data, vlan, udp6, arp, loop })
            for (int i = 0; i <= sample.Length; i++) PacketDecoder.Detail(PacketDecoder.Decode(Frame(sample.Take(i).ToArray()), 1, i));
        var random = new Random(1234);
        for (int i = 0; i < 10000; i++)
        {
            var bytes = new byte[random.Next(300)]; random.NextBytes(bytes);
            PacketDecoder.Decode(Frame(bytes), new[] { 0, 1, 101, 108, 127, 228, 229 }[i % 7], i);
        }
        Check(PacketDecoder.Detail(PacketDecoder.Decode(Frame(new byte[6000]), 1, 1)).Contains("4,096"), "bounded HEX preview");
        report.AppendLine("PASS packet decoding: Ethernet/VLAN, IPv4/IPv6, TCP/UDP/ICMP/ARP, loopback, fragments and 10,000 malformed inputs");

        var source = new FakePacketSource(); source.Frames.Enqueue(tcp); source.Frames.Enqueue(tcp);
        var options = new CaptureOptions { Adapter = "fake", File = Path.Combine(directory, "limit.pcap"), ByteLimit = 24 + 16 + tcp.Data.Length };
        var session = new CaptureSession(o => source); int callbacks = 0;
        await session.Run(options, CancellationToken.None, p => callbacks++);
        Check(source.Disposed && session.Count == 1 && callbacks == 1 && session.Bytes == options.ByteLimit && session.Dropped == 3 && ReadFile(options.File) == 1, "exact file limit and release");
        Check(session.Reason.Contains("大小"), "size stop reason");
        bool collision = false; source = new FakePacketSource();
        try { await new CaptureSession(o => source).Run(options, CancellationToken.None, p => { }); } catch (IOException) { collision = true; }
        Check(collision && source.Disposed && ReadFile(options.File) == 1, "existing capture cannot be overwritten");

        source = new FakePacketSource(); options.File = Path.Combine(directory, "idle.pcap"); options.Seconds = 1;
        var watch = Stopwatch.StartNew(); await new CaptureSession(o => source).Run(options, CancellationToken.None, p => { });
        Check(source.Disposed && watch.ElapsedMilliseconds >= 900 && watch.ElapsedMilliseconds < 4000 && ReadFile(options.File) == 0, "idle timeout");
        source = new FakePacketSource(); options.File = Path.Combine(directory, "cancel.pcap"); options.Seconds = 60;
        using (var cancel = new CancellationTokenSource())
        {
            var task = new CaptureSession(o => source).Run(options, cancel.Token, p => { });
            await Task.Delay(50); watch.Restart(); cancel.Cancel(); await task;
            Check(source.Disposed && watch.ElapsedMilliseconds < 1000 && ReadFile(options.File) == 0, "idle cancellation does not hang");
        }
        source = new FakePacketSource(); source.Frames.Enqueue(tcp); options.File = Path.Combine(directory, "failure.pcap"); options.ByteLimit = 4096;
        bool failed = false;
        try { await new CaptureSession(o => source).Run(options, CancellationToken.None, p => source.Fail = true); } catch (IOException) { failed = true; }
        Check(failed && source.Disposed && ReadFile(options.File) == 1, "source error preserves completed PCAP records and closes handle");
        report.AppendLine("PASS PCAP format, exact size limit, timestamps, no overwrite, idle timeout/cancel, drop counts and error cleanup");
    }
}
