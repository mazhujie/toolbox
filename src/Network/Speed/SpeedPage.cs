using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class SpeedPage : UserControl
{
    readonly ComboBox source = new ComboBox(), mode = new ComboBox();
    readonly TextBox url = new TextBox(), proxy = new TextBox(), log = new TextBox();
    readonly NumericUpDown seconds = new NumericUpDown(), connections = new NumericUpDown();
    readonly Label speed = new Label(), detail = new Label(), status = new Label();
    readonly Button start = new Button(), stop = new Button(), export = new Button();
    readonly ProgressBar bar = new ProgressBar();
    CancellationTokenSource running;
    readonly List<string[]> history = new List<string[]>();
    long previousBytes; double previousTime;
    readonly Action<bool> setBusy;
    internal Button[] Actions { get { return new[] { start, stop, export }; } }
    internal SpeedPage(Action<bool> busy)
    {
        setBusy = busy; AutoScroll = true;
        Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.FromArgb(245, 247, 251);
        var title = new Label { Text = "网络测速", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), AutoSize = true, Location = new Point(24, 18) };
        Controls.Add(title);
        AddLabel("测速地址", 24, 82); source.SetBounds(120, 78, 650, 30); source.DropDownStyle = ComboBoxStyle.DropDownList;
        source.Items.AddRange(new object[] { "国内 · 阿里云开源镜像", "国内 · 华为云开源镜像", "国内 · 中科大测速网", "国内 · 南京大学测速网", "海外 · Cloudflare", "海外 · OVH 备用", "自定义下载地址" });
        Controls.Add(source); url.SetBounds(120, 117, 650, 30); Controls.Add(url);
        source.SelectedIndexChanged += delegate {
            string[] urls = { "https://mirrors.aliyun.com/ubuntu/ls-lR.gz", "https://mirrors.huaweicloud.com/ubuntu/ls-lR.gz", "http://test.ustc.edu.cn/backend/garbage.php?ckSize=16", "https://test.nju.edu.cn/backend/garbage.php?ckSize=16", "https://speed.cloudflare.com/__down?bytes=10000000", "https://proof.ovh.net/files/100Mb.dat" };
            if (source.SelectedIndex >= 0 && source.SelectedIndex < urls.Length) url.Text = urls[source.SelectedIndex];
        };
        source.SelectedIndex = 0;
        AddLabel("连接方式", 24, 166); mode.SetBounds(120, 162, 225, 30); mode.DropDownStyle = ComboBoxStyle.DropDownList;
        mode.Items.AddRange(new object[] { "直连（不使用 HTTP 代理）", "跟随 Windows 系统代理", "手动 HTTP / 混合端口" });
        Controls.Add(mode); proxy.SetBounds(360, 162, 410, 30); proxy.Text = "http://127.0.0.1:7890"; Controls.Add(proxy);
        mode.SelectedIndexChanged += delegate { proxy.Enabled = mode.SelectedIndex == 2; }; mode.SelectedIndex = 0;
        AddLabel("下载时长", 24, 212); seconds.SetBounds(120, 207, 90, 30); seconds.Minimum = 3; seconds.Maximum = 60; seconds.Value = 10; Controls.Add(seconds);
        AddLabel("秒", 218, 212); AddLabel("并发连接", 310, 212); connections.SetBounds(405, 207, 85, 30); connections.Minimum = 1; connections.Maximum = 8; connections.Value = 4; Controls.Add(connections);
        var note = new Label { Text = "手动模式使用 HTTP 或混合端口，不支持纯 SOCKS5。TUN/VPN 可能接管直连流量。\n测速会消耗流量；结果代表当前地址的下载能力，不包含上传测速。", AutoSize = false, ForeColor = Color.DimGray };
        note.SetBounds(24, 253, 752, 48); Controls.Add(note);
        speed.Text = "— Mbps"; speed.Font = new Font(Font.FontFamily, 36, FontStyle.Bold); speed.ForeColor = Color.FromArgb(35, 96, 207); speed.SetBounds(24, 308, 740, 75); Controls.Add(speed);
        detail.Text = "实时下载速度 · 1 MB/s = 8 Mbps"; detail.SetBounds(28, 389, 742, 27); Controls.Add(detail);
        bar.SetBounds(28, 425, 742, 8); bar.Maximum = 1000; Controls.Add(bar);
        start.Text = "开始测速"; start.SetBounds(28, 453, 150, 40); start.BackColor = Color.FromArgb(35, 96, 207); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat;
        stop.Text = "停止"; stop.SetBounds(192, 453, 100, 40); stop.Enabled = false;
        export.Text = "导出 CSV"; export.SetBounds(650, 453, 120, 40);
        Controls.AddRange(new Control[] { start, stop, export });
        status.Text = "就绪。可用同一地址分别测直连和代理，方便比较。"; status.SetBounds(28, 453, 742, 30); Controls.Add(status);
        log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.SetBounds(28, 492, 742, 164); Controls.Add(log);
        start.Click += async delegate { await StartTest(); }; stop.Click += delegate { if (running != null) running.Cancel(); };
        export.Click += delegate { Export(); };
    }
    internal void Cancel() { if (running != null) running.Cancel(); }
    void AddLabel(string text, int x, int y) { var l = new Label { Text = text, AutoSize = true, Location = new Point(x, y) }; Controls.Add(l); }
    void Ui(Action action) { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
    internal void SetExternalBusy(bool yes)
    {
        foreach (Control c in new Control[] { source, mode, url, seconds, connections, start }) c.Enabled = !yes;
        proxy.Enabled = !yes && mode.SelectedIndex == 2; stop.Enabled = running != null;
    }
    async Task StartTest()
    {
        Uri address;
        if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out address) || (address.Scheme != "http" && address.Scheme != "https") || address.UserInfo.Length > 0)
        { MessageBox.Show("请输入有效的 HTTP/HTTPS 文件地址，不要包含账号密码。"); return; }
        Uri p;
        if (mode.SelectedIndex == 2 && (!Uri.TryCreate(proxy.Text.Trim(), UriKind.Absolute, out p) || p.Scheme != "http" || p.UserInfo.Length > 0 || p.AbsolutePath != "/" || p.Query.Length > 0 || p.Fragment.Length > 0))
        { MessageBox.Show("代理格式：http://127.0.0.1:7890；请填写 HTTP 或混合端口。此版本不支持代理认证。"); return; }
        var o = new Options { Url = address.AbsoluteUri, Mode = mode.SelectedIndex, Proxy = proxy.Text.Trim(), Seconds = (int)seconds.Value, Connections = (int)connections.Value };
        var route = mode.Text; var chosenSource = source.Text;
        running = new CancellationTokenSource(); setBusy(true); bar.Value = 0; previousBytes = 0; previousTime = 0; speed.Text = "准备中…";
        try
        {
            var r = await Engine.Run(o, running.Token, (bytes, elapsed) => Ui(() => {
                double instant = (bytes - previousBytes) * 8.0 / Math.Max(.001, elapsed - previousTime) / 1000000;
                previousBytes = bytes; previousTime = elapsed;
                speed.Text = instant.ToString("F2") + " Mbps";
                detail.Text = String.Format("{0:F2} MB/s  ·  已下载 {1:F1} MB  ·  {2:F1} / {3} 秒", instant / 8, bytes / 1000000.0, elapsed, o.Seconds);
                bar.Value = Math.Min(1000, (int)(elapsed / o.Seconds * 1000));
            }), text => Ui(() => status.Text = text));
            if (IsDisposed) return;
            speed.Text = r.Mbps.ToString("F2") + " Mbps";
            detail.Text = String.Format("平均 {0:F2} MB/s  ·  首字节 {1:F0} ms  ·  已下载 {2:F1} MB  ·  {3:F1} 秒", r.Mbps / 8, r.Latency, r.Bytes / 1000000.0, r.Seconds);
            status.Text = r.Stopped ? "已停止，显示停止前的平均值。" : "测速完成（平均值含连接开销）。";
            if (!r.Stopped) bar.Value = 1000;
            var row = new[] { DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), chosenSource, o.Url, route, o.Mode == 2 ? o.Proxy : "", o.Connections.ToString(), r.Seconds.ToString("F2", CultureInfo.InvariantCulture), r.Mbps.ToString("F3", CultureInfo.InvariantCulture), r.Latency.ToString("F1", CultureInfo.InvariantCulture), r.Bytes.ToString(), r.Stopped ? "停止" : "完成" };
            history.Add(row); log.AppendText(row[0] + "  " + route + "  " + r.Mbps.ToString("F2") + " Mbps  " + row[10] + Environment.NewLine);
        }
        catch (OperationCanceledException) { if (!IsDisposed) { status.Text = "已取消。"; speed.Text = "— Mbps"; } }
        catch (Exception ex) { if (!IsDisposed) { speed.Text = "测速失败"; status.Text = "请检查地址或代理，可切换测速源重试。"; log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + ex.GetBaseException().Message + Environment.NewLine); } }
        finally { var old = running; running = null; old.Dispose(); if (!IsDisposed) setBusy(false); }
    }
    void Export()
    {
        if (history.Count == 0) { MessageBox.Show("请先完成一次测速。"); return; }
        using (var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "测速结果-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" })
        if (dialog.ShowDialog() == DialogResult.OK)
        try {
            var lines = new List<string> { "时间,测速源,地址,连接方式,代理,并发数,下载秒数,平均Mbps,首字节延迟ms,下载字节,状态" };
            lines.AddRange(history.Select(row => String.Join(",", row.Select(Csv))));
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
            status.Text = "测速记录已导出。";
        } catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message); }
    }
    static string Csv(string s) { if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
}
