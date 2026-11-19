using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class LanDots : Control
{
    internal LanRange Range; internal LanResult[] Results;
    internal int PageIndex, Fast = 10, Medium = 50;
    readonly ToolTip tip = new ToolTip { InitialDelay = 150, ReshowDelay = 100, AutoPopDelay = 15000 };
    int hovered = -1;
    internal Action<string> Selected;
    internal void RefreshResults() { if (hovered >= 0) tip.SetToolTip(this, Description(hovered)); Invalidate(); }
    internal static readonly Color Green = Color.FromArgb(31, 158, 87), Yellow = Color.FromArgb(211, 151, 0), Red = Color.FromArgb(217, 66, 66), Gray = Color.FromArgb(145, 151, 160);
    internal LanDots()
    { DoubleBuffered = true; BackColor = Color.White; Font = UiTheme.Dot; Cursor = Cursors.Hand; }
    internal static Color Shade(int level)
    { return level == 1 ? Green : level == 2 ? Yellow : level == 3 ? Red : Gray; }
    internal string Description(int index)
    {
        if (Range == null || index < 0 || index >= Range.Count) return "";
        var ip = LanRange.Address(Range.First + (uint)index); var result = Results[index];
        if (result == null) return ip + "  尚未完成扫描";
        string text = ip + (result.Probe.Success ? "  有响应  " + (result.Probe.Milliseconds == 0 ? "<1" : result.Probe.Milliseconds.ToString()) + " ms" : "  未响应（未收到成功的 Echo Reply）")
            + "  尝试 " + result.Attempts + " 次";
        if (!result.Probe.Success) text += "  " + result.Probe.Status;
        if (result.Probe.Success && result.Probe.ReplyAddress != ip.ToString()) text += "  响应地址 " + result.Probe.ReplyAddress;
        return text;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Range == null) { TextRenderer.DrawText(e.Graphics, "选择网卡或输入网段，开始扫描后每个 IP 会显示一个点。", Font, ClientRectangle, Color.DimGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        float cellWidth = ClientSize.Width / 16f, cellHeight = ClientSize.Height / 16f;
        float diameter = Math.Max(6, Math.Min(13, Math.Min(cellWidth, cellHeight) * .38f));
        int start = PageIndex * 256, count = Math.Min(256, Range.Count - start);
        bool shortLabels = (Range.First >> 8) == (Range.Last >> 8);
        using (var outline = new Pen(Color.FromArgb(185, 193, 202), 1.2f))
        for (int cell = 0; cell < count; cell++)
        {
            int index = start + cell; uint number = Range.First + (uint)index;
            float x = cell % 16 * cellWidth, y = cell / 16 * cellHeight;
            var circle = new RectangleF(x + 3, y + (cellHeight - diameter) / 2, diameter, diameter);
            int level = LanScanner.Classify(Results[index], Fast, Medium);
            if (level < 0) e.Graphics.DrawEllipse(outline, circle);
            else using (var brush = new SolidBrush(Shade(level))) e.Graphics.FillEllipse(brush, circle);
            string label = shortLabels ? (number & 255).ToString() : ((number >> 8) & 255) + "." + (number & 255);
            TextRenderer.DrawText(e.Graphics, label, Font, new Rectangle((int)(x + diameter + 6), (int)y, Math.Max(0, (int)(cellWidth - diameter - 6)), (int)cellHeight), Color.FromArgb(54, 61, 72), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
    int Hit(Point location)
    {
        if (Range == null || ClientSize.Width == 0 || ClientSize.Height == 0) return -1;
        int column = location.X * 16 / ClientSize.Width, row = location.Y * 16 / ClientSize.Height;
        int index = PageIndex * 256 + row * 16 + column;
        return column >= 0 && column < 16 && row >= 0 && row < 16 && index < Range.Count ? index : -1;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); int index = Hit(e.Location);
        if (index != hovered) { hovered = index; tip.SetToolTip(this, Description(index)); }
    }
    protected override void OnMouseLeave(EventArgs e) { hovered = -1; tip.SetToolTip(this, ""); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e); int index = Hit(e.Location);
        if (index >= 0 && Selected != null) Selected(Description(index));
    }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
}

internal sealed class LanAdapter
{
    public string Name, Ip, Cidr; public bool HasGateway;
    public override string ToString() { return Name + "  ·  " + Ip + "  ·  " + Cidr; }
}
internal sealed class LanPage : UserControl
{
    readonly ComboBox adapter = new ComboBox(); readonly TextBox rangeText = new TextBox();
    readonly NumericUpDown timeout = new NumericUpDown(), parallel = new NumericUpDown(), attempts = new NumericUpDown(), fast = new NumericUpDown(), medium = new NumericUpDown();
    readonly Button refresh = new Button(), start = new Button(), stop = new Button(), export = new Button(), previous = new Button(), next = new Button();
    readonly Label status = new Label(), legend = new Label(), pageLabel = new Label(), selected = new Label();
    readonly ProgressBar progress = new ProgressBar(); readonly LanDots dots = new LanDots();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
    readonly ConcurrentQueue<LanResult> pending = new ConcurrentQueue<LanResult>();
    readonly Action<bool> setBusy; CancellationTokenSource running; bool externalBusy;
    int completed; string phase = "就绪";
    internal LanPage(Action<bool> busy)
    {
        setBusy = busy;
        var layout = UiTheme.Page(this);
        UiTheme.Heading(layout, "局域网扫描", UiTheme.Button(start, "开始扫描", true),
            UiTheme.Button(stop, "停止", warning: true), UiTheme.Button(refresh, "刷新网卡"), UiTheme.Button(export, "导出 CSV"));
        stop.Enabled = export.Enabled = false;
        var inputs = UiTheme.Card(true);
        adapter.DropDownStyle = ComboBoxStyle.DropDownList;
        UiTheme.Add(inputs, UiTheme.Row(adapter, UiTheme.Label("网卡"), adapter));
        UiTheme.Number(timeout, 78, 100, 5000, 800);
        UiTheme.Number(parallel, 65, 1, 128, 32);
        UiTheme.Number(fast, 65, 1, 4999, 10);
        UiTheme.Number(medium, 65, 2, 5000, 50);
        UiTheme.Number(attempts, 65, 1, 3, 2);
        UiTheme.Add(inputs, UiTheme.Row(rangeText, UiTheme.Label("网段"), rangeText, UiTheme.Label("超时 ms"), timeout, UiTheme.Label("并发"), parallel));
        UiTheme.Add(inputs, UiTheme.Row(null, UiTheme.Label("绿 < ms"), fast, UiTheme.Label("黄 < ms"), medium,
            UiTheme.Label("其余为红     最多尝试"), attempts));
        UiTheme.Add(layout, inputs);
        UiTheme.Status(status); UiTheme.Status(legend); UiTheme.Status(selected);
        progress.Height = 7; progress.Maximum = 1000;
        UiTheme.Add(layout, status);
        UiTheme.Add(layout, progress);
        UiTheme.Add(layout, legend);
        UiTheme.Add(layout, dots, true);
        UiTheme.Button(previous, "上一页"); UiTheme.Button(next, "下一页");
        pageLabel.TextAlign = ContentAlignment.MiddleCenter;
        UiTheme.Add(layout, UiTheme.Row(pageLabel, previous, pageLabel, next));
        selected.Text = "悬停或单击查看详情。灰色代表未收到 ICMP 响应。";
        UiTheme.Add(layout, selected);
        adapter.SelectedIndexChanged += delegate { var a = adapter.SelectedItem as LanAdapter; if (a != null) rangeText.Text = a.Cidr; };
        refresh.Click += delegate { LoadAdapters(); };
        rangeText.TextChanged += delegate { if (running == null) PreviewRange(); };
        fast.ValueChanged += delegate { if (medium.Value <= fast.Value) medium.Value = fast.Value + 1; UpdateDisplay(); };
        medium.ValueChanged += delegate { if (medium.Value <= fast.Value) fast.Value = medium.Value - 1; UpdateDisplay(); };
        start.Click += async delegate { await Scan(); }; stop.Click += delegate { Cancel(); }; export.Click += delegate { Export(); };
        previous.Click += delegate { if (dots.PageIndex > 0) { dots.PageIndex--; UpdateDisplay(); } };
        next.Click += delegate { if (dots.Range != null && (dots.PageIndex + 1) * 256 < dots.Range.Count) { dots.PageIndex++; UpdateDisplay(); } };
        dots.Selected = text => selected.Text = text;
        timer.Tick += delegate { Drain(); }; LoadAdapters();
    }
    internal void SetExternalBusy(bool busy) { externalBusy = busy; start.Enabled = !busy && running == null; }
    internal void Cancel()
    {
        if (running != null) { running.Cancel(); phase = "正在停止（等待本轮 Ping 超时）"; UpdateDisplay(); }
    }
    void LoadAdapters()
    {
        adapter.Items.Clear(); var found = new List<LanAdapter>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    var properties = network.GetIPProperties();
                    foreach (var ip in properties.UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip.Address)) continue;
                        if (ip.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Preferred) continue;
                        found.Add(new LanAdapter { Name = network.Name, Ip = ip.Address.ToString(), Cidr = LanRange.Network(ip.Address, ip.IPv4Mask),
                            HasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) });
                    }
                } catch (NetworkInformationException) { } catch (FormatException) { }
            }
            foreach (var a in found.OrderByDescending(a => a.HasGateway).ThenBy(a => a.Name)) adapter.Items.Add(a);
            if (adapter.Items.Count > 0) adapter.SelectedIndex = 0;
            else { rangeText.Text = ""; status.Text = "未找到可用 IPv4 网卡，请手动输入网段，例如 192.168.50.0/24。"; }
        }
        catch (Exception ex) { status.Text = "读取网卡失败，可手动输入网段：" + ex.Message; }
    }
    void PreviewRange()
    {
        try
        {
            dots.Range = LanRange.Parse(rangeText.Text); dots.Results = new LanResult[dots.Range.Count]; dots.PageIndex = 0;
            completed = 0; phase = "就绪"; export.Enabled = false; UpdateDisplay();
        }
        catch (Exception ex) { dots.Range = null; dots.Results = null; progress.Value = 0; export.Enabled = false; status.Text = ex.Message; dots.Invalidate(); }
    }
    void SetInputs(bool enabled)
    {
        foreach (Control c in new Control[] { adapter, rangeText, timeout, parallel, attempts, refresh }) c.Enabled = enabled;
    }
    async Task Scan()
    {
        if (running != null || externalBusy) return;
        LanRange range;
        try { range = LanRange.Parse(rangeText.Text); } catch (Exception ex) { status.Text = ex.Message; return; }
        LanResult leftover; while (pending.TryDequeue(out leftover)) { }
        dots.Range = range; dots.Results = new LanResult[range.Count]; dots.PageIndex = 0; completed = 0; phase = "扫描中";
        running = new CancellationTokenSource(); var current = running;
        SetInputs(false); stop.Enabled = true; export.Enabled = false; setBusy(true); timer.Start(); UpdateDisplay();
        try
        {
            var summary = await LanScanner.Run(range, (int)timeout.Value, (int)parallel.Value, (int)attempts.Value, current.Token, r => pending.Enqueue(r));
            if (IsDisposed) return;
            Drain(); phase = summary.Cancelled ? "已停止，空心点为尚未完成" : "扫描完成";
        }
        catch (Exception ex) { if (!IsDisposed) { Drain(); phase = "扫描出错：" + ex.Message; } }
        finally
        {
            running = null; current.Dispose(); timer.Stop();
            if (!IsDisposed) { SetInputs(true); stop.Enabled = false; setBusy(false); start.Enabled = !externalBusy; export.Enabled = completed > 0; UpdateDisplay(); }
        }
    }
    void Drain()
    {
        LanResult result; bool changed = false;
        while (pending.TryDequeue(out result)) { dots.Results[result.Index] = result; completed++; changed = true; }
        if (changed) UpdateDisplay();
    }
    void UpdateDisplay()
    {
        dots.Fast = (int)fast.Value; dots.Medium = (int)medium.Value; dots.RefreshResults();
        if (dots.Range == null) { previous.Enabled = next.Enabled = false; pageLabel.Text = ""; return; }
        int[] counts = new int[4];
        foreach (var result in dots.Results) { int level = LanScanner.Classify(result, dots.Fast, dots.Medium); if (level >= 0) counts[level]++; }
        status.Text = phase + "  ·  " + completed + " / " + dots.Range.Count + "  ·  有响应 " + (counts[1] + counts[2] + counts[3]);
        legend.Text = "● 绿 " + counts[1] + "    ● 黄 " + counts[2] + "    ● 红 " + counts[3] + "    ● 灰（未响应）" + counts[0] + "    ○ 待完成 " + (dots.Range.Count - completed);
        progress.Value = (int)((long)completed * 1000 / dots.Range.Count);
        int first = dots.PageIndex * 256, last = Math.Min(first + 255, dots.Range.Count - 1);
        pageLabel.Text = "第 " + (dots.PageIndex + 1) + " / " + ((dots.Range.Count + 255) / 256) + " 页  ·  " + LanRange.Address(dots.Range.First + (uint)first) + " – " + LanRange.Address(dots.Range.First + (uint)last);
        previous.Enabled = dots.PageIndex > 0; next.Enabled = last + 1 < dots.Range.Count;
    }
    static string Csv(string text) { if (text.Length > 0 && "=+-@\t\r".IndexOf(text[0]) >= 0) text = "'" + text; return "\"" + text.Replace("\"", "\"\"") + "\""; }
    void Export()
    {
        if (dots.Range == null || completed == 0) return;
        using (var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "局域网扫描-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" })
        if (dialog.ShowDialog() == DialogResult.OK)
        try
        {
            var lines = new List<string> { "IP,分类,延迟ms,尝试次数,状态,响应地址" };
            for (int i = 0; i < dots.Range.Count; i++)
            {
                var result = dots.Results[i]; int level = LanScanner.Classify(result, dots.Fast, dots.Medium);
                string name = level < 0 ? "未完成" : new[] { "未响应", "绿-快", "黄-中等", "红-慢" }[level];
                var values = new[] { LanRange.Address(dots.Range.First + (uint)i).ToString(), name,
                    result != null && result.Probe.Success ? result.Probe.Milliseconds.ToString(CultureInfo.InvariantCulture) : "",
                    result == null ? "" : result.Attempts.ToString(), result == null ? "" : result.Probe.Status, result == null ? "" : result.Probe.ReplyAddress ?? "" };
                lines.Add(String.Join(",", values.Select(Csv)));
            }
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true)); selected.Text = "已导出本次全部 IP（含未响应及未完成项）。";
        }
        catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message); }
    }
    protected override void Dispose(bool disposing) { if (disposing) { Cancel(); timer.Dispose(); } base.Dispose(disposing); }
}
