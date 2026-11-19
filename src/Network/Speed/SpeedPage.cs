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
    bool externalBusy, syncingUrl, stopping;
    static readonly string[] urls = {
        "https://mirrors.aliyun.com/ubuntu/ls-lR.gz", "https://mirrors.huaweicloud.com/ubuntu/ls-lR.gz",
        "http://test.ustc.edu.cn/backend/garbage.php?ckSize=16", "https://test.nju.edu.cn/backend/garbage.php?ckSize=16",
        "https://speed.cloudflare.com/__down?bytes=10000000", "https://proof.ovh.net/files/100Mb.dat"
    };
    internal SpeedPage(Action<bool> busy)
    {
        setBusy = busy;
        var layout = UiTheme.Page(this);
        UiTheme.Heading(layout, "下载测速", UiTheme.Button(start, "开始测速", true),
            UiTheme.Button(stop, "停止", warning: true), UiTheme.Button(export, "导出 CSV"));
        var inputs = UiTheme.Card(true);
        source.DropDownStyle = ComboBoxStyle.DropDownList;
        source.Items.AddRange(new object[] { "国内 · 阿里云开源镜像", "国内 · 华为云开源镜像", "国内 · 中科大测速网", "国内 · 南京大学测速网", "海外 · Cloudflare", "海外 · OVH 备用", "自定义下载地址" });
        source.SelectedIndexChanged += delegate {
            if (source.SelectedIndex < 0 || source.SelectedIndex >= urls.Length) return;
            syncingUrl = true;
            url.Text = urls[source.SelectedIndex];
            syncingUrl = false;
        };
        url.TextChanged += delegate {
            if (!syncingUrl && source.SelectedIndex < urls.Length) source.SelectedIndex = urls.Length;
        };
        source.SelectedIndex = 0;
        UiTheme.Combo(mode, 235, new[] { "直连（不使用 HTTP 代理）", "跟随 Windows 系统代理", "手动 HTTP / 混合端口" }, 0);
        proxy.Text = "http://127.0.0.1:7890";
        mode.SelectedIndexChanged += delegate { UpdateControls(); };
        UiTheme.Number(seconds, 85, 3, 60, 10);
        UiTheme.Number(connections, 85, 1, 8, 4);
        UiTheme.Add(inputs, UiTheme.Row(source, UiTheme.Label("测速来源"), source));
        UiTheme.Add(inputs, UiTheme.Row(url, UiTheme.Label("下载地址"), url));
        UiTheme.Add(inputs, UiTheme.Row(proxy, UiTheme.Label("连接方式"), mode, UiTheme.Label("代理地址"), proxy));
        UiTheme.Add(inputs, UiTheme.Row(null, UiTheme.Label("下载时长"), seconds, UiTheme.Label("秒     并发连接"), connections));
        UiTheme.Add(layout, inputs);

        var metric = UiTheme.Card(true);
        speed.Text = "— Mbps"; speed.Font = UiTheme.Metric; speed.ForeColor = UiTheme.Blue; speed.Height = 67;
        detail.Text = "实时下载速度 · 1 MB/s = 8 Mbps";
        UiTheme.Status(detail);
        bar.Height = 8; bar.Maximum = 1000;
        UiTheme.Add(metric, speed);
        UiTheme.Add(metric, detail);
        UiTheme.Add(metric, bar);
        UiTheme.Add(layout, metric);
        status.Text = "就绪。可用同一地址分别测直连和代理，方便比较。";
        status.AutoSize = true; status.ForeColor = UiTheme.Muted;
        UiTheme.Add(layout, status);
        log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.BackColor = Color.White; log.Font = UiTheme.Mono;
        UiTheme.Add(layout, log, true);
        var note = UiTheme.Label("测速会消耗流量。手动代理支持 HTTP / 混合端口；TUN / VPN 可能接管直连流量。");
        note.ForeColor = UiTheme.Muted;
        UiTheme.Add(layout, note);
        start.Click += async delegate { await StartTest(); };
        stop.Click += delegate { Cancel(); };
        export.Click += delegate { Export(); };
        UpdateControls();
    }

    internal void Cancel()
    {
        if (running == null || stopping) return;
        stopping = true;
        status.Text = "正在停止…";
        UpdateControls();
        running.Cancel();
    }

    void Ui(Action action) { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
    internal void SetExternalBusy(bool busy) { externalBusy = busy; UpdateControls(); }

    void UpdateControls()
    {
        bool editable = running == null && !externalBusy;
        foreach (Control control in new Control[] { source, mode, url, seconds, connections, start }) control.Enabled = editable;
        proxy.Enabled = editable && mode.SelectedIndex == 2;
        stop.Enabled = running != null && !stopping;
    }

    async Task StartTest()
    {
        if (running != null || externalBusy) return;
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
            }), text => Ui(() => { if (!stopping) status.Text = text; }));
            if (IsDisposed) return;
            speed.Text = r.Mbps.ToString("F2") + " Mbps";
            detail.Text = String.Format("平均 {0:F2} MB/s  ·  首字节 {1:F0} ms  ·  已下载 {2:F1} MB  ·  {3:F1} 秒", r.Mbps / 8, r.Latency, r.Bytes / 1000000.0, r.Seconds);
            status.Text = r.Stopped ? "已停止，显示停止前的平均值。" : "测速完成（平均值含连接开销）。";
            if (!r.Stopped) bar.Value = 1000;
            var row = new[] { DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), chosenSource, o.Url, route, o.Mode == 2 ? o.Proxy : "", o.Connections.ToString(), r.Seconds.ToString("F2", CultureInfo.InvariantCulture), r.Mbps.ToString("F3", CultureInfo.InvariantCulture), r.Latency.ToString("F1", CultureInfo.InvariantCulture), r.Bytes.ToString(), r.Stopped ? "停止" : "完成" };
            history.Add(row); log.AppendText(row[0] + "  " + route + "  " + r.Mbps.ToString("F2") + " Mbps  " + row[10] + Environment.NewLine);
        }
        catch (OperationCanceledException) { if (!IsDisposed) { status.Text = "已取消。"; speed.Text = "— Mbps"; } }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                var error = ex.GetBaseException();
                bool limited = error is SpeedRateLimitException;
                speed.Text = limited ? "测速源限流" : "测速失败";
                status.Text = limited ? error.Message : "请检查地址或代理，可切换测速源重试。";
                if (limited) detail.Text = "本次测速已中断，未生成完整测速结果。";
                log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + error.Message + Environment.NewLine);
            }
        }
        finally { var old = running; running = null; stopping = false; old.Dispose(); if (!IsDisposed) setBusy(false); }
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
