using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal sealed class LanRange
{
    public uint First, Last;
    public int Count { get { return checked((int)((ulong)Last - First + 1)); } }
    public static uint Number(string text)
    {
        var parts = text.Trim().Split('.');
        if (parts.Length != 4) throw new FormatException("请输入完整 IPv4 地址，例如 192.168.50.1。");
        uint result = 0;
        foreach (string part in parts)
        {
            byte n;
            if (!Byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out n)) throw new FormatException("IPv4 地址每一段必须为 0–255。");
            result = (result << 8) | n;
        }
        return result;
    }
    public static IPAddress Address(uint number)
    { return new IPAddress(new[] { (byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number }); }
    public static LanRange Parse(string input)
    {
        string value = input.Trim(); uint first, last;
        if (value.Contains("/"))
        {
            var parts = value.Split('/'); int prefix;
            if (parts.Length != 2 || !Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > 32)
                throw new FormatException("CIDR 前缀应为 0–32，例如 192.168.50.0/24。");
            uint ip = Number(parts[0]); uint mask = prefix == 0 ? 0 : UInt32.MaxValue << (32 - prefix);
            first = ip & mask; last = first | ~mask;
            if (prefix <= 30) { first++; last--; }
        }
        else if (value.Contains("-"))
        {
            var parts = value.Split('-');
            if (parts.Length != 2) throw new FormatException("范围格式：192.168.50.1-192.168.50.254。");
            first = Number(parts[0]); last = Number(parts[1]);
        }
        else first = last = Number(value);
        if (last < first) throw new FormatException("结束 IP 不能小于起始 IP。");
        if ((ulong)last - first + 1 > 65536) throw new FormatException("一次最多扫描 65,536 个 IP，请缩小网段或地址范围。");
        return new LanRange { First = first, Last = last };
    }
    public static string Network(IPAddress ip, IPAddress mask)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork || mask == null) throw new FormatException("没有 IPv4 子网掩码。");
        uint bits = Number(mask.ToString()); int prefix = 0; bool zero = false;
        for (int i = 31; i >= 0; i--)
        {
            if ((bits & (1U << i)) == 0) zero = true;
            else { if (zero) throw new FormatException("子网掩码不连续。"); prefix++; }
        }
        return Address(Number(ip.ToString()) & bits) + "/" + prefix;
    }
}

internal sealed class LanProbe
{
    public bool Success; public long Milliseconds; public string Status, ReplyAddress;
}
internal sealed class LanResult
{
    public int Index, Attempts; public uint Address;
    public LanProbe Probe;
}
internal sealed class LanSummary
{
    public int Completed, Responsive; public bool Cancelled;
}
internal static class LanScanner
{
    static async Task<LanProbe> PingOne(IPAddress address, int timeout)
    {
        using (var ping = new Ping())
        {
            try
            {
                var reply = await ping.SendPingAsync(address, timeout, new byte[32], new PingOptions(64, false)).ConfigureAwait(false);
                return new LanProbe { Success = reply.Status == IPStatus.Success, Milliseconds = reply.RoundtripTime,
                    Status = reply.Status.ToString(), ReplyAddress = reply.Address == null ? "" : reply.Address.ToString() };
            }
            catch (PingException ex) { return new LanProbe { Status = "Ping 错误：" + ex.GetBaseException().Message }; }
            catch (Exception ex) { return new LanProbe { Status = "发送失败：" + ex.Message }; }
        }
    }
    internal static async Task<LanSummary> Run(LanRange range, int timeout, int parallel, int attempts,
        CancellationToken token, Action<LanResult> publish, Func<IPAddress, int, Task<LanProbe>> probe = null)
    {
        if (timeout < 100 || timeout > 5000 || parallel < 1 || parallel > 128 || attempts < 1 || attempts > 3)
            throw new ArgumentException("扫描参数超出范围。");
        probe = probe ?? PingOne;
        int next = -1, completed = 0, responsive = 0;
        var workers = Enumerable.Range(0, Math.Min(parallel, range.Count)).Select(worker => Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                int index = Interlocked.Increment(ref next); if (index >= range.Count) break;
                var result = new LanResult { Index = index, Address = range.First + (uint)index };
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    if (token.IsCancellationRequested) return;
                    result.Probe = await probe(LanRange.Address(result.Address), timeout).ConfigureAwait(false);
                    // Framework Ping has no cancellation token; wait for the bounded in-flight echo only.
                    if (token.IsCancellationRequested) return;
                    result.Attempts = attempt;
                    if (result.Probe.Success) break;
                }
                Interlocked.Increment(ref completed);
                if (result.Probe.Success) Interlocked.Increment(ref responsive);
                publish(result);
            }
        })).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        return new LanSummary { Completed = completed, Responsive = responsive, Cancelled = token.IsCancellationRequested };
    }
    // -1 pending, 0 no reply, 1 fast, 2 medium, 3 slow.
    internal static int Classify(LanResult result, int fast, int medium)
    {
        if (result == null) return -1;
        if (!result.Probe.Success) return 0;
        return result.Probe.Milliseconds < fast ? 1 : result.Probe.Milliseconds < medium ? 2 : 3;
    }
}
