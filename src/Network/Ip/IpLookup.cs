using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class IpInfo
{
    public string Ip, Location, Isp, Asn, Provider, Warning, RiskLevel, ConnectionType;
    public string RiskProvider = "Scamalytics";
    public double? Risk;
    public bool? Vpn, Proxy, Tor, Datacenter, Mobile, Blacklisted, ResidentialProxy;
    public DateTime CheckedAt;
    internal string NetworkType
    {
        get
        {
            if (Datacenter == true) return "机房 IP";
            if (ResidentialProxy == true) return "住宅代理 IP";
            // A negative datacenter flag alone does not establish a residential connection.
            if (Datacenter == false)
            {
                switch ((ConnectionType ?? "").Trim().ToLowerInvariant())
                {
                    case "residential": return "住宅 IP";
                    case "cable": case "dsl": case "cable/dsl": case "fiber": case "fttx": return "疑似住宅 IP";
                    case "mobile": case "cellular": return "移动网络 IP";
                }
            }
            return "未知";
        }
    }
}

internal static class IpLookup
{
    internal const string Primary = "https://api.ipquery.io/?format=json";
    internal const string Fallback = "https://api.ipify.org?format=json";
    internal const string RiskBase = "https://scamalytics.com/ip/";
    static object Value(Dictionary<string, object> d, string key)
    { object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
    static string Text(Dictionary<string, object> d, string key)
    {
        var text = Value(d, key) as string;
        return String.IsNullOrWhiteSpace(text) ? "" : text.Replace("\r", " ").Replace("\n", " ").Substring(0, Math.Min(200, text.Length));
    }
    internal static IpInfo Parse(string json, bool full)
    {
        var d = new JavaScriptSerializer { MaxJsonLength = 131072 }.Deserialize<Dictionary<string, object>>(json);
        if (d == null || Value(d, "error") != null) throw new IOException("查询服务返回错误。");
        string raw = Text(d, "ip"); IPAddress ip;
        if (!IPAddress.TryParse(raw, out ip) || (ip.AddressFamily != AddressFamily.InterNetwork && ip.AddressFamily != AddressFamily.InterNetworkV6))
            throw new IOException("查询服务没有返回有效 IP。");
        var result = new IpInfo { Ip = ip.ToString(), Provider = full ? "IPQuery" : "ipify", CheckedAt = DateTime.Now };
        if (!full) return result;
        var location = Value(d, "location") as Dictionary<string, object>;
        var isp = Value(d, "isp") as Dictionary<string, object>;
        result.Location = String.Join(" / ", new[] { Text(location, "country"), Text(location, "state"), Text(location, "city") }.Where(s => s.Length > 0).Distinct());
        result.Isp = Text(isp, "isp"); if (result.Isp.Length == 0) result.Isp = Text(isp, "org");
        result.Asn = Text(isp, "asn");
        // IPQuery supplies the address and network metadata only. Its risk data is never used.
        return result;
    }
    static Match Match(string input, string pattern)
    { return Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1)); }
    static string Plain(string html)
    {
        string value = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(1)));
        return Regex.Replace(value, "\\s+", " ").Trim();
    }
    static string Cell(string html, string name)
    {
        var m = Match(html, "<th(?:\\s[^>]*)?>\\s*" + Regex.Escape(name) + "\\s*</th>\\s*<td(?:\\s[^>]*)?>(.*?)</td>");
        return m.Success ? Plain(m.Groups[1].Value) : "";
    }
    static bool? YesNo(string value)
    {
        if (String.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (String.Equals(value, "No", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }
    internal static void ApplyScamalytics(IpInfo info, string html)
    {
        html = Regex.Replace(html, "<!--.*?-->|<script\\b[^>]*>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        var heading = Match(html, "<h1(?:\\s[^>]*)?>(.*?)</h1>");
        string title = heading.Success ? Plain(heading.Groups[1].Value) : "";
        var address = Match(title, "^(.+?)\\s+Fraud Risk$"); IPAddress pageIp, expected;
        if (!address.Success || !IPAddress.TryParse(address.Groups[1].Value, out pageIp) || !IPAddress.TryParse(info.Ip, out expected) || !pageIp.Equals(expected))
            throw new IOException("Scamalytics 未返回该 IP 的有效报告，可能需要验证或页面格式已变化。");
        var score = Match(html, "<div\\b[^>]*class=[\"']score[\"'][^>]*>\\s*Fraud Score:\\s*(\\d+)\\s*</div>");
        int number;
        if (!score.Success || !Int32.TryParse(score.Groups[1].Value, out number) || number < 0 || number > 100)
            throw new IOException("Scamalytics 未提供有效 Fraud Score，风险分未知。");
        info.Risk = number; info.RiskProvider = "Scamalytics";
        var level = Match(html, "<div\\b[^>]*class=[\"']panel_title[^\"']*[\"'][^>]*>\\s*(Low|Medium|High|Very High) Risk\\s*</div>");
        if (level.Success)
        {
            var labels = new Dictionary<string, string> { { "low", "低" }, { "medium", "中" }, { "high", "高" }, { "very high", "很高" } };
            info.RiskLevel = labels[level.Groups[1].Value.ToLowerInvariant()];
        }
        info.Datacenter = YesNo(Cell(html, "Datacenter")); info.Vpn = YesNo(Cell(html, "Anonymizing VPN")); info.Tor = YesNo(Cell(html, "Tor Exit Node"));
        string connection = Cell(html, "Connection type");
        info.ConnectionType = String.IsNullOrEmpty(connection) || String.Equals(connection, "n/a", StringComparison.OrdinalIgnoreCase) ? null : connection.Substring(0, Math.Min(200, connection.Length));
        info.ResidentialProxy = YesNo(Cell(html, "Residential Proxy"));
        var proxies = new[] { YesNo(Cell(html, "Public Proxy")), YesNo(Cell(html, "Web Proxy")), info.ResidentialProxy };
        info.Proxy = proxies.Any(v => v == true) ? true : proxies.All(v => v == false) ? (bool?)false : null;
        var blacklist = Match(html, "[\"']is_blacklisted_external[\"']\\s*:\\s*(true|false)\\b");
        if (blacklist.Success) info.Blacklisted = blacklist.Groups[1].Value.ToLowerInvariant() == "true";
    }
    static async Task<string> Get(HttpClient client, string url, CancellationToken user)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(user))
        {
            timeout.CancelAfter(12000);
            try
            {
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 429) throw new IOException("查询服务限流，请稍后重试。");
                    if ((int)response.StatusCode == 403) throw new IOException("查询服务拒绝访问或要求验证，请稍后重试。");
                    response.EnsureSuccessStatusCode();
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (timeout.Token.Register(() => stream.Dispose()))
                    using (var buffer = new MemoryStream())
                    {
                        byte[] chunk = new byte[4096]; int count;
                        while ((count = await stream.ReadAsync(chunk, 0, chunk.Length, timeout.Token).ConfigureAwait(false)) > 0)
                        {
                            if (buffer.Length + count > 131072) throw new IOException("查询服务响应过大。");
                            buffer.Write(chunk, 0, count);
                        }
                        return Encoding.UTF8.GetString(buffer.ToArray());
                    }
                }
            }
            catch (Exception)
            {
                if (user.IsCancellationRequested) throw new OperationCanceledException(user);
                if (timeout.IsCancellationRequested) throw new IOException("查询超时。");
                throw;
            }
        }
    }
    static async Task<IpInfo> Discover(Options route, CancellationToken token, string primary, string fallback)
    {
        using (var client = Engine.Client(route))
        {
            string failure;
            try { return Parse(await Get(client, primary, token).ConfigureAwait(false), true); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failure = ex.Message; }
            token.ThrowIfCancellationRequested();
            try
            {
                var result = Parse(await Get(client, fallback, token).ConfigureAwait(false), false);
                result.Warning = "IPQuery 不可用（" + failure + "），已通过 ipify 取得出口 IP。";
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new IOException("无法查询出口 IP。请检查当前网络或代理设置。IPQuery：" + failure); }
        }
    }
    internal static async Task<IpInfo> Query(Options route, CancellationToken token, string primary = Primary, string fallback = Fallback, string riskBase = RiskBase)
    {
        var info = await Discover(route, token, primary, fallback).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using (var client = Engine.Client(route))
        {
            try { ApplyScamalytics(info, await Get(client, riskBase + Uri.EscapeDataString(info.Ip), token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                info.Risk = null; info.RiskLevel = info.ConnectionType = null; info.Vpn = info.Proxy = info.Tor = info.Datacenter = info.Mobile = info.Blacklisted = info.ResidentialProxy = null;
                info.Warning = (info.Warning ?? "") + "\r\nScamalytics 查询失败：" + ex.Message + " 风险分保持未知。";
            }
        }
        info.CheckedAt = DateTime.Now;
        return info;
    }
}
