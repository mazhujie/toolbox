using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

internal static class IpTests
{
    static void Assert(bool value, string name) { if (!value) throw new Exception("FAIL IP: " + name); }
    internal static string Page(string ip, int score = 71, string connectionType = "", string datacenter = "Yes", string residentialProxy = "No")
    {
        return "<html><h1>" + ip + " Fraud Risk</h1><!-- <div class=\"score\">Fraud Score: 99</div> -->"
            + "<div class=\"panel_title high_risk\">" + (score == 0 ? "Low" : "High") + " Risk</div><div class=\"score\">Fraud Score: " + score + "</div>"
            + "<pre>\"is_blacklisted_external\": false</pre><table>"
            + "<tr><th>Connection type</th><td>" + WebUtility.HtmlEncode(connectionType) + "</td></tr>"
            + "<tr><th>Datacenter</th><td><div>" + datacenter + "</div></td></tr>"
            + "<tr><th>Anonymizing VPN</th><td>No</td></tr><tr><th>Tor Exit Node</th><td>No</td></tr>"
            + "<tr><th>Public Proxy</th><td>Yes</td></tr><tr><th>Web Proxy</th><td>No</td></tr><tr><th>Residential Proxy</th><td>" + residentialProxy + "</td></tr></table></html>";
    }
    internal static async Task<string> Run()
    {
        var zero = IpLookup.Parse("{\"ip\":\"203.0.113.8\",\"risk\":{\"risk_score\":55,\"is_vpn\":true}}", true);
        Assert(zero.Risk == null && zero.Vpn == null, "IPQuery risk is never used");
        IpLookup.ApplyScamalytics(zero, Page(zero.Ip, 0));
        Assert(zero.Risk == 0 && zero.Vpn == false && zero.Mobile == null && zero.Proxy == true && zero.Blacklisted == false, "Scamalytics zero, flags, missing fields");
        string[,] types = {
            { "n/a", "Yes", "No", "机房 IP" }, { "cable", "Yes", "Yes", "机房 IP" },
            { "residential", "No", "No", "住宅 IP" }, { " Cable ", "No", "No", "疑似住宅 IP" },
            { "dsl", "No", "No", "疑似住宅 IP" }, { "fiber", "No", "No", "疑似住宅 IP" },
            { "mobile", "No", "No", "移动网络 IP" }, { "cellular", "No", "No", "移动网络 IP" },
            { "n/a", "No", "Yes", "住宅代理 IP" }, { "n/a", "", "Yes", "住宅代理 IP" },
            { "n/a", "No", "No", "未知" }, { "corporate", "No", "No", "未知" },
            { "", "", "", "未知" }, { "cable", "", "No", "未知" }, { "not residential", "No", "No", "未知" }
        };
        for (int i = 0; i < types.GetLength(0); i++)
        {
            var info = new IpInfo { Ip = zero.Ip };
            IpLookup.ApplyScamalytics(info, Page(info.Ip, 99, types[i, 0], types[i, 1], types[i, 2]));
            Assert(info.NetworkType == types[i, 3] && info.Risk == 99, "network classification uses explicit evidence independently of risk: " + i);
        }
        var missingType = new IpInfo { Ip = zero.Ip };
        string noFields = "<h1>" + zero.Ip + " Fraud Risk</h1><div class=\"score\">Fraud Score: 0</div>";
        IpLookup.ApplyScamalytics(missingType, noFields + "<!-- <th>Datacenter</th><td>No</td><th>Connection type</th><td>residential</td> -->");
        Assert(missingType.NetworkType == "未知" && missingType.ConnectionType == null && missingType.ResidentialProxy == null, "missing and commented fields cannot imply residential");
        foreach (var html in new[] { Page("203.0.113.9"), Page(zero.Ip, 999), "<html>Verify you are human</html>", "<h1>203.0.113.8 Fraud Risk</h1><p>risk_score: 10</p>" })
        { bool failed = false; try { IpLookup.ApplyScamalytics(new IpInfo { Ip = zero.Ip }, html); } catch (IOException) { failed = true; } Assert(failed, "reject wrong IP, invalid score or challenge page"); }
        foreach (var json in new[] { "{\"ip\":\"invalid\"}", "{\"error\":\"rate limited\"}" })
        { bool failed = false; try { IpLookup.Parse(json, true); } catch (IOException) { failed = true; } Assert(failed, "reject malformed/error response"); }
        using (var server = new TestServer())
        {
            string root = server.Url.Replace("/data", "");
            var direct = await IpLookup.Query(new Options { Mode = 0 }, CancellationToken.None, root + "/ip-good", root + "/ip-basic", root + "/scam/");
            var route = new Options { Mode = 2, Proxy = root };
            var proxy = await IpLookup.Query(route, CancellationToken.None, "http://ip-test.invalid/ip-good", "http://ip-test.invalid/ip-basic", "http://risk-test.invalid/scam/");
            Assert(direct.Ip == "203.0.113.8" && proxy.Ip == "203.0.113.9" && direct.Risk == 71 && proxy.Risk == 71 && proxy.Proxy == true, "independent direct/proxy routes, Scamalytics score instead of IPQuery score");
            var previousProxy = WebRequest.DefaultWebProxy;
            try
            {
                // Only this test process changes; Windows proxy settings are untouched.
                WebRequest.DefaultWebProxy = new WebProxy(root, false);
                using (var limit = new CancellationTokenSource(5000))
                {
                    var system = await IpLookup.Query(new Options { Mode = 1 }, limit.Token,
                        "http://ip-test.invalid/ip-good", "http://ip-test.invalid/ip-basic", "http://risk-test.invalid/scam/");
                    Assert(system.Ip == "203.0.113.9" && system.Risk == 71, "system proxy handles both IP and Scamalytics requests");
                }
            }
            finally { WebRequest.DefaultWebProxy = previousProxy; }
            int before = server.ProxyRequests;
            var partial = await IpLookup.Query(route, CancellationToken.None, "http://ip-test.invalid/ip-error", "http://ip-test.invalid/ip-basic", "http://risk-test.invalid/scam/");
            Assert(partial.Ip == "203.0.113.9" && partial.Risk == 71 && partial.Provider == "ipify" && server.ProxyRequests == before + 3, "fallback retains proxy and queries Scamalytics for exact IP");
            foreach (var path in new[] { "/scam-error/", "/scam-bad/" })
            {
                var failed = await IpLookup.Query(route, CancellationToken.None, "http://ip-test.invalid/ip-good", "http://ip-test.invalid/ip-basic", "http://risk-test.invalid" + path);
                Assert(failed.Ip == "203.0.113.9" && failed.Risk == null && failed.Proxy == null && failed.RiskProvider == "Scamalytics" && failed.NetworkType == "未知" && failed.ConnectionType == null && failed.ResidentialProxy == null, "risk failure keeps IP with unknown type and never substitutes IPQuery score");
            }
            using (var cancel = new CancellationTokenSource(200))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew(); bool stopped = false;
                before = server.ProxyRequests;
                try { await IpLookup.Query(route, cancel.Token, "http://ip-test.invalid/ip-stall", "http://ip-test.invalid/ip-basic", "http://risk-test.invalid/scam/"); }
                catch (OperationCanceledException) { stopped = true; }
                Assert(stopped && watch.Elapsed.TotalSeconds < 2 && server.ProxyRequests == before + 1, "cancellation interrupts body and skips fallback");
            }
        }
        return "PASS Scamalytics score/flags, zero and missing values\r\nPASS IP classification: hosting, residential, inferred broadband, residential proxy, mobile and unknown; non-datacenter is not automatically residential\r\nPASS reject wrong-IP, challenge and invalid-score pages\r\nPASS separate routes and same-route IP fallback plus Scamalytics lookup\r\nPASS system proxy routes both IP discovery and risk lookup (process-local proxy fixture)\r\nPASS risk failures keep IP with unknown risk/type; no IPQuery score substitution\r\nPASS IP cancellation with stalled response\r\n";
    }
    internal static async Task<string> Live()
    {
        using (var cancel = new CancellationTokenSource(45000))
        {
            var info = await IpLookup.Query(new Options { Mode = 0 }, cancel.Token);
            Assert(!String.IsNullOrEmpty(info.Ip), "live public IP");
            Assert(info.RiskProvider == "Scamalytics" && info.Risk.HasValue, "live Scamalytics report: " + info.Warning);
            return "PASS live direct public IP and Scamalytics Fraud Score for that IP; IP omitted from report.\r\n";
        }
    }
}
