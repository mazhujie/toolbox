using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class CaptureAdapter
{
    internal string Name, Description;
    public override string ToString() { return Description; }
}
internal sealed class CaptureOptions
{
    internal string Adapter, Filter = "", File;
    internal bool Promiscuous;
    internal int Seconds = 60;
    internal long ByteLimit = 64 * 1024 * 1024;
    internal void Validate()
    {
        if (String.IsNullOrWhiteSpace(Adapter)) throw new ArgumentException("请先刷新并选择网卡。");
        if (Filter == null || Filter.Length > 2048 || Filter.Any(c => c > 127 || Char.IsControl(c))) throw new ArgumentException("抓包过滤式应为不超过 2048 字符的 ASCII BPF 表达式。");
        if (Seconds < 1 || Seconds > 3600 || ByteLimit < 24 || ByteLimit > 512L * 1024 * 1024) throw new ArgumentException("抓包时长或文件上限无效。");
        if (String.IsNullOrWhiteSpace(File) || !Path.IsPathRooted(File)) throw new ArgumentException("请选择完整的本地保存路径。");
    }
}
internal sealed class PacketFrame
{
    internal uint Seconds, Microseconds, OriginalLength;
    internal byte[] Data;
    internal DateTime At { get { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Seconds).AddTicks(Microseconds * 10L).ToLocalTime(); } }
}
internal interface IPacketSource : IDisposable
{
    int LinkType { get; }
    PacketFrame Read();
    long Dropped { get; }
}

