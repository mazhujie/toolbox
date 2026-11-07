using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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
using System.Windows.Forms;

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

internal sealed class IpCard : Panel
{
    readonly TextBox address = new TextBox(), details = new TextBox();
    readonly Label score = new Label(), networkType = new Label();
    public IpCard(string name)
    {
        Size = new Size(360, 396); BackColor = Color.White; BorderStyle = BorderStyle.FixedSingle;
        var title = new Label { Text = name, Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold), Location = new Point(14, 14), Size = new Size(328, 28) };
        address.SetBounds(14, 52, 328, 58); address.Multiline = true; address.ReadOnly = true; address.BorderStyle = BorderStyle.None;
        address.BackColor = Color.White; address.Font = new Font("Segoe UI", 17, FontStyle.Bold); address.Text = "—";
        score.SetBounds(14, 120, 328, 30); score.Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold); score.ForeColor = Color.FromArgb(35, 96, 207);
        networkType.SetBounds(14, 154, 328, 28); networkType.Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold);
        details.SetBounds(14, 194, 328, 184); details.Multiline = true; details.ReadOnly = true; details.BorderStyle = BorderStyle.None;
        details.BackColor = Color.White; details.ScrollBars = ScrollBars.Vertical; details.Font = new Font("Microsoft YaHei UI", 10);
        Controls.AddRange(new Control[] { title, address, score, networkType, details }); Reset("点击“查询出口 IP”后显示结果。");
    }
    internal void Reset(string message) { address.Text = "—"; score.Text = "Scamalytics：未知"; networkType.Text = "IP 类型：未知"; details.Text = message; }
    static string Known(string value) { return String.IsNullOrEmpty(value) ? "未知" : value; }
    static string Flag(bool? value) { return value.HasValue ? (value.Value ? "已标记" : "未标记") : "未知"; }
    internal void ShowInfo(IpInfo info)
    {
        address.Text = info.Ip;
        networkType.Text = "IP 类型：" + info.NetworkType;
        score.Text = info.Risk.HasValue ? "Scamalytics：" + info.Risk.Value.ToString("0.##") + " / 100" : "Scamalytics：未知";
        details.Text = "类型依据：Scamalytics（公开网页）"
            + "\r\n连接类型：" + Known(info.ConnectionType) + "    住宅代理：" + Flag(info.ResidentialProxy)
            + "\r\n归属地：" + Known(info.Location) + "\r\n运营商：" + Known(info.Isp) + "\r\nASN：" + Known(info.Asn)
            + "\r\n风险等级：" + Known(info.RiskLevel) + "    机房：" + Flag(info.Datacenter)
            + "\r\nVPN：" + Flag(info.Vpn) + "    代理：" + Flag(info.Proxy) + "\r\nTor：" + Flag(info.Tor)
            + "\r\n外部黑名单：" + Flag(info.Blacklisted)
            + "\r\n风险来源：" + info.RiskProvider + "（公开网页）"
            + "\r\nIP / 归属地：" + info.Provider + "   " + info.CheckedAt.ToString("HH:mm:ss")
            + (String.IsNullOrEmpty(info.Warning) ? "" : "\r\n" + info.Warning);
    }
}

internal sealed class IpPage : UserControl
{
    readonly Action<bool> setBusy;
    readonly IpCard direct = new IpCard("直连出口"), proxied = new IpCard("代理出口");
    readonly Button query = new Button(), cancel = new Button(), settings = new Button();
    internal Button[] Actions { get { return new[] { query, cancel, settings }; } }
    readonly Label routeLabel = new Label(), summary = new Label(), proxyLabel = new Label(), note = new Label();
    readonly TextBox proxy = new TextBox();
    readonly ContextMenuStrip routeMenu = new ContextMenuStrip();
    readonly string[] routeNames = { "直连", "系统代理", "手动代理" };
    int routeMode = 1;
    CancellationTokenSource running;
    bool speedBusy;
    public IpPage(Action<bool> busy)
    {
        setBusy = busy; BackColor = Color.FromArgb(245, 247, 251); AutoScroll = true;
        Controls.Add(new Label { Text = "出口 IP 与风险参考", Font = new Font("Microsoft YaHei UI", 21, FontStyle.Bold), Location = new Point(24, 20), Size = new Size(600, 40) });
        routeLabel.SetBounds(24, 72, 744, 28); Controls.Add(routeLabel);
        query.Text = "查询出口 IP"; query.SetBounds(24, 112, 160, 38); query.BackColor = Color.FromArgb(35, 96, 207); query.ForeColor = Color.White; query.FlatStyle = FlatStyle.Flat;
        cancel.Text = "停止查询"; cancel.SetBounds(198, 112, 110, 38); cancel.Enabled = false;
        for (int i = 0; i < routeNames.Length; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(routeNames[i]);
            item.Click += delegate { SelectRoute(index); };
            routeMenu.Items.Add(item);
        }
        settings.Click += delegate { routeMenu.Show(settings, new Point(settings.Width + 6, 0)); };
        proxyLabel.Text = "代理地址"; proxyLabel.SetBounds(24, 112, 90, 26);
        proxy.Text = "http://127.0.0.1:7890"; proxy.SetBounds(114, 108, 654, 28);
        proxy.TextChanged += delegate { RefreshRoute(); };
        Controls.AddRange(new Control[] { proxyLabel, proxy });
        Controls.AddRange(new Control[] { query, cancel, settings });
        direct.Location = new Point(24, 126); proxied.Location = new Point(408, 126); Controls.AddRange(new Control[] { direct, proxied });
        summary.SetBounds(24, 506, 744, 42); summary.Text = "选择系统或手动代理后，可同时查询直连和代理出口。"; Controls.Add(summary);
        note.Text = "风险分取自 Scamalytics 网页的 Fraud Score（0–100，越高风险越高）。\nIP 类型依据该站标记；宽带类型仅推测住宅，未标记机房不等于住宅。\n遇到验证、限流或数据缺失时显示未知；住宅 IP 不代表低风险。\n分流可能改变出口；直连仅禁用 HTTP 代理，TUN / VPN 仍可能接管流量。";
        note.ForeColor = Color.DimGray;
        note.SetBounds(24, 558, 744, 88); Controls.Add(note);
        query.Click += async delegate { await QueryBoth(); }; cancel.Click += delegate { Cancel(); };
        RefreshRoute();
    }

