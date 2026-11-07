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
    { DoubleBuffered = true; BackColor = Color.White; Font = new Font("Segoe UI", 8); Cursor = Cursors.Hand; }
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
        int start = PageIndex * 256, count = Math.Min(256, Range.Count - start);
        bool shortLabels = (Range.First >> 8) == (Range.Last >> 8);
        using (var outline = new Pen(Color.FromArgb(185, 193, 202), 1.2f))
        for (int cell = 0; cell < count; cell++)
        {
            int index = start + cell; uint number = Range.First + (uint)index;
            float x = cell % 16 * cellWidth, y = cell / 16 * cellHeight;
            var circle = new RectangleF(x + 3, y + (cellHeight - 7) / 2, 7, 7);
            int level = LanScanner.Classify(Results[index], Fast, Medium);
            if (level < 0) e.Graphics.DrawEllipse(outline, circle);
            else using (var brush = new SolidBrush(Shade(level))) e.Graphics.FillEllipse(brush, circle);
            string label = shortLabels ? (number & 255).ToString() : ((number >> 8) & 255) + "." + (number & 255);
            TextRenderer.DrawText(e.Graphics, label, Font, new Rectangle((int)x + 11, (int)y, (int)cellWidth - 10, (int)cellHeight), Color.FromArgb(54, 61, 72), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
    internal Button[] Actions { get { return new[] { start, stop, refresh, export }; } }
    readonly Label status = new Label(), legend = new Label(), pageLabel = new Label(), selected = new Label();
    readonly ProgressBar progress = new ProgressBar(); readonly LanDots dots = new LanDots();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
    readonly ConcurrentQueue<LanResult> pending = new ConcurrentQueue<LanResult>();
    readonly Action<bool> setBusy; CancellationTokenSource running; bool externalBusy;
    int completed; string phase = "就绪";
    internal LanPage(Action<bool> busy)
    {
        setBusy = busy; BackColor = Color.FromArgb(245, 247, 251); AutoScroll = true;
        Controls.Add(new Label { Text = "局域网 Ping 扫描", Font = new Font("Microsoft YaHei UI", 21, FontStyle.Bold), Location = new Point(24, 16), Size = new Size(700, 40) });
        LabelAt("网卡", 24, 71); adapter.SetBounds(80, 66, 688, 30); adapter.DropDownStyle = ComboBoxStyle.DropDownList;
        refresh.Text = "刷新网卡"; refresh.SetBounds(660, 65, 108, 32); Controls.AddRange(new Control[] { adapter, refresh });
        LabelAt("网段", 24, 113); rangeText.SetBounds(80, 108, 322, 30); Controls.Add(rangeText);
        LabelAt("超时 ms", 420, 113); Number(timeout, 494, 108, 82, 100, 5000, 800);
        LabelAt("并发", 600, 113); Number(parallel, 652, 108, 116, 1, 128, 32);
        LabelAt("绿 < ms", 24, 155); Number(fast, 101, 150, 68, 1, 4999, 10);
        LabelAt("黄 < ms", 192, 155); Number(medium, 269, 150, 68, 2, 5000, 50);
        LabelAt("其余为红", 355, 155); LabelAt("最多尝试", 565, 155); Number(attempts, 652, 150, 116, 1, 3, 2);
        start.Text = "开始扫描"; start.SetBounds(24, 190, 140, 36); start.BackColor = Color.FromArgb(35, 96, 207); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat;
        stop.Text = "停止"; stop.SetBounds(180, 190, 94, 36); stop.Enabled = false;
        export.Text = "导出 CSV"; export.SetBounds(648, 190, 120, 36); export.Enabled = false;
        Controls.AddRange(new Control[] { start, stop, export });
        status.SetBounds(24, 194, 744, 26); Controls.Add(status);
        progress.SetBounds(24, 222, 744, 6); progress.Maximum = 1000; Controls.Add(progress);
        legend.SetBounds(24, 237, 744, 24); Controls.Add(legend);
        dots.SetBounds(24, 262, 744, 328); Controls.Add(dots);
        previous.Text = "上一页"; previous.SetBounds(24, 598, 84, 28);
        next.Text = "下一页"; next.SetBounds(684, 598, 84, 28); pageLabel.SetBounds(120, 604, 552, 22); pageLabel.TextAlign = ContentAlignment.MiddleCenter;
        Controls.AddRange(new Control[] { previous, next, pageLabel });
        selected.SetBounds(24, 635, 744, 28); selected.Text = "悬停 / 单击查看详情；灰色不等于离线。ICMP 通过系统路由，不走 HTTP 代理。"; selected.ForeColor = Color.DimGray; Controls.Add(selected);
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
    void LabelAt(string text, int x, int y) { Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y) }); }
    void Number(NumericUpDown n, int x, int y, int width, int min, int max, int value)
    { n.SetBounds(x, y, width, 30); n.Minimum = min; n.Maximum = max; n.Value = value; Controls.Add(n); }
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
