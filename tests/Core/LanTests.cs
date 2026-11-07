using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

internal static class LanTests
{
    static void Assert(bool value, string name) { if (!value) throw new Exception("FAIL LAN: " + name); }
    internal static async Task<string> Run()
    {
        var cidr = LanRange.Parse("192.168.50.87/24");
        Assert(cidr.Count == 254 && LanRange.Address(cidr.First).ToString() == "192.168.50.1" && LanRange.Address(cidr.Last).ToString() == "192.168.50.254", "CIDR normalization excludes network/broadcast");
        Assert(LanRange.Parse("192.0.2.0/31").Count == 2 && LanRange.Parse("192.0.2.1/32").Count == 1, "/31 and /32");
        Assert(LanRange.Parse("10.1.0.0/16").Count == 65534 && LanRange.Parse("192.0.2.254-192.0.3.1").Count == 4, "large network and cross-octet range");
        Assert(LanRange.Address(LanRange.Number("255.255.255.255")).ToString() == "255.255.255.255", "unsigned conversion");
        Assert(LanRange.Network(IPAddress.Parse("192.168.4.5"), IPAddress.Parse("255.255.252.0")) == "192.168.4.0/22", "adapter mask");
        foreach (var value in new[] { "10.0.0.0/8", "192.0.2.0/33", "192.0.2.9-192.0.2.1", "192.168.1.999", "127.1" })
        { bool failed = false; try { LanRange.Parse(value); } catch (FormatException) { failed = true; } Assert(failed, "invalid or oversized range rejected"); }
        Assert(LanScanner.Classify(null, 10, 50) == -1 && LanScanner.Classify(new LanResult { Probe = new LanProbe { Success = false } }, 10, 50) == 0, "pending is distinct from no reply");
        int[] samples = { 0, 9, 10, 49, 50, 100 }, expected = { 1, 1, 2, 2, 3, 3 };
        for (int i = 0; i < samples.Length; i++) Assert(LanScanner.Classify(new LanResult { Probe = new LanProbe { Success = true, Milliseconds = samples[i] } }, 10, 50) == expected[i], "latency boundaries");
        var replies = new ConcurrentDictionary<int, LanResult>(); int active = 0, peak = 0, calls = 0;
        var summary = await LanScanner.Run(LanRange.Parse("192.0.2.1-192.0.2.20"), 100, 4, 2, CancellationToken.None, result => {
            if (!replies.TryAdd(result.Index, result)) throw new Exception("duplicate result");
        }, async (ip, timeout) => {
            Interlocked.Increment(ref calls); int current = Interlocked.Increment(ref active), old;
            do { old = peak; if (old >= current) break; } while (Interlocked.CompareExchange(ref peak, current, old) != old);
            await Task.Delay(8).ConfigureAwait(false); Interlocked.Decrement(ref active);
            return new LanProbe { Success = ip.GetAddressBytes()[3] % 2 == 0, Milliseconds = 7, Status = "fixture" };
        });
        Assert(summary.Completed == 20 && summary.Responsive == 10 && replies.Count == 20 && calls == 30 && peak <= 4, "bounded workers, retries and unique results");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel(); int sent = 0;
            var cancelled = await LanScanner.Run(cidr, 100, 4, 2, stop.Token, result => { }, (ip, timeout) => { sent++; return Task.FromResult(new LanProbe()); });
            Assert(cancelled.Cancelled && cancelled.Completed == 0 && sent == 0, "pre-cancel sends no packets");
        }
        using (var stop = new CancellationTokenSource(30))
        {
            int sent = 0;
            var cancelled = await LanScanner.Run(cidr, 100, 4, 2, stop.Token, result => { }, async (ip, timeout) => { Interlocked.Increment(ref sent); await Task.Delay(80); return new LanProbe { Status = "TimedOut" }; });
            Assert(cancelled.Cancelled && cancelled.Completed == 0 && sent <= 4, "cancel does not schedule more IPs or invent no-reply results");
        }
        return "PASS LAN range/mask parsing, /31-/32, limits and unsigned boundaries\r\nPASS LAN green/yellow/red boundaries; gray versus pending\r\nPASS LAN bounded concurrency, retries, complete/unique coverage\r\nPASS LAN cancellation before and during scanning\r\n";
    }
    internal static async Task<string> Live()
    {
        LanResult reply = null;
        var summary = await LanScanner.Run(LanRange.Parse("127.0.0.1/32"), 1000, 1, 2, CancellationToken.None, result => reply = result);
        Assert(summary.Completed == 1 && summary.Responsive == 1 && reply != null && reply.Probe.Success, "native Windows ICMP loopback reply");
        return "PASS Windows native Ping.SendPingAsync on 127.0.0.1; no full subnet scan performed.\r\n";
    }
}