    internal void SetSpeedBusy(bool busy) { speedBusy = busy; UpdateControls(); }

    void SelectRoute(int index)
    {
        if (speedBusy || running != null) return;
        routeMode = index;
        RefreshRoute();
        if (index == 2) proxy.Focus();
    }

    void RefreshRoute()
    {
        bool manual = routeMode == 2;
        settings.Text = "连接方式：" + routeNames[routeMode] + " ▾";
        for (int i = 0; i < routeMenu.Items.Count; i++)
            ((ToolStripMenuItem)routeMenu.Items[i]).Checked = i == routeMode;
        routeLabel.Text = routeMode == 0 ? "连接方式：直连，本次仅查询左侧出口。"
            : manual ? "连接方式：手动代理，在下方填写 HTTP / 混合端口。"
            : "连接方式：跟随 Windows 系统代理，同时查询直连和代理出口。";
        proxy.Visible = proxyLabel.Visible = manual;
        direct.Top = proxied.Top = manual ? 166 : 126;
        summary.Top = direct.Bottom + 20;
        note.Top = summary.Bottom + 10;
        proxied.Reset(routeMode == 0 ? "当前仅查询直连，可用左侧连接方式切换到代理。" : "点击“查询出口 IP”，对比直连和所选代理出口。");
        summary.Text = "结果是查询服务看到的出口 IP，可选中文字复制。修改连接方式后请重新查询。";
        UpdateControls();
    }

    Options ReadRoute() { return new Options { Mode = routeMode, Proxy = proxy.Text.Trim() }; }

    void UpdateControls()
    {
        bool editable = !speedBusy && running == null;
        query.Enabled = settings.Enabled = editable;
        proxy.Enabled = editable && routeMode == 2;
        cancel.Enabled = running != null;
    }
    internal void Cancel() { if (running != null) running.Cancel(); }
    async Task<IpInfo> Fetch(IpCard card, Options route, CancellationToken token)
    {
        try { var info = await IpLookup.Query(route, token); if (!IsDisposed) card.ShowInfo(info); return info; }
        catch (OperationCanceledException) { if (!IsDisposed) card.Reset("已取消查询。"); return null; }
        catch (Exception ex) { if (!IsDisposed) card.Reset(ex.Message); return null; }
    }
    async Task QueryBoth()
    {
        if (running != null || speedBusy) return;
        var route = ReadRoute(); Uri p;
        if (route.Mode == 2 && (!Uri.TryCreate(route.Proxy, UriKind.Absolute, out p) || p.Scheme != "http" || p.UserInfo.Length > 0 || p.AbsolutePath != "/" || p.Query.Length > 0 || p.Fragment.Length > 0))
        { summary.Text = "代理格式应为 http://127.0.0.1:7890，请先修改代理设置。"; return; }
        running = new CancellationTokenSource(); var current = running;
        query.Enabled = false; cancel.Enabled = true; setBusy(true);
        direct.Reset("正在查询直连出口和风险信息…");
        proxied.Reset(route.Mode == 0 ? "未选择代理模式，本次仅查询直连。" : "正在查询所选代理的出口和风险信息…");
        summary.Text = "查询中；两路独立请求，失败的线路会显示错误。";
        try
        {
            var a = Fetch(direct, new Options { Mode = 0 }, current.Token);
            var b = route.Mode == 0 ? Task.FromResult<IpInfo>(null) : Fetch(proxied, route, current.Token);
            await Task.WhenAll(a, b);
            if (IsDisposed) return;
            if (current.IsCancellationRequested) summary.Text = "查询已停止。";
            else if (a.Result != null && b.Result != null)
            {
                var left = IPAddress.Parse(a.Result.Ip); var right = IPAddress.Parse(b.Result.Ip);
                summary.Text = left.Equals(right) ? "两路返回相同出口；可能受分流、共同出口或 TUN 影响，不能仅凭此判断代理失效。"
                    : left.AddressFamily != right.AddressFamily ? "两路返回了不同协议的地址（IPv4 / IPv6），不能直接比较出口是否相同。" : "两路出口不同。结果仅代表访问 IP 查询服务时的出口。";
            }
            else summary.Text = route.Mode == 0 ? "直连查询结束。选择系统或手动代理后，可同时查询两路。" : "查询结束；未取得的 IP 或风险分显示为未知。";
        }
        finally
        {
            running = null; current.Dispose();
            if (!IsDisposed) { setBusy(false); UpdateControls(); }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) routeMenu.Dispose();
        base.Dispose(disposing);
    }
}

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
