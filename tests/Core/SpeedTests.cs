using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class SpeedTests
{
    static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
    internal static async Task<string> CheckUniversities()
    {
        var report = new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
        foreach (string host in new[] { "test.ustc.edu.cn", "test.nju.edu.cn" })
        {
            using (var limit = new CancellationTokenSource(60000))
            {
                var o = new Options { Url = (host == "test.ustc.edu.cn" ? "http://" : "https://") + host + "/backend/garbage.php?ckSize=16", Mode = 0, Seconds = 2, Connections = 1 };
                try {
                    var r = await Engine.Run(o, limit.Token, (b, t) => { }, s => { });
                    Assert(r.Bytes > 0 && !r.Stopped, host + " live download");
                    report.AppendLine("PASS " + host + " native verification + latency + 2s download; bytes=" + r.Bytes);
                } catch (Exception ex) { report.AppendLine("FAIL " + host + ": " + ex.Message); }
            }
        }
        return report.ToString();
    }
    internal static async Task<string> Run()
    {
        var report = new StringBuilder();
        using (var server = new TestServer())
        {
            var o = new Options { Url = server.Url, Mode = 0, Seconds = 1, Connections = 3 };
            var r = await Engine.Run(o, CancellationToken.None, (b, t) => { }, s => { });
            Assert(r.Bytes > 0 && r.Seconds >= .8 && r.Seconds < 5 && r.Latency > 0 && !r.Stopped && r.Mbps > 0, "direct streaming / deadline / metrics");
            report.AppendLine("PASS direct streaming, parallel download, deadline, latency, Mbps");
            o.Mode = 2; o.Proxy = server.Url.Replace("/data", ""); o.Url = "http://proxy-test.invalid/data";
            r = await Engine.Run(o, CancellationToken.None, (b, t) => { }, s => { });
            Assert(server.ProxyRequests >= 6 && r.Bytes > 0, "explicit proxy routing"); report.AppendLine("PASS explicit HTTP proxy routing (unresolvable origin)");
            o.Mode = 0; o.Url = server.Url;
            using (var cancel = new CancellationTokenSource()) {
                cancel.CancelAfter(500); o.Seconds = 10;
                r = await Engine.Run(o, cancel.Token, (b, t) => { }, s => { });
                Assert(r.Stopped && r.Seconds < 3, "user cancellation");
            }
            report.AppendLine("PASS cancellation during streaming");
            o.Url = server.Url.Replace("/data", "/stall");
            using (var cancel = new CancellationTokenSource()) {
                cancel.CancelAfter(500);
                r = await Engine.Run(o, cancel.Token, (b, t) => { }, s => { });
                Assert(r.Stopped && r.Seconds < 2, "cancellation on stalled response body");
            }
            report.AppendLine("PASS cancellation on stalled response body");
            using (var cancel = new CancellationTokenSource()) {
                cancel.Cancel(); bool stopped = false;
                try { await Engine.Run(o, cancel.Token, (b, t) => { }, s => { }); } catch (OperationCanceledException) { stopped = true; }
                Assert(stopped, "cancel during probe");
            }
            report.AppendLine("PASS cancellation during latency probe");
            foreach (var path in new[] { "/error", "/html" }) {
                o.Url = server.Url.Replace("/data", path); bool failed = false;
                try { await Engine.Run(o, CancellationToken.None, (b, t) => { }, s => { }); } catch (Exception) { failed = true; }
                Assert(failed, path); report.AppendLine("PASS reject " + path);
            }
        }
        return report.ToString();
    }
}