internal static class Npcap
{
    const string Library = "wpcap.dll";
    static readonly object sync = new object();
    static IntPtr module;
    static bool loaded;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr pcap_lib_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pcap_findalldevs(out IntPtr devices, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void pcap_freealldevs(IntPtr devices);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern IntPtr pcap_open_live(string device, int snaplen, int promisc, int timeout, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void pcap_close(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pcap_datalink(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pcap_setnonblock(IntPtr handle, int value, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pcap_next_ex(IntPtr handle, out IntPtr header, out IntPtr data);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int pcap_compile(IntPtr handle, ref FilterProgram program, string filter, int optimize, uint netmask);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pcap_setfilter(IntPtr handle, ref FilterProgram program);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void pcap_freecode(ref FilterProgram program);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr pcap_geterr(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pcap_stats(IntPtr handle, IntPtr stats);
    [StructLayout(LayoutKind.Sequential)] internal struct FilterProgram { internal uint Length; internal IntPtr Instructions; }
    [StructLayout(LayoutKind.Sequential)] internal struct Header { internal uint Seconds, Microseconds, Captured, Original; }
    [StructLayout(LayoutKind.Sequential)] struct Device { internal IntPtr Next, Name, Description, Addresses; internal uint Flags; }

    internal static void Load()
    {
        lock (sync)
        {
            if (loaded) return;
            string system = Environment.SystemDirectory;
            string path = new[] { Path.Combine(system, "Npcap", Library), Path.Combine(system, Library) }.FirstOrDefault(File.Exists);
            if (path == null) throw new IOException("未找到 Npcap。请从左侧官网入口安装 Npcap，然后刷新网卡。");
            // Load only the installed system library and its own/system dependencies, never a DLL beside the EXE.
            module = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x800);
            if (module == IntPtr.Zero) throw new IOException("Npcap 加载失败（Windows 错误 " + Marshal.GetLastWin32Error() + "），请修复与系统位数匹配的 Npcap 安装。");
            string version = Marshal.PtrToStringAnsi(pcap_lib_version()) ?? "";
            if (version.IndexOf("Npcap", StringComparison.OrdinalIgnoreCase) < 0) throw new IOException("检测到旧版 WinPcap，请安装 Npcap 后重启工具箱。");
            loaded = true;
        }
    }

    internal static CaptureAdapter[] Adapters()
    {
        Load();
        var error = new StringBuilder(256); IntPtr head;
        if (pcap_findalldevs(out head, error) != 0) throw new IOException("网卡枚举失败：" + error);
        try
        {
            var result = new List<CaptureAdapter>();
            NetworkInterface[] local;
            try { local = NetworkInterface.GetAllNetworkInterfaces(); } catch (NetworkInformationException) { local = new NetworkInterface[0]; }
            for (IntPtr p = head; p != IntPtr.Zero; )
            {
                var item = (Device)Marshal.PtrToStructure(p, typeof(Device)); p = item.Next;
                string name = Marshal.PtrToStringAnsi(item.Name), description = Marshal.PtrToStringAnsi(item.Description);
                if (String.IsNullOrEmpty(name)) continue;
                var nic = local.FirstOrDefault(n => name.IndexOf(n.Id.Trim('{', '}'), StringComparison.OrdinalIgnoreCase) >= 0);
                result.Add(new CaptureAdapter { Name = name, Description = nic != null ? nic.Name + " · " + nic.Description : (item.Flags & 1) != 0 ? "本机回环 · " + (description ?? name) : description ?? name });
            }
            return result.ToArray();
        }
        finally { if (head != IntPtr.Zero) pcap_freealldevs(head); }
    }
}

internal sealed class NpcapSource : IPacketSource
{
    IntPtr handle;
    readonly IntPtr stats = Marshal.AllocHGlobal(256);
    public int LinkType { get; private set; }
    internal NpcapSource(CaptureOptions options)
    {
        try
        {
            Npcap.Load();
            var error = new StringBuilder(256);
            handle = Npcap.pcap_open_live(options.Adapter, 65535, options.Promiscuous ? 1 : 0, 100, error);
            if (handle == IntPtr.Zero) throw new IOException("打开网卡失败：" + error + "。请检查 Npcap 驱动与抓包权限。");
            LinkType = PcapFile.FileLinkType(Npcap.pcap_datalink(handle));
            if (options.Filter.Length > 0)
            {
                var program = new Npcap.FilterProgram();
                if (Npcap.pcap_compile(handle, ref program, options.Filter, 1, UInt32.MaxValue) != 0) throw new IOException("BPF 过滤式无效：" + Error());
                try { if (Npcap.pcap_setfilter(handle, ref program) != 0) throw new IOException("无法应用抓包过滤式：" + Error()); }
                finally { Npcap.pcap_freecode(ref program); }
            }
            if (Npcap.pcap_setnonblock(handle, 1, error) != 0) throw new IOException("无法启用可停止的抓包模式：" + error);
        }
        catch { Dispose(); throw; }
    }
    string Error() { return Marshal.PtrToStringAnsi(Npcap.pcap_geterr(handle)); }
    public PacketFrame Read()
    {
        IntPtr header, data;
        int result = Npcap.pcap_next_ex(handle, out header, out data);
        if (result == 0) return null;
        if (result != 1) throw new IOException("抓包读取失败：" + Error());
        var value = (Npcap.Header)Marshal.PtrToStructure(header, typeof(Npcap.Header));
        if (value.Captured > 65535 || value.Original < value.Captured || value.Microseconds >= 1000000) throw new IOException("Npcap 返回无效的数据包头。");
        var bytes = new byte[value.Captured]; if (bytes.Length > 0) Marshal.Copy(data, bytes, 0, bytes.Length);
        return new PacketFrame { Seconds = value.Seconds, Microseconds = value.Microseconds, OriginalLength = value.Original, Data = bytes };
    }
    public long Dropped { get { return Npcap.pcap_stats(handle, stats) == 0 ? unchecked((uint)Marshal.ReadInt32(stats, 4)) : -1L; } }
    bool disposed;
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (handle != IntPtr.Zero) { Npcap.pcap_close(handle); handle = IntPtr.Zero; }
        Marshal.FreeHGlobal(stats);
    }
}

internal sealed class PcapFile : IDisposable
{
    readonly BinaryWriter writer;
    internal long Length { get { return writer.BaseStream.Position; } }
    internal static int FileLinkType(int native)
    {
        if (native == 12) return 101; // DLT_RAW differs from its savefile LINKTYPE_RAW value.
        if (new[] { 0, 1, 101, 105, 108, 127, 228, 229 }.Contains(native)) return native;
        throw new NotSupportedException("暂不支持此网卡的链路类型（" + native + "），请使用以太网、Wi-Fi 或回环网卡。");
    }
    internal PcapFile(string path, int linkType)
    {
        writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        try { writer.Write(0xa1b2c3d4U); writer.Write((ushort)2); writer.Write((ushort)4); writer.Write(0); writer.Write(0U); writer.Write(65535U); writer.Write((uint)FileLinkType(linkType)); }
        catch { writer.Dispose(); throw; }
    }
    internal void Write(PacketFrame packet)
    {
        if (packet.Data == null || packet.Data.Length > 65535 || packet.OriginalLength < packet.Data.Length || packet.Microseconds >= 1000000) throw new IOException("数据包长度或时间戳无效。");
        writer.Write(packet.Seconds); writer.Write(packet.Microseconds); writer.Write((uint)packet.Data.Length); writer.Write(packet.OriginalLength); writer.Write(packet.Data);
    }
    internal void Flush() { writer.Flush(); }
    public void Dispose() { writer.Dispose(); }
}

internal sealed class CaptureSession
{
    readonly Func<CaptureOptions, IPacketSource> factory;
    long count, bytes, dropped = -1;
    internal long Count { get { return Interlocked.Read(ref count); } }
    internal long Bytes { get { return Interlocked.Read(ref bytes); } }
    internal long Dropped { get { return Interlocked.Read(ref dropped); } }
    internal string Reason;
    internal CaptureSession(Func<CaptureOptions, IPacketSource> source) { factory = source; }
    internal Task Run(CaptureOptions options, CancellationToken token, Action<PacketRecord> packet)
    {
        options.Validate();
        return Task.Run(() => {
            token.ThrowIfCancellationRequested();
            using (var source = factory(options))
            {
                token.ThrowIfCancellationRequested();
                using (var file = new PcapFile(options.File, source.LinkType))
                {
                    var watch = Stopwatch.StartNew(); long flushed = 0;
                    Interlocked.Exchange(ref bytes, file.Length); Reason = "已停止";
                    while (!token.IsCancellationRequested)
                    {
                        if (watch.Elapsed.TotalSeconds >= options.Seconds) { Reason = "已达到时长上限"; break; }
                        var frame = source.Read();
                        if (frame == null) { token.WaitHandle.WaitOne(10); continue; }
                        if (frame.Data == null || frame.Data.Length > 65535) throw new IOException("捕获数据长度无效。");
                        if (file.Length + 16 + frame.Data.Length > options.ByteLimit) { Reason = "已达到文件大小上限"; break; }
                        file.Write(frame); long number = Interlocked.Increment(ref count); Interlocked.Exchange(ref bytes, file.Length);
                        packet(PacketDecoder.Decode(frame, source.LinkType, number));
                        if (watch.ElapsedMilliseconds - flushed >= 1000) { file.Flush(); flushed = watch.ElapsedMilliseconds; Interlocked.Exchange(ref dropped, source.Dropped); }
                    }
                    file.Flush(); Interlocked.Exchange(ref dropped, source.Dropped);
                }
            }
        });
    }
}
