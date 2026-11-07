using System;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

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
