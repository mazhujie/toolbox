using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class CapturePage : UserControl
{
    const int DisplayBytes = 8 * 1024 * 1024, DisplayCount = 1000;
    readonly ComboBox adapters = new ComboBox();
    readonly TextBox filter = new TextBox(), folder = new TextBox(), detail = new TextBox();
    readonly NumericUpDown seconds = new NumericUpDown(), megabytes = new NumericUpDown();
    readonly CheckBox promiscuous = new CheckBox(), pause = new CheckBox(), follow = new CheckBox();
    readonly ListView packets = new ListView();
    readonly Label state = new Label(), counters = new Label(), notice = new Label();
    readonly Button start = new Button(), stop = new Button(), refresh = new Button(), save = new Button(), openFolder = new Button(), install = new Button(), chooseFolder = new Button();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
    readonly Func<CaptureAdapter[]> catalog;
    readonly Func<CaptureOptions, IPacketSource> factory;
    readonly object queueLock = new object();
    readonly Queue<PacketRecord> pending = new Queue<PacketRecord>(), history = new Queue<PacketRecord>();
    PacketRecord[] shown = new PacketRecord[0];
    int pendingBytes, historyBytes;
    bool busy, shuttingDown, dirty;
    CancellationTokenSource cancellation;
    TaskCompletionSource<bool> completion;
    CaptureSession session;
    string captureFile;
    internal bool Busy { get { return busy; } }

    internal CapturePage(Func<CaptureAdapter[]> list = null, Func<CaptureOptions, IPacketSource> source = null, string outputFolder = null)
    {
        catalog = list ?? Npcap.Adapters; factory = source ?? (options => new NpcapSource(options));
        var layout = UiTheme.Page(this);
        UiTheme.Heading(layout, "实时抓包", UiTheme.Button(start, "开始抓包", true),
            UiTheme.Button(stop, "停止抓包", warning: true), UiTheme.Button(refresh, "刷新网卡"));
        UiTheme.Status(state); UiTheme.Status(counters); UiTheme.Status(notice);
        adapters.DropDownStyle = ComboBoxStyle.DropDownList;
        filter.MaxLength = 2048;
        UiTheme.Number(seconds, 74, 1, 3600, 60);
        UiTheme.Number(megabytes, 74, 1, 512, 64);
        UiTheme.Check(promiscuous, "混杂模式", false);
        UiTheme.Check(pause, "暂停显示", false);
        UiTheme.Check(follow, "自动滚动", true);
        folder.Text = outputFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ToolboxCaptures");
        var inputs = UiTheme.Card(true);
        UiTheme.Add(inputs, UiTheme.Row(adapters, UiTheme.Label("抓包网卡"), adapters, UiTheme.Button(install, "获取 Npcap")));
        UiTheme.Add(inputs, UiTheme.Row(filter, UiTheme.Label("过滤 BPF"), filter));
        UiTheme.Add(inputs, UiTheme.Row(null, UiTheme.Label("时长上限"), seconds, UiTheme.Label("秒     文件上限"), megabytes, UiTheme.Label("MiB"), promiscuous));
        UiTheme.Add(inputs, UiTheme.Row(folder, UiTheme.Label("保存目录"), folder, UiTheme.Button(chooseFolder, "选择目录"),
            UiTheme.Button(openFolder, "打开目录"), UiTheme.Button(save, "另存 PCAP")));
        UiTheme.Add(layout, inputs);
        UiTheme.Add(layout, UiTheme.Row(counters, state, pause, follow, counters));

        packets.View = View.Details; packets.FullRowSelect = true; packets.MultiSelect = false; packets.HideSelection = false; packets.VirtualMode = true;
        packets.Columns.Add("序号", 54); packets.Columns.Add("时间", 96);
        packets.Columns.Add("源地址", 125); packets.Columns.Add("目标地址", 125);
        packets.Columns.Add("协议", 62); packets.Columns.Add("长度", 56); packets.Columns.Add("摘要", 150);
        packets.SizeChanged += delegate { ResizeColumns(); };
        detail.Multiline = true; detail.ReadOnly = true; detail.BackColor = Color.White;
        detail.WordWrap = false; detail.ScrollBars = ScrollBars.Both; detail.Font = UiTheme.Mono; detail.MaxLength = 100000;
        var detailPanel = UiTheme.Stack();
        UiTheme.Add(detailPanel, UiTheme.Label("数据包详情 · HEX / ASCII"));
        UiTheme.Add(detailPanel, detail, true);
        UiTheme.Add(layout, UiTheme.Split(packets, detailPanel, 170), true);
        notice.Text = "PCAP 自动保存。界面显示最近 1,000 包 / 8 MiB。";
        UiTheme.Add(layout, notice);
        start.Click += async delegate { await StartCapture(); }; stop.Click += delegate { if (cancellation != null) cancellation.Cancel(); };
        refresh.Click += async delegate { await RefreshAdapters(); };
        save.Click += delegate { Save(); }; openFolder.Click += delegate { OpenDirectory(); };
        install.Click += delegate { try { Process.Start(new ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true }); } catch (Exception ex) { notice.Text = ex.Message; } };
        chooseFolder.Click += delegate { using (var dialog = new FolderBrowserDialog { SelectedPath = folder.Text }) if (dialog.ShowDialog() == DialogResult.OK) folder.Text = dialog.SelectedPath; };
        adapters.SelectedIndexChanged += delegate { UpdateControls(); };
        pause.CheckedChanged += delegate { if (!pause.Checked) { dirty = true; Drain(); } };
        packets.RetrieveVirtualItem += delegate(object sender, RetrieveVirtualItemEventArgs e) {
            if (e.ItemIndex >= shown.Length) { e.Item = new ListViewItem(""); return; }
            var p = shown[e.ItemIndex];
            e.Item = new ListViewItem(new[] { p.Number.ToString(), p.Frame.At.ToString("HH:mm:ss.fff"), p.Source, p.Destination, p.Protocol, p.Frame.OriginalLength.ToString(), p.Info });
        };
        packets.SelectedIndexChanged += delegate { if (packets.SelectedIndices.Count > 0 && packets.SelectedIndices[0] < shown.Length) detail.Text = PacketDecoder.Detail(shown[packets.SelectedIndices[0]]); };
        timer.Tick += delegate { Drain(); }; timer.Start(); UpdateControls();
    }
    void ResizeColumns()
    {
        int width = packets.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
        int addressWidth = Math.Max(108, Math.Min(185, width / 5));
        packets.Columns[2].Width = packets.Columns[3].Width = addressWidth;
        packets.Columns[6].Width = Math.Max(84, width - 268 - 2 * addressWidth);
    }

    void UpdateControls()
    {
        bool idle = !busy && !shuttingDown;
        foreach (Control control in new Control[] { adapters, filter, folder, seconds, megabytes, promiscuous, chooseFolder, refresh }) control.Enabled = idle;
        start.Enabled = idle && adapters.SelectedItem != null; stop.Enabled = cancellation != null && !shuttingDown;
        save.Enabled = idle && captureFile != null && File.Exists(captureFile);
        state.Text = busy ? cancellation == null ? "枚举网卡中…" : "● 抓包运行中" : "○ 未在抓包";
        state.ForeColor = busy && cancellation != null ? Color.FromArgb(32, 137, 81) : Color.DimGray;
    }
    internal async Task RefreshAdapters()
    {
        if (busy || shuttingDown) return;
        string previous = adapters.SelectedItem == null ? null : ((CaptureAdapter)adapters.SelectedItem).Name;
        busy = true; var done = new TaskCompletionSource<bool>(); completion = done; UpdateControls();
        try
        {
            var result = await Task.Run(catalog);
            if (shuttingDown) return;
            adapters.Items.Clear(); adapters.Items.AddRange(result);
            install.Visible = result.Length == 0;
            adapters.SelectedItem = result.FirstOrDefault(a => a.Name == previous);
            if (previous == null && result.Length == 1) adapters.SelectedIndex = 0;
            notice.Text = result.Length == 0 ? "未发现可抓包网卡，请检查 Npcap 安装、驱动服务与权限。" : "已发现 " + result.Length + " 个网卡，请选择后开始。回环网卡用于本机进程间流量。";
        }
        catch (Exception ex) { if (!shuttingDown) { adapters.Items.Clear(); install.Visible = true; notice.Text = ex.Message; } }
        finally { busy = false; if (!shuttingDown) UpdateControls(); done.TrySetResult(true); }
    }
    internal async Task StartCapture()
    {
        if (busy || shuttingDown) return;
        CaptureOptions options;
        try
        {
            if (adapters.SelectedItem == null) throw new ArgumentException("请选择抓包网卡。");
            if (String.IsNullOrWhiteSpace(folder.Text) || !Path.IsPathRooted(folder.Text)) throw new ArgumentException("请输入完整的本地保存目录。");
            string directory = Path.GetFullPath(folder.Text);
            options = new CaptureOptions { Adapter = ((CaptureAdapter)adapters.SelectedItem).Name, Filter = filter.Text.Trim(), Seconds = (int)seconds.Value, ByteLimit = (long)megabytes.Value * 1024 * 1024, Promiscuous = promiscuous.Checked,
                File = Path.Combine(directory, "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".pcap") };
            options.Validate(); Directory.CreateDirectory(directory);
        }
        catch (Exception ex) { notice.Text = ex.Message; return; }
        var cancel = new CancellationTokenSource(); cancellation = cancel; busy = true;
        var done = new TaskCompletionSource<bool>(); completion = done; captureFile = options.File;
        lock (queueLock) { pending.Clear(); pendingBytes = 0; }
        history.Clear(); historyBytes = 0; shown = new PacketRecord[0]; packets.VirtualListSize = 0; detail.Clear(); dirty = true;
        session = new CaptureSession(factory); UpdateControls(); notice.Text = "正在抓包，原始数据写入：" + captureFile;
        try { await session.Run(options, cancel.Token, Enqueue); if (!shuttingDown) notice.Text = session.Reason + "。文件：" + captureFile; }
        catch (OperationCanceledException) { if (!shuttingDown) notice.Text = "抓包已取消。"; }
        catch (Exception ex) { if (!shuttingDown) notice.Text = "抓包失败：" + ex.Message; }
        finally
        {
            cancellation = null; cancel.Dispose(); busy = false;
            if (!shuttingDown) { Drain(); UpdateControls(); }
            done.TrySetResult(true);
        }
    }
    void Enqueue(PacketRecord packet)
    {
        lock (queueLock)
        {
            pending.Enqueue(packet); pendingBytes += packet.Frame.Data.Length;
            while (pending.Count > DisplayCount || pendingBytes > DisplayBytes) pendingBytes -= pending.Dequeue().Frame.Data.Length;
        }
    }
    void Drain()
    {
        lock (queueLock)
        {
            while (pending.Count > 0)
            {
                var packet = pending.Dequeue(); pendingBytes -= packet.Frame.Data.Length;
                history.Enqueue(packet); historyBytes += packet.Frame.Data.Length; dirty = true;
            }
        }
        while (history.Count > DisplayCount || historyBytes > DisplayBytes) historyBytes -= history.Dequeue().Frame.Data.Length;
        if (dirty && !pause.Checked)
        {
            long selected = packets.SelectedIndices.Count > 0 && packets.SelectedIndices[0] < shown.Length ? shown[packets.SelectedIndices[0]].Number : -1;
            shown = history.ToArray(); packets.SelectedIndices.Clear(); packets.VirtualListSize = shown.Length;
            int index = Array.FindIndex(shown, p => p.Number == selected);
            if (index >= 0) packets.SelectedIndices.Add(index); else if (selected != -1) detail.Clear();
            if (follow.Checked && shown.Length > 0) packets.EnsureVisible(shown.Length - 1);
            packets.Invalidate(); dirty = false;
        }
        counters.Text = session == null ? "0 包" : session.Count + " 包 · " + (session.Bytes / 1048576.0).ToString("F2") + " MiB · 缓冲丢包 " + (session.Dropped < 0 ? "—" : session.Dropped.ToString()) + " · 列表 " + shown.Length;
    }
    void Save()
    {
        if (busy || captureFile == null || !File.Exists(captureFile)) return;
        using (var dialog = new SaveFileDialog { Filter = "PCAP 抓包文件|*.pcap", FileName = Path.GetFileName(captureFile) })
        if (dialog.ShowDialog() == DialogResult.OK)
        try { if (!String.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(captureFile), StringComparison.OrdinalIgnoreCase)) File.Copy(captureFile, dialog.FileName, true); notice.Text = "已保存：" + dialog.FileName; }
        catch (Exception ex) { notice.Text = "保存失败：" + ex.Message; }
    }
    void OpenDirectory()
    {
        try
        {
            string path = captureFile == null ? folder.Text : Path.GetDirectoryName(captureFile);
            if (!Directory.Exists(path)) throw new IOException("保存目录尚不存在，开始抓包后会创建。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { notice.Text = ex.Message; }
    }
    internal Task Shutdown()
    {
        shuttingDown = true; timer.Stop(); if (cancellation != null) cancellation.Cancel();
        return completion == null ? Task.FromResult(true) : completion.Task;
    }
    protected override void Dispose(bool disposing) { if (disposing) { Shutdown(); timer.Dispose(); } base.Dispose(disposing); }
}
