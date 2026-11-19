using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class SocketPage : UserControl
{
    readonly SocketSession session = new SocketSession();
    readonly ComboBox protocol = new ComboBox(), localAddress = new ComboBox(), peers = new ComboBox(), encoding = new ComboBox(), ending = new ComboBox();
    readonly NumericUpDown localPort = new NumericUpDown(), remotePort = new NumericUpDown(), interval = new NumericUpDown();
    readonly TextBox remoteHost = new TextBox(), incoming = new TextBox(), outgoing = new TextBox();
    readonly CheckBox rxHex = new CheckBox(), txHex = new CheckBox(), logging = new CheckBox(), newlines = new CheckBox(), pause = new CheckBox(), escapes = new CheckBox(), autoscroll = new CheckBox();
    readonly Label state = new Label(), status = new Label(), counters = new Label(), notice = new Label(), targetLabel = UiTheme.Label("目标地址");
    readonly TableLayoutPanel remoteRow, peerRow;
    readonly Button connect = new Button(), refresh = new Button(), send = new Button(), automatic = new Button(), disconnect = new Button(), clear = new Button(), save = new Button(), loadFile = new Button();
    readonly System.Windows.Forms.Timer displayTimer = new System.Windows.Forms.Timer { Interval = 100 }, sendTimer = new System.Windows.Forms.Timer();
    readonly object queueLock = new object();
    readonly Queue<DataRecord> queue = new Queue<DataRecord>();
    readonly DataHistory history = new DataHistory();
    CancellationTokenSource opening, sending;
    long received, transmitted, dropped;
    int queuedBytes;
    bool writing, shuttingDown;
    string peerSignature = "", repeatHost;
    int repeatPort;
    long repeatPeer;
    byte[] repeatPayload;
    bool IsServer { get { return protocol.SelectedIndex == 2; } }

    internal SocketPage()
    {
        var layout = UiTheme.Page(this);
        UiTheme.Heading(layout, "TCP / UDP 收发", UiTheme.Button(connect, "打开 UDP", true),
            UiTheme.Button(refresh, "刷新地址"), UiTheme.Button(clear, "清空记录"), UiTheme.Button(save, "保存日志"));
        var inputs = UiTheme.Card(true);
        UiTheme.Combo(protocol, 145, new[] { "UDP", "TCP 客户端", "TCP 服务端" }, 0);
        localAddress.DropDownStyle = ComboBoxStyle.DropDown;
        UiTheme.Number(localPort, 95, 0, 65535, 8080);
        UiTheme.Number(remotePort, 95, 1, 65535, 8080);
        remoteHost.Text = "127.0.0.1";
        peers.DropDownStyle = ComboBoxStyle.DropDownList;
        UiTheme.Add(inputs, UiTheme.Row(localAddress, UiTheme.Label("协议"), protocol, UiTheme.Label("本地地址"), localAddress, UiTheme.Label("本地端口"), localPort));
        remoteRow = UiTheme.Row(remoteHost, UiTheme.Label("目标地址"), remoteHost, UiTheme.Label("目标端口"), remotePort);
        peerRow = UiTheme.Row(peers, targetLabel, peers, UiTheme.Button(disconnect, "断开选中客户端"));
        UiTheme.Add(inputs, remoteRow);
        UiTheme.Add(inputs, peerRow);
        UiTheme.Add(layout, inputs);

        UiTheme.Status(state); UiTheme.Status(status);
        UiTheme.Add(layout, UiTheme.Row(status, state, status));

        var receive = UiTheme.Stack();
        UiTheme.Check(rxHex, "HEX 显示", false);
        UiTheme.Check(logging, "日志模式", true);
        UiTheme.Check(newlines, "自动换行", true);
        UiTheme.Check(pause, "暂停显示", false);
        UiTheme.Check(autoscroll, "自动滚动", true);
        UiTheme.Combo(encoding, 98, new[] { "UTF-8", "ASCII", "GBK" }, 0);
        UiTheme.Add(receive, UiTheme.Row(null, UiTheme.Label("接收区"), rxHex, logging, newlines, pause, autoscroll, UiTheme.Label("编码"), encoding));
        incoming.Multiline = true; incoming.ReadOnly = true; incoming.ScrollBars = ScrollBars.Vertical;
        incoming.BackColor = Color.White; incoming.MaxLength = 2097152; incoming.Font = UiTheme.Mono;
        UiTheme.Add(receive, incoming, true);
        UiTheme.Status(counters);
        UiTheme.Add(receive, counters);

        var transmit = UiTheme.Stack();
        UiTheme.Check(txHex, "HEX 发送", false);
        UiTheme.Check(escapes, "解析转义符", false);
        UiTheme.Combo(ending, 80, new[] { "无", "CRLF", "LF", "CR" }, 0);
        UiTheme.Number(interval, 95, 100, 3600000, 1000);
        UiTheme.Add(transmit, UiTheme.Row(null, UiTheme.Label("发送区"), txHex, escapes, UiTheme.Label("结尾"), ending, UiTheme.Label("间隔 ms"), interval));
        var sendHint = UiTheme.Label("Ctrl+Enter 发送");
        sendHint.ForeColor = UiTheme.Muted;
        UiTheme.Add(transmit, UiTheme.Row(sendHint, sendHint, UiTheme.Button(loadFile, "载入为 HEX"),
            UiTheme.Button(send, "发送一次", true), UiTheme.Button(automatic, "开始定时发送")));
        outgoing.Multiline = true; outgoing.ScrollBars = ScrollBars.Vertical; outgoing.MaxLength = 200000; outgoing.Font = UiTheme.Mono;
        UiTheme.Add(transmit, outgoing, true);
        UiTheme.Add(layout, UiTheme.Split(receive, transmit, 192), true);
        UiTheme.Status(notice);
        notice.Text = "使用系统路由。TCP 按数据流接收，UDP 每次发送一个数据报。";
        UiTheme.Add(layout, notice);
        BindEvents();
        RefreshAddresses();
        SetMode();
        displayTimer.Start();
    }

    void BindEvents()
    {
        protocol.SelectedIndexChanged += delegate { SetMode(); };
        connect.Click += async delegate { await ToggleConnection(); };
        refresh.Click += delegate { RefreshAddresses(); };
        send.Click += async delegate { await SendOnce(); };
        outgoing.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; send.PerformClick(); }
        };
        automatic.Click += delegate { ToggleAutomatic(); };
        disconnect.Click += delegate { if (peers.SelectedItem != null) session.Disconnect(((SocketPeer)peers.SelectedItem).Id); RefreshState(); };
        clear.Click += delegate { ClearRecords(); };
        save.Click += delegate { SaveLog(); };
        loadFile.Click += delegate { LoadFile(); };
        foreach (var check in new[] { rxHex, logging, newlines }) check.CheckedChanged += delegate { Render(); };
        pause.CheckedChanged += delegate { if (!pause.Checked) Render(); };
        encoding.SelectedIndexChanged += delegate { Render(); };
        txHex.CheckedChanged += delegate { UpdateControls(); };
        peers.SelectedIndexChanged += delegate { UpdateControls(); };
        session.Recorded += Enqueue;
        displayTimer.Tick += delegate { Drain(); RefreshState(); };
        sendTimer.Tick += async delegate { if (!writing && repeatPayload != null) await Write(repeatPayload, repeatHost, repeatPort, repeatPeer); };
    }

    void SetMode()
    {
        localPort.Value = protocol.SelectedIndex == 1 ? 0 : 8080;
        targetLabel.Text = "发送给";
        remoteRow.Visible = !IsServer;
        peerRow.Visible = IsServer;
        peerSignature = "";
        RefreshState();
    }

    void RefreshAddresses()
    {
        string selected = localAddress.Text;
        var addresses = new List<string> { "0.0.0.0", "127.0.0.1" };
        try
        {
            addresses.AddRange(NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()));
        }
        catch (Exception ex) { notice.Text = "枚举地址失败，可手动输入：" + ex.Message; }
        localAddress.Items.Clear();
        localAddress.Items.AddRange(addresses.Distinct().Cast<object>().ToArray());
        localAddress.Text = String.IsNullOrEmpty(selected) ? "0.0.0.0" : selected;
    }

    SocketOptions ReadOptions()
    {
        IPAddress address;
        if (!IPAddress.TryParse(localAddress.Text.Trim(), out address)) throw new FormatException("请输入有效的本地 IPv4 地址。");
        return new SocketOptions { Mode = (SocketMode)protocol.SelectedIndex, LocalAddress = address, LocalPort = (int)localPort.Value,
            RemoteHost = remoteHost.Text.Trim(), RemotePort = (int)remotePort.Value };
    }

    async Task ToggleConnection()
    {
        if (opening != null) { opening.Cancel(); session.Close(); return; }
        if (session.IsOpen) { CloseConnection(); return; }
        var cancellation = new CancellationTokenSource();
        opening = cancellation;
        UpdateControls();
        try
        {
            await session.Open(ReadOptions(), cancellation.Token);
            notice.Text = !session.IsOpen ? "连接已结束，详见日志。" : IsServer
                ? "监听已启动。可选择单个客户端发送，也可选择“全部客户端”。"
                : "已打开。输入文本或 HEX 后点击“发送一次”，也可启动定时发送。";
        }
        catch (OperationCanceledException) { Info("INFO", "已取消连接。"); }
        catch (Exception ex) { Info("ERROR", "打开失败：" + ex.Message); }
        finally
        {
            opening = null;
            cancellation.Dispose();
            if (!IsDisposed) RefreshState();
        }
    }

    void CloseConnection()
    {
        StopAutomatic();
        if (sending != null) sending.Cancel();
        session.Close();
        Info("CLOSE", "网络连接已关闭。");
        RefreshState();
    }

    byte[] Payload()
    {
        string text = escapes.Checked && !txHex.Checked ? DataCodec.ExpandEscapes(outgoing.Text) : outgoing.Text;
        byte[] bytes = DataCodec.Encode(text, txHex.Checked, encoding.Text, ending.Text);
        if (protocol.SelectedIndex == 0 && bytes.Length > 65507) throw new FormatException("UDP 单个数据报最多 65,507 字节。");
        return bytes;
    }

    long SelectedPeer() { return peers.SelectedItem == null ? -1 : ((SocketPeer)peers.SelectedItem).Id; }

    async Task SendOnce()
    {
        if (writing || sendTimer.Enabled || !session.IsOpen) return;
        try { await Write(Payload(), remoteHost.Text.Trim(), (int)remotePort.Value, IsServer ? SelectedPeer() : 0); }
        catch (Exception ex) { notice.Text = ex.Message; }
    }

    async Task Write(byte[] payload, string host, int port, long peer)
    {
        if (writing || shuttingDown) return;
        var cancellation = new CancellationTokenSource();
        sending = cancellation;
        writing = true;
        UpdateControls();
        try { await session.Send(payload, host, port, peer, cancellation.Token); }
        catch (OperationCanceledException) { StopAutomatic(); }
        catch (Exception ex) { StopAutomatic(); Info("ERROR", "发送失败：" + ex.Message); }
        finally
        {
            sending = null;
            cancellation.Dispose();
            writing = false;
            if (!IsDisposed) RefreshState();
        }
    }

    void ToggleAutomatic()
    {
        if (sendTimer.Enabled) { StopAutomatic(); notice.Text = "定时发送已停止。"; UpdateControls(); return; }
        if (!session.IsOpen || writing) return;
        try
        {
            repeatPayload = Payload(); repeatHost = remoteHost.Text.Trim(); repeatPort = (int)remotePort.Value;
            repeatPeer = IsServer ? SelectedPeer() : 0;
            sendTimer.Interval = (int)interval.Value;
            sendTimer.Start();
            notice.Text = "定时发送中，每 " + interval.Value + " ms 一次；停止后可修改内容和目标。";
            UpdateControls();
        }
        catch (Exception ex) { notice.Text = ex.Message; }
    }

    void StopAutomatic() { sendTimer.Stop(); repeatPayload = null; }

    void Enqueue(DataRecord item)
    {
        lock (queueLock)
        {
            int size = item.Bytes == null ? 64 : item.Bytes.Length;
            if (item.Bytes != null)
            {
                if (item.Kind == "RX") received += item.Bytes.Length;
                if (item.Kind == "TX") transmitted += item.Bytes.Length;
            }
            if (queuedBytes + size > 1048576 || queue.Count >= 4000) { dropped++; return; }
            queue.Enqueue(item); queuedBytes += size;
        }
    }

    void Info(string kind, string message)
    {
        if (IsDisposed || shuttingDown) return;
        Enqueue(new DataRecord { Kind = kind, Message = message });
        notice.Text = message;
        Drain();
    }

    void Drain()
    {
        bool changed = false;
        lock (queueLock)
        {
            int bytes = 0;
            while (queue.Count > 0 && bytes < DataHistory.Limit)
            {
                var item = queue.Dequeue();
                int size = item.Bytes == null ? 64 : item.Bytes.Length;
                queuedBytes -= size; bytes += size; history.Add(item); changed = true;
            }
            if (dropped > 0)
            {
                history.Add(new DataRecord { Kind = "WARN", Message = "显示队列繁忙，已丢弃 " + dropped + " 条显示记录；字节计数仍累计。" });
                dropped = 0; changed = true;
            }
            counters.Text = "累计 RX  " + received + " 字节      TX  " + transmitted + " 字节"
                + (history.Trimmed ? "      仅保留最近 256 KB / 4000 条记录" : "");
        }
        if (changed && !pause.Checked) Render();
        save.Enabled = history.Entries.Any();
    }

    void ClearRecords()
    {
        lock (queueLock)
        {
            queue.Clear(); queuedBytes = 0; received = transmitted = dropped = 0;
            history.Clear();
        }
        Render();
        Drain();
    }

    void Render()
    {
        if (IsDisposed || encoding.SelectedIndex < 0) return;
        int start = incoming.SelectionStart, length = incoming.SelectionLength;
        incoming.Text = (history.Trimmed ? "[较早记录已裁剪]\r\n" : "")
            + DataCodec.RenderNetwork(history.Entries, rxHex.Checked, encoding.Text, logging.Checked, newlines.Checked);
        if (autoscroll.Checked)
        {
            incoming.Select(incoming.TextLength, 0);
            incoming.ScrollToCaret();
        }
        else incoming.Select(Math.Min(start, incoming.TextLength), Math.Min(length, Math.Max(0, incoming.TextLength - start)));
    }

    void RefreshState()
    {
        if (IsDisposed || shuttingDown) return;
        var clients = session.Peers;
        string signature = String.Join(",", clients.Select(p => p.Id.ToString()));
        if (signature != peerSignature || (IsServer && peers.Items.Count == 0))
        {
            long selected = SelectedPeer();
            peerSignature = signature;
            peers.Items.Clear();
            peers.Items.Add(new SocketPeer { Id = 0 });
            peers.Items.AddRange(clients.Cast<object>().ToArray());
            peers.SelectedItem = peers.Items.Cast<SocketPeer>().FirstOrDefault(p => p.Id == selected);
            if (selected == -1 && clients.Length > 0) peers.SelectedIndex = 1;
        }
        bool active = session.IsOpen;
        if (!active && opening == null) StopAutomatic();
        state.Text = opening != null ? "连接中…" : active ? "● " + (IsServer ? "监听中" : "已打开") : "○ 未打开";
        state.ForeColor = active ? Color.FromArgb(32, 137, 81) : Color.DimGray;
        status.Text = active ? "本地 " + session.LocalEndpoint + (IsServer ? "    已连接客户端 " + clients.Length : "")
            : "本地端口 0 自动分配；0.0.0.0 监听所有本机 IPv4 地址。";
        UpdateControls();
    }

    void UpdateControls()
    {
        if (IsDisposed) return;
        bool active = session.IsOpen, busy = opening != null, locked = writing || sendTimer.Enabled;
        foreach (Control control in new Control[] { protocol, localAddress, localPort, refresh }) control.Enabled = !active && !busy && !shuttingDown;
        remoteHost.Enabled = remotePort.Enabled = !busy && !locked && !shuttingDown && (!active || protocol.SelectedIndex == 0);
        peers.Enabled = active && !locked && !shuttingDown;
        bool target = !IsServer || (peers.SelectedItem != null && session.Peers.Length > 0);
        connect.Enabled = !shuttingDown;
        connect.Text = busy ? "取消连接" : active ? "关闭连接 / 监听" : IsServer ? "开始监听" : protocol.SelectedIndex == 1 ? "连接服务器" : "打开 UDP";
        send.Enabled = active && target && !locked && !shuttingDown;
        automatic.Enabled = active && target && (!writing || sendTimer.Enabled) && !shuttingDown;
        automatic.Text = sendTimer.Enabled ? "停止定时发送" : "开始定时发送";
        disconnect.Enabled = IsServer && active && target && !locked && !shuttingDown;
        foreach (Control control in new Control[] { outgoing, txHex, interval, encoding, loadFile }) control.Enabled = !locked && !shuttingDown;
        ending.Enabled = escapes.Enabled = !txHex.Checked && !locked && !shuttingDown;
    }

    void SaveLog()
    {
        using (var dialog = new SaveFileDialog { Filter = "文本日志|*.txt", FileName = "网络调试-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" })
        if (dialog.ShowDialog() == DialogResult.OK)
        try { File.WriteAllText(dialog.FileName, DataCodec.RenderNetwork(history.Entries, rxHex.Checked, encoding.Text, logging.Checked, newlines.Checked), new UTF8Encoding(true)); notice.Text = "日志已保存。"; }
        catch (Exception ex) { notice.Text = "保存失败：" + ex.Message; }
    }

    void LoadFile()
    {
        using (var dialog = new OpenFileDialog { Filter = "所有文件|*.*" })
        if (dialog.ShowDialog() == DialogResult.OK)
        try
        {
            int maximum = protocol.SelectedIndex == 0 ? 65507 : 65536;
            var info = new FileInfo(dialog.FileName);
            if (info.Length < 1 || info.Length > maximum) throw new IOException("文件大小应为 1–" + maximum + " 字节。");
            outgoing.Text = BitConverter.ToString(File.ReadAllBytes(dialog.FileName)).Replace('-', ' ');
            txHex.Checked = true;
            notice.Text = "文件已载入发送区，点击“发送一次”后才发送。";
        }
        catch (Exception ex) { notice.Text = "读取失败：" + ex.Message; }
    }

    internal void Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        if (opening != null) opening.Cancel();
        if (sending != null) sending.Cancel();
        StopAutomatic(); displayTimer.Stop(); session.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Shutdown(); displayTimer.Dispose(); sendTimer.Dispose(); session.Dispose(); }
        base.Dispose(disposing);
    }
}
