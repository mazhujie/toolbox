using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class SerialWorkspace : IDisposable
{
    internal readonly UserControl Terminal = new UserControl();
    readonly Panel advancedPanel = new Panel();
    readonly TableLayoutPanel layout;
    readonly TabControl sendTabs = new TabControl();
    readonly DataGridView commands = new DataGridView();
    readonly FlowLayoutPanel commandTools = new FlowLayoutPanel();
    readonly LinkLabel advancedToggle = new LinkLabel();
    readonly ComboBox ports = new ComboBox(), baud = new ComboBox(), bits = new ComboBox(), parity = new ComboBox(), stops = new ComboBox(), flow = new ComboBox(), encoding = new ComboBox(), newline = new ComboBox();
    readonly CheckBox dtr = new CheckBox(), rts = new CheckBox(), rxHex = new CheckBox(), txHex = new CheckBox(), timestamps = new CheckBox(), showTx = new CheckBox(), autoscroll = new CheckBox();
    readonly NumericUpDown interval = new NumericUpDown(); readonly TextBox outgoing = new TextBox(), incoming = new TextBox();
    readonly Label state = new Label(), config = new Label(), counters = new Label(), notice = new Label();
    readonly Button connect = new Button(), send = new Button(), automatic = new Button(), clear = new Button(), save = new Button(), refresh = new Button();
    readonly System.Windows.Forms.Timer displayTimer = new System.Windows.Forms.Timer { Interval = 100 }, sendTimer = new System.Windows.Forms.Timer();
    readonly SerialSession session; readonly DataHistory history = new DataHistory();
    readonly ConcurrentQueue<DataRecord> queue = new ConcurrentQueue<DataRecord>();
    readonly object receiveLock = new object();
    long received, transmitted, dropped; int queuedBytes, handlingFault;
    bool changing, writing, shuttingDown, disposed, advancedExpanded;
    byte[][] repeatPayloads;
    int repeatIndex;
    CancellationTokenSource sendCancellation;
    string connectionText = "未连接";
    bool MultiSend { get { return sendTabs.SelectedIndex == 1; } }
    internal SerialWorkspace(SerialSession connection = null)
    {
        session = connection ?? new SerialSession();
        layout = UiTheme.Page(Terminal);
        UiTheme.Heading(layout, "串口收发", UiTheme.Button(connect, "连接串口", true),
            UiTheme.Button(refresh, "刷新端口"), UiTheme.Button(clear, "清空记录"), UiTheme.Button(save, "保存日志"));
        BuildConnection();
        BuildTerminal();
        LayoutTerminal();
        BindEvents();
        displayTimer.Start();
        RefreshPorts();
    }

    void BuildTerminal()
    {
        var receive = UiTheme.Stack();
        UiTheme.Check(timestamps, "时间戳", true);
        UiTheme.Check(rxHex, "HEX 显示", false);
        UiTheme.Check(showTx, "显示发送", true);
        UiTheme.Check(autoscroll, "自动滚动", true);
        UiTheme.Combo(encoding, 105, new[] { "UTF-8", "GBK", "ASCII" }, 0);
        UiTheme.Add(receive, UiTheme.Row(null, UiTheme.Label("接收区"), timestamps, rxHex, showTx, autoscroll, UiTheme.Label("编码"), encoding));
        incoming.ReadOnly = true; incoming.BackColor = Color.White; incoming.Font = UiTheme.Mono;
        incoming.Multiline = true; incoming.ScrollBars = ScrollBars.Vertical; incoming.MaxLength = 2097152;
        UiTheme.Add(receive, incoming, true);
        UiTheme.Status(counters);
        UiTheme.Add(receive, counters);

        var transmit = UiTheme.Stack();
        UiTheme.Check(txHex, "HEX 发送", false);
        UiTheme.Combo(newline, 75, new[] { "无", "CRLF", "LF", "CR" }, 0);
        UiTheme.Number(interval, 87, 100, 3600000, 1000);
        var sendLabel = UiTheme.Label("发送区");
        UiTheme.Add(transmit, UiTheme.Row(sendLabel, sendLabel, txHex, UiTheme.Label("结尾"), newline,
            UiTheme.Label("间隔 ms"), interval, UiTheme.Button(send, "发送一次", true), UiTheme.Button(automatic, "开始定时发送")));
        outgoing.Multiline = true; outgoing.ScrollBars = ScrollBars.Vertical; outgoing.Font = UiTheme.Mono; outgoing.MaxLength = 131072;
        BuildSendEditor();
        UiTheme.Add(transmit, sendTabs, true);
        UiTheme.Add(layout, UiTheme.Split(receive, transmit, 204), true);
        UiTheme.Status(notice);
        notice.Text = "选择端口和波特率后连接串口。输入文本或 HEX，使用 Ctrl+Enter 发送。";
        UiTheme.Add(layout, notice);
    }

    void BuildSendEditor()
    {
        var single = new TabPage("单条发送");
        var multiple = new TabPage("多条发送");
        outgoing.Dock = DockStyle.Fill;
        single.Controls.Add(outgoing);
        sendTabs.TabPages.AddRange(new[] { single, multiple });

        commands.Dock = DockStyle.Fill;
        commands.BackgroundColor = Color.White;
        commands.AllowUserToAddRows = commands.AllowUserToDeleteRows = false;
        commands.AllowUserToResizeRows = false;
        commands.RowHeadersVisible = false;
        commands.AutoGenerateColumns = false;
        commands.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        commands.MultiSelect = false;
        commands.RowTemplate.Height = 27;
        commands.ColumnHeadersHeight = 28;
        commands.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "发送", Width = 52 });
        commands.Columns.Add(new DataGridViewTextBoxColumn { Name = "Number", HeaderText = "序号", Width = 56, ReadOnly = true });
        commands.Columns.Add(new DataGridViewTextBoxColumn { Name = "Content", HeaderText = "发送内容", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MaxInputLength = 131072 });
        commands.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Hex", HeaderText = "HEX", Width = 58 });
        foreach (DataGridViewColumn column in commands.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        commands.CurrentCellDirtyStateChanged += delegate
        {
            if (commands.IsCurrentCellDirty && commands.CurrentCell is DataGridViewCheckBoxCell)
                commands.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        commandTools.Dock = DockStyle.Top;
        commandTools.Height = 34;
        commandTools.WrapContents = false;
        AddCommandTool("新增一条", AddCommand);
        AddCommandTool("删除当前行", DeleteCommand);
        AddCommandTool("全选", () => SelectCommands(true));
        AddCommandTool("全不选", () => SelectCommands(false));
        multiple.Controls.Add(commands);
        multiple.Controls.Add(commandTools);
        for (int i = 0; i < 5; i++) AddCommand();
    }

    void AddCommandTool(string title, Action action)
    {
        var button = new Button { Text = title, AutoSize = true, Height = 28, Margin = new Padding(0, 1, 6, 1) };
        button.Click += delegate { action(); };
        commandTools.Controls.Add(button);
    }

    void AddCommand()
    {
        if (commands.Rows.Count >= 1000) { notice.Text = "多条发送最多可添加 1,000 条。"; return; }
        commands.Rows.Add(false, commands.Rows.Count + 1, "", false);
    }

    void DeleteCommand()
    {
        if (commands.CurrentRow == null) return;
        commands.EndEdit();
        commands.Rows.RemoveAt(commands.CurrentRow.Index);
        for (int i = 0; i < commands.Rows.Count; i++) commands.Rows[i].Cells["Number"].Value = i + 1;
    }

    void SelectCommands(bool selected)
    {
        commands.EndEdit();
        foreach (DataGridViewRow row in commands.Rows) row.Cells["Selected"].Value = selected;
    }

    void BuildConnection()
    {
        UiTheme.Combo(ports, 135, new string[0], -1);
        UiTheme.Combo(baud, 112, new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600" }, 7);
        baud.DropDownStyle = ComboBoxStyle.DropDown;
        advancedToggle.AutoSize = true;
        advancedToggle.LinkColor = UiTheme.Blue;
        advancedToggle.LinkClicked += delegate { advancedExpanded = !advancedExpanded; LayoutTerminal(); };
        UiTheme.Status(state); UiTheme.Status(config);
        UiTheme.Add(layout, UiTheme.Row(state, UiTheme.Label("端口"), ports, UiTheme.Label("波特率"), baud, advancedToggle, state));
        UiTheme.Add(layout, config);

        var options = UiTheme.Card(true);
        UiTheme.Combo(bits, 70, new[] { "5", "6", "7", "8" }, 3);
        UiTheme.Combo(parity, 108, new[] { "None", "Odd", "Even", "Mark", "Space" }, 0);
        UiTheme.Combo(stops, 70, new[] { "1", "1.5", "2" }, 0);
        UiTheme.Combo(flow, 155, new[] { "无流控", "XON / XOFF", "RTS / CTS", "RTS + XON / XOFF" }, 0);
        UiTheme.Check(dtr, "DTR", false); UiTheme.Check(rts, "RTS", false);
        UiTheme.Add(options, UiTheme.Row(null, UiTheme.Label("数据位"), bits, UiTheme.Label("校验"), parity, UiTheme.Label("停止位"), stops));
        UiTheme.Add(options, UiTheme.Row(null, UiTheme.Label("流控"), flow, dtr, rts));
        advancedPanel.AutoSize = true;
        advancedPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        advancedPanel.Controls.Add(options);
        UiTheme.Add(layout, advancedPanel);
    }

    void LayoutTerminal()
    {
        advancedPanel.Visible = advancedExpanded;
        advancedToggle.Text = advancedExpanded ? "收起高级参数 ▴" : "高级参数 ▾";
    }

    void BindEvents()
    {
        connect.Click += async delegate
        {
            if (changing || shuttingDown) return;
            if (session.IsOpen) await ClosePort();
            else await OpenPort();
        };
        send.Click += async delegate { await SendOnce(); };
        outgoing.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; send.PerformClick(); }
        };
        automatic.Click += delegate { ToggleAutomatic(); };
        clear.Click += delegate { ClearRecords(); };
        save.Click += delegate { SaveLog(); };
        refresh.Click += delegate { RefreshPorts(); };
        sendTabs.SelectedIndexChanged += delegate
        {
            notice.Text = MultiSend ? "勾选需要发送的条目，按表格顺序发送；每条可单独选 HEX，文本使用上方结尾。" : "单条发送：输入文本或 HEX，使用发送按钮或 Ctrl+Enter。";
            UpdateControls();
        };
        foreach (var box in new[] { rxHex, timestamps, showTx }) box.CheckedChanged += delegate { Render(); };
        encoding.SelectedIndexChanged += delegate { Render(); };
        txHex.CheckedChanged += delegate { UpdateControls(); };
        ports.SelectedIndexChanged += delegate { UpdateControls(); };
        foreach (var box in new[] { baud, bits, parity, stops, flow })
            box.TextChanged += delegate { UpdateControls(); };
        foreach (var box in new[] { dtr, rts }) box.CheckedChanged += delegate { UpdateControls(); };
        session.Received += bytes =>
        {
            lock (receiveLock)
            {
                Interlocked.Add(ref received, bytes.Length);
                if (Interlocked.Add(ref queuedBytes, bytes.Length) > 1048576)
                {
                    Interlocked.Add(ref queuedBytes, -bytes.Length);
                    Interlocked.Add(ref dropped, bytes.Length);
                    return;
                }
                queue.Enqueue(new DataRecord { Kind = "RX", Bytes = bytes });
            }
        };
        session.Faulted += error =>
        {
            if (Interlocked.Exchange(ref handlingFault, 1) != 0) return;
            Ui(async () =>
            {
                try { await ClosePort(); ShowError("设备错误：" + error); }
                finally { Interlocked.Exchange(ref handlingFault, 0); }
            });
        };
        displayTimer.Tick += delegate { Drain(); };
        sendTimer.Tick += async delegate
        {
            if (writing || repeatPayloads == null) return;
            var payload = repeatPayloads[repeatIndex];
            repeatIndex = (repeatIndex + 1) % repeatPayloads.Length;
            await WritePayload(payload);
        };
    }
    void Ui(Action action)
    {
        Control host = (Control)Terminal.FindForm() ?? Terminal;
        if (!shuttingDown && !host.IsDisposed && host.IsHandleCreated) try { host.BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    void RefreshPorts()
    {
        string selected = ports.Text; ports.Items.Clear();
        try { foreach (string p in SerialPort.GetPortNames().OrderBy(p => p.Length).ThenBy(p => p)) ports.Items.Add(p); }
        catch (Exception ex) { notice.Text = "枚举端口失败：" + ex.Message; }
        if (ports.Items.Contains(selected)) ports.SelectedItem = selected; else if (ports.Items.Count > 0) ports.SelectedIndex = 0;
        if (ports.Items.Count == 0) notice.Text = "未发现 COM 端口，接入 USB 串口设备后点击“刷新端口”。";
        UpdateControls();
    }
    SerialSettings ReadSettings()
    {
        int rate; if (!Int32.TryParse(baud.Text, out rate)) throw new FormatException("波特率请输入整数。");
        var s = new SerialSettings { PortName = ports.Text, Baud = rate, DataBits = Int32.Parse(bits.Text), Parity = (Parity)Enum.Parse(typeof(Parity), parity.Text),
            StopBits = stops.SelectedIndex == 0 ? StopBits.One : stops.SelectedIndex == 1 ? StopBits.OnePointFive : StopBits.Two,
            Handshake = (Handshake)flow.SelectedIndex, Dtr = dtr.Checked, Rts = rts.Checked };
        s.Validate(); return s;
    }
    async Task OpenPort()
    {
        if (changing || shuttingDown || session.IsOpen) return;
        changing = true;
        UpdateControls();
        try
        {
            var s = ReadSettings();
            await session.Open(s);
            connectionText = s.PortName + " · " + s.Baud + " · " + Framing();
            Info("OPEN", "串口已打开：" + connectionText);
            notice.Text = "已连接。可在发送区输入文本或 HEX，按 Ctrl+Enter 发送。";
        }
        catch (Exception ex) { ShowError("打开失败：" + ex.Message); }
        finally { changing = false; UpdateControls(); }
    }

    async Task ClosePort()
    {
        StopAutomatic();
        CancelSend();
        changing = true;
        UpdateControls();
        try
        {
            await session.Close();
            connectionText = "未连接";
            Info("CLOSE", "串口已关闭。");
            notice.Text = "串口已关闭。可修改连接参数后重新打开。";
        }
        catch (Exception ex) { ShowError("关闭失败：" + ex.Message); }
        finally { changing = false; UpdateControls(); }
    }

    byte[][] Payloads()
    {
        if (!MultiSend) return new[] { DataCodec.Encode(outgoing.Text, txHex.Checked, encoding.Text, newline.Text) };
        if (!commands.EndEdit()) throw new FormatException("请先完成发送内容的编辑。");
        var result = new List<byte[]>();
        int size = 0;
        foreach (DataGridViewRow row in commands.Rows)
        {
            if (!Convert.ToBoolean(row.Cells["Selected"].Value ?? false)) continue;
            byte[] bytes;
            try
            {
                bytes = DataCodec.Encode(Convert.ToString(row.Cells["Content"].Value),
                    Convert.ToBoolean(row.Cells["Hex"].Value ?? false), encoding.Text, newline.Text);
            }
            catch (Exception ex) { throw new FormatException("第 " + (row.Index + 1) + " 条：" + ex.Message, ex); }
            size += bytes.Length;
            if (size > 1048576) throw new FormatException("选中条目的总数据量不能超过 1 MB。");
            result.Add(bytes);
        }
        if (result.Count == 0) throw new FormatException("请先勾选需要发送的条目。");
        return result.ToArray();
    }

    async Task SendOnce()
    {
        if (sendCancellation != null) { CancelSend(); return; }
        if (writing || changing || sendTimer.Enabled || !session.IsOpen || shuttingDown) return;
        byte[][] payloads;
        try { payloads = Payloads(); }
        catch (Exception ex) { notice.Text = ex.Message; return; }
        var cancellation = new CancellationTokenSource();
        sendCancellation = cancellation;
        writing = true;
        UpdateControls();
        try
        {
            for (int i = 0; i < payloads.Length; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await SendPayload(payloads[i]);
                if (i + 1 < payloads.Length) await Task.Delay((int)interval.Value, cancellation.Token);
            }
            notice.Text = "已按顺序发送 " + payloads.Length + " 条。";
        }
        catch (OperationCanceledException) { notice.Text = "发送已停止，剩余条目未发送。"; }
        catch (Exception ex) { ShowError("发送失败：" + ex.Message); }
        finally
        {
            sendCancellation = null;
            cancellation.Dispose();
            writing = false;
            UpdateControls();
        }
    }

    void CancelSend() { if (sendCancellation != null) sendCancellation.Cancel(); }

    async Task SendPayload(byte[] data)
    {
        await session.Send(data);
        Interlocked.Add(ref transmitted, data.Length);
        history.Add(new DataRecord { Kind = "TX", Bytes = (byte[])data.Clone() });
        Render();
    }
    async Task WritePayload(byte[] data)
    {
        if (writing || shuttingDown || data == null) return;
        writing = true;
        UpdateControls();
        try
        {
            await SendPayload(data);
        }
        catch (Exception ex) { StopAutomatic(); ShowError("发送失败：" + ex.Message); }
        finally { writing = false; UpdateControls(); }
    }
    void ToggleAutomatic()
    {
        if (sendTimer.Enabled)
        {
            StopAutomatic();
            notice.Text = "定时发送已停止，可修改发送内容。";
            UpdateControls();
            return;
        }
        if (!session.IsOpen || changing || writing) return;
        try
        {
            repeatPayloads = Payloads();
            repeatIndex = 0;
            sendTimer.Interval = (int)interval.Value;
            sendTimer.Start();
            notice.Text = MultiSend ? "循环发送 " + repeatPayloads.Length + " 条，每条间隔 " + interval.Value + " ms；停止后可编辑。"
                : "定时发送中，每 " + interval.Value + " ms 发送一次；停止后可编辑。";
            UpdateControls();
        }
        catch (Exception ex) { notice.Text = ex.Message; }
    }
    void StopAutomatic() { sendTimer.Stop(); repeatPayloads = null; repeatIndex = 0; }
    void Info(string kind, string message) { history.Add(new DataRecord { Kind = kind, Message = message }); Render(); }
    void ShowError(string message) { Info("ERROR", message); notice.Text = message; }
    void ClearRecords()
    {
        // Reset the queued data and RX counters together so an old receive event
        // cannot enqueue its record after the reset.
        lock (receiveLock)
        {
            DataRecord item;
            while (queue.TryDequeue(out item)) { }
            Interlocked.Exchange(ref received, 0);
            Interlocked.Exchange(ref queuedBytes, 0);
            Interlocked.Exchange(ref dropped, 0);
        }
        Interlocked.Exchange(ref transmitted, 0);
        history.Clear();
        Render();
        UpdateCounters();
        save.Enabled = false;
    }

    void UpdateCounters()
    {
        counters.Text = "累计 RX  " + Interlocked.Read(ref received) + " 字节      TX  " + Interlocked.Read(ref transmitted) + " 字节" + (history.Trimmed ? "      仅保留最近 256 KB 记录" : "");
    }

    void Drain()
    {
        DataRecord item; bool changed = false; int bytes = 0;
        while (bytes < DataHistory.Limit && queue.TryDequeue(out item))
        {
            Interlocked.Add(ref queuedBytes, -item.Bytes.Length);
            bytes += item.Bytes.Length;
            history.Add(item);
            changed = true;
        }
        long lost = Interlocked.Exchange(ref dropped, 0);
        if (lost > 0) { history.Add(new DataRecord { Kind = "WARN", Message = "接收队列溢出，已丢弃 " + lost + " 字节的显示记录。" }); changed = true; }
        if (changed) Render();
        UpdateCounters();
        save.Enabled = history.Entries.Any();
    }
    void Render()
    {
        if (Terminal.IsDisposed || encoding.SelectedIndex < 0) return;
        int selection = incoming.SelectionStart;
        incoming.Text = (history.Trimmed ? "[仅显示最近 256 KB，较早记录已裁剪]\r\n" : "") + DataCodec.Render(history.Entries, rxHex.Checked, encoding.Text, timestamps.Checked, showTx.Checked);
        incoming.SelectionStart = autoscroll.Checked ? incoming.TextLength : Math.Min(selection, incoming.TextLength);
        incoming.SelectionLength = 0; incoming.ScrollToCaret();
    }
    void UpdateControls()
    {
        if (Terminal.IsDisposed) return;
        bool connected = session.IsOpen, editable = !connected && !changing && !shuttingDown;
        foreach (Control c in new Control[] { ports, baud, bits, parity, stops, flow, dtr, refresh }) c.Enabled = editable;
        rts.Enabled = editable && flow.SelectedIndex < 2;
        connect.Enabled = !changing && !shuttingDown && (connected || ports.SelectedIndex >= 0);
        connect.Text = changing ? "连接处理中…" : connected ? "断开串口" : "连接串口";
        bool sending = writing || sendTimer.Enabled;
        send.Enabled = !shuttingDown && (sendCancellation != null || (connected && !changing && !sending));
        send.Text = sendCancellation != null ? "停止发送" : MultiSend ? "发送选中条目" : "发送一次";
        automatic.Enabled = connected && !changing && !shuttingDown && sendCancellation == null && (!writing || sendTimer.Enabled);
        automatic.Text = sendTimer.Enabled ? (MultiSend ? "停止循环发送" : "停止定时发送") : MultiSend ? "开始循环发送" : "开始定时发送";
        foreach (Control c in new Control[] { sendTabs, outgoing, commands, commandTools, interval, encoding }) c.Enabled = !sending && !shuttingDown;
        txHex.Enabled = !MultiSend && !sending && !shuttingDown;
        txHex.Visible = !MultiSend;
        newline.Enabled = (MultiSend || !txHex.Checked) && !sending && !shuttingDown;
        state.Text = changing ? "连接操作中…" : connected ? "● 已连接 " + connectionText : "○ 未连接";
        state.ForeColor = connected ? Color.FromArgb(32, 137, 81) : Color.DimGray;
        config.Text = baud.Text + " · " + Framing() + " · " + flow.Text
            + " · DTR " + (dtr.Checked ? "开" : "关") + " · RTS " + (flow.SelectedIndex >= 2 ? "自动" : rts.Checked ? "开" : "关")
            + (connected ? "    断开后可修改参数" : "");
    }

    string Framing() { return bits.Text + (parity.Text.Length > 0 ? parity.Text.Substring(0, 1) : "N") + stops.Text; }
    void SaveLog()
    {
        using (var dialog = new SaveFileDialog { Filter = "文本日志|*.txt", FileName = "串口日志-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" })
        if (dialog.ShowDialog() == DialogResult.OK)
        try { File.WriteAllText(dialog.FileName, incoming.Text, new UTF8Encoding(true)); notice.Text = "已保存当前显示的日志。"; }
        catch (Exception ex) { notice.Text = "保存失败：" + ex.Message; }
    }
    internal async Task Shutdown()
    {
        shuttingDown = true;
        StopAutomatic();
        CancelSend();
        displayTimer.Stop();
        try { await session.Close().ConfigureAwait(false); } catch { }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shuttingDown = true;
        StopAutomatic();
        CancelSend();
        displayTimer.Dispose();
        sendTimer.Dispose();
        try { session.Close().GetAwaiter().GetResult(); } catch { }
    }
}
