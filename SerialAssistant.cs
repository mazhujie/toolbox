using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class SerialSettings
{
    public string PortName;
    public int Baud = 115200, DataBits = 8;
    public Parity Parity = Parity.None;
    public StopBits StopBits = StopBits.One;
    public Handshake Handshake = Handshake.None;
    public bool Dtr, Rts;

    internal void Validate()
    {
        if (String.IsNullOrWhiteSpace(PortName) || !Regex.IsMatch(PortName, "^COM[1-9][0-9]*$", RegexOptions.IgnoreCase))
            throw new ArgumentException("请选择有效 COM 端口。");
        if (Baud < 1 || Baud > 4000000 || DataBits < 5 || DataBits > 8 || StopBits == StopBits.None)
            throw new ArgumentException("串口参数超出范围。");
    }
}
internal interface ISerialTransport : IDisposable
{
    event Action<byte[]> Received;
    event Action<string> Faulted;
    bool IsOpen { get; }
    void Open();
    void Write(byte[] bytes);
}
internal sealed class WindowsSerialTransport : ISerialTransport
{
    readonly SerialPort port;
    volatile bool closing;
    Task reader;
    public event Action<byte[]> Received;
    public event Action<string> Faulted;
    public bool IsOpen { get { return !closing && port.IsOpen; } }

    internal WindowsSerialTransport(SerialSettings s)
    {
        port = new SerialPort(s.PortName, s.Baud, s.Parity, s.DataBits, s.StopBits)
        {
            Handshake = s.Handshake, DtrEnable = s.Dtr,
            ReadTimeout = 200, WriteTimeout = 1000,
            ReadBufferSize = 65536, WriteBufferSize = 65536
        };
        if (s.Handshake != Handshake.RequestToSend && s.Handshake != Handshake.RequestToSendXOnXOff)
            port.RtsEnable = s.Rts;
    }

    public void Open()
    {
        port.Open();
        closing = false;
        reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    void ReadLoop()
    {
        var buffer = new byte[4096];
        while (!closing)
        {
            try
            {
                int n = port.Read(buffer, 0, buffer.Length);
                if (n <= 0) continue;
                var copy = new byte[n];
                Buffer.BlockCopy(buffer, 0, copy, 0, n);
                var handler = Received;
                if (handler != null) handler(copy);
            }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                var handler = Faulted;
                if (!closing && handler != null) handler(ex.Message);
                break;
            }
        }
    }

    public void Write(byte[] bytes) { port.Write(bytes, 0, bytes.Length); }

    public void Dispose()
    {
        closing = true;
        try { if (port.IsOpen) port.Close(); }
        finally
        {
            if (reader != null)
            {
                try { reader.Wait(1500); }
                catch (AggregateException) { }
            }
            port.Dispose();
        }
    }
}
internal sealed class SerialSession
{
    readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    readonly Func<SerialSettings, ISerialTransport> factory;
    volatile ISerialTransport current;
    internal event Action<byte[]> Received;
    internal event Action<string> Faulted;
    internal bool IsOpen { get { var port = current; return port != null && port.IsOpen; } }

    internal SerialSession(Func<SerialSettings, ISerialTransport> make = null)
    {
        factory = make ?? (s => new WindowsSerialTransport(s));
    }

    internal async Task Open(SerialSettings settings)
    {
        settings.Validate();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (current != null) throw new InvalidOperationException("请先关闭当前串口。");
            var candidate = factory(settings);
            current = candidate;
            candidate.Received += bytes =>
            {
                var handler = Received;
                if (ReferenceEquals(current, candidate) && handler != null) handler(bytes);
            };
            candidate.Faulted += error =>
            {
                var handler = Faulted;
                if (ReferenceEquals(current, candidate) && handler != null) handler(error);
            };
            // C# 5 cannot await cleanup inside a catch block.
            Exception failure = null;
            try { await Task.Run(() => candidate.Open()).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            if (failure != null)
            {
                current = null;
                await Task.Run(() => { try { candidate.Dispose(); } catch { } }).ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally { gate.Release(); }
    }

    async Task CloseLocked()
    {
        var old = current;
        current = null;
        if (old != null) await Task.Run(() => old.Dispose()).ConfigureAwait(false);
    }

    internal async Task Close()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await CloseLocked().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    internal async Task Send(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > 65536)
            throw new ArgumentException("单次发送应为 1–65,536 字节。");
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var port = current;
            if (port == null || !port.IsOpen) throw new InvalidOperationException("串口尚未打开。");
            Exception failure = null;
            try { await Task.Run(() => port.Write(bytes)).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            if (failure != null)
            {
                await CloseLocked().ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally { gate.Release(); }
    }
}
internal sealed class SerialRecord
{
    public DateTime At = DateTime.Now; public string Kind, Message; public byte[] Bytes;
}
internal static class SerialCodec
{
    internal static Encoding EncodingFor(string name)
    { return name == "GBK" ? Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback) : name == "ASCII" ? Encoding.GetEncoding(20127, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback) : new UTF8Encoding(false, false); }
    internal static byte[] Encode(string text, bool hex, string encoding, string newline)
    {
        byte[] result;
        if (hex)
        {
            var output = new List<byte>();
            foreach (var raw in Regex.Split(text.Trim(), "[\\s,;]+"))
            {
                if (raw.Length == 0) continue;
                string part = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw.Substring(2) : raw;
                if (part.Length == 0 || part.Length % 2 != 0) throw new FormatException("HEX 必须是完整字节，例如 AA BB 0D 0A。");
                for (int i = 0; i < part.Length; i += 2)
                {
                    byte value; if (!Byte.TryParse(part.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) throw new FormatException("HEX 中存在非十六进制字符。");
                    output.Add(value); if (output.Count > 65536) throw new FormatException("单次发送最多 65,536 字节。");
                }
            }
            result = output.ToArray();
        }
        else result = EncodingFor(encoding).GetBytes(text + (newline == "CRLF" ? "\r\n" : newline == "LF" ? "\n" : newline == "CR" ? "\r" : ""));
        if (result.Length == 0 || result.Length > 65536) throw new FormatException("单次发送应为 1–65,536 字节。");
        return result;
    }
    internal static string Render(IEnumerable<SerialRecord> records, bool hex, string encoding, bool timestamps, bool showTx)
    {
        var output = new StringBuilder(); var codec = EncodingFor(encoding); var decoder = codec.GetDecoder();
        foreach (var item in records)
        {
            string content;
            if (item.Bytes == null) { content = item.Message; if (item.Kind == "OPEN" || item.Kind == "CLOSE") decoder.Reset(); }
            else
            {
                if (item.Kind == "TX" && !showTx) continue;
                if (hex) content = BitConverter.ToString(item.Bytes).Replace('-', ' ');
                else if (item.Kind == "RX")
                {
                    char[] chars = new char[codec.GetMaxCharCount(item.Bytes.Length)];
                    int n = decoder.GetChars(item.Bytes, 0, item.Bytes.Length, chars, 0, false); content = new string(chars, 0, n);
                }
                else content = codec.GetString(item.Bytes);
            }
            if (String.IsNullOrEmpty(content)) continue;
            if (timestamps) output.Append('[').Append(item.At.ToString("HH:mm:ss.fff")).Append("] ");
            output.Append(item.Kind).Append("  ").Append(content);
            if (!content.EndsWith("\n")) output.AppendLine();
        }
        return output.ToString();
    }
}
internal sealed class SerialHistory
{
    internal const int Limit = 262144;
    readonly Queue<SerialRecord> entries = new Queue<SerialRecord>(); int bytes;
    internal bool Trimmed;
    internal IEnumerable<SerialRecord> Entries { get { return entries; } }
    internal int ByteCount { get { return bytes; } }
    static int Size(SerialRecord item) { return item.Bytes == null ? (item.Message ?? "").Length * 2 + 32 : item.Bytes.Length; }
    internal void Add(SerialRecord item)
    {
        entries.Enqueue(item); bytes += Size(item);
        while (bytes > Limit || entries.Count > 4000) { bytes -= Size(entries.Dequeue()); Trimmed = true; }
    }
    internal void Clear() { entries.Clear(); bytes = 0; Trimmed = false; }
}

internal sealed class SerialWorkspace : IDisposable
{
    internal readonly UserControl Terminal = new UserControl();
    readonly Panel advancedPanel = new Panel(), traffic = new Panel(), sendOptions = new Panel();
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
    readonly SerialSession session; readonly SerialHistory history = new SerialHistory();
    readonly ConcurrentQueue<SerialRecord> queue = new ConcurrentQueue<SerialRecord>();
    readonly object receiveLock = new object();
    long received, transmitted, dropped; int queuedBytes, handlingFault;
    bool changing, writing, shuttingDown, disposed, advancedExpanded;
    byte[][] repeatPayloads;
    int repeatIndex;
    CancellationTokenSource sendCancellation;
    string connectionText = "未连接";
    bool MultiSend { get { return sendTabs.SelectedIndex == 1; } }
    internal Button[] TerminalActions { get { return new[] { connect, refresh, send, automatic, clear, save }; } }
    internal SerialWorkspace(SerialSession connection = null)
    {
        session = connection ?? new SerialSession();
        Terminal.BackColor = Color.FromArgb(245, 247, 251);
        Terminal.AutoScroll = true;
        Terminal.Font = new Font("Microsoft YaHei UI", 10);
        BuildTerminal();
        BuildConnection();
        LayoutTerminal();
        BindEvents();
        displayTimer.Start();
        RefreshPorts();
    }

    void BuildTerminal()
    {
        Title(Terminal, "串口收发");
        state.SetBounds(356, 28, 412, 30); state.TextAlign = ContentAlignment.MiddleRight; Terminal.Controls.Add(state);
        config.SetBounds(24, 118, 744, 26); config.ForeColor = Color.DimGray; Terminal.Controls.Add(config);
        traffic.SetBounds(24, 150, 744, 554); Terminal.Controls.Add(traffic);
        var receiveOptions = new Panel { Size = new Size(744, 30) };
        traffic.Controls.Add(receiveOptions);
        LabelAt(receiveOptions, "接收区", 0, 6);
        Check(receiveOptions, timestamps, "时间戳", 86, 0, 100, true);
        Check(receiveOptions, rxHex, "HEX 显示", 196, 0, 112, false);
        Check(receiveOptions, showTx, "显示发送", 318, 0, 112, true);
        Check(receiveOptions, autoscroll, "自动滚动", 438, 0, 110, true);
        LabelAt(receiveOptions, "编码", 566, 6);
        Combo(receiveOptions, encoding, 616, 0, 128, new[] { "UTF-8", "GBK", "ASCII" }, 0);
        incoming.SetBounds(0, 38, 744, 260); incoming.ReadOnly = true; incoming.BackColor = Color.White; incoming.Font = new Font("Consolas", 10);
        incoming.Multiline = true; incoming.ScrollBars = ScrollBars.Vertical; incoming.MaxLength = 2097152;
        incoming.WordWrap = true; traffic.Controls.Add(incoming);
        counters.SetBounds(0, 308, 744, 26); traffic.Controls.Add(counters);
        sendOptions.SetBounds(0, 346, 744, 30); traffic.Controls.Add(sendOptions);
        LabelAt(sendOptions, "发送区", 0, 6); Check(sendOptions, txHex, "HEX 发送", 86, 0, 120, false);
        LabelAt(sendOptions, "结尾", 224, 6); Combo(sendOptions, newline, 276, 0, 108, new[] { "无", "CRLF", "LF", "CR" }, 0);
        LabelAt(sendOptions, "发送间隔 ms", 416, 6); interval.SetBounds(540, 0, 204, 30); interval.Minimum = 100; interval.Maximum = 3600000; interval.Value = 1000; sendOptions.Controls.Add(interval);
        outgoing.Multiline = true; outgoing.ScrollBars = ScrollBars.Vertical; outgoing.Font = new Font("Consolas", 11); outgoing.MaxLength = 131072;
        BuildSendEditor();
        notice.SetBounds(0, 510, 744, 44); notice.ForeColor = Color.DimGray;
        notice.Text = "在顶部选择端口和波特率，点击左侧“连接串口”。HEX 示例：AA BB 0D 0A。";
        traffic.Controls.Add(notice);
    }

    void BuildSendEditor()
    {
        sendTabs.SetBounds(0, 370, 744, 130);
        var single = new TabPage("单条发送");
        var multiple = new TabPage("多条发送");
        outgoing.Dock = DockStyle.Fill;
        single.Controls.Add(outgoing);
        sendTabs.TabPages.AddRange(new[] { single, multiple });
        traffic.Controls.Add(sendTabs);

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
        LabelAt(Terminal, "端口", 24, 86);
        Combo(Terminal, ports, 80, 80, 180, new string[0], -1);
        LabelAt(Terminal, "波特率", 282, 86);
        Combo(Terminal, baud, 350, 80, 178, new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600" }, 7);
        baud.DropDownStyle = ComboBoxStyle.DropDown;
        advancedToggle.SetBounds(600, 84, 168, 26);
        advancedToggle.LinkColor = Color.FromArgb(35, 96, 207);
        advancedToggle.LinkClicked += delegate { advancedExpanded = !advancedExpanded; LayoutTerminal(); };
        Terminal.Controls.Add(advancedToggle);

        advancedPanel.SetBounds(24, 150, 744, 104);
        advancedPanel.BackColor = Color.FromArgb(233, 239, 248);
        Terminal.Controls.Add(advancedPanel);
        LabelAt(advancedPanel, "数据位", 10, 16); Combo(advancedPanel, bits, 78, 10, 100, new[] { "5", "6", "7", "8" }, 3);
        LabelAt(advancedPanel, "校验", 214, 16); Combo(advancedPanel, parity, 270, 10, 140, new[] { "None", "Odd", "Even", "Mark", "Space" }, 0);
        LabelAt(advancedPanel, "停止位", 460, 16); Combo(advancedPanel, stops, 538, 10, 188, new[] { "1", "1.5", "2" }, 0);
        LabelAt(advancedPanel, "流控", 10, 66); Combo(advancedPanel, flow, 78, 60, 240, new[] { "无流控", "XON / XOFF", "RTS / CTS", "RTS + XON / XOFF" }, 0);
        Check(advancedPanel, dtr, "DTR", 378, 58, 120, false);
        Check(advancedPanel, rts, "RTS", 538, 58, 120, false);
    }

    void LayoutTerminal()
    {
        int extra = advancedExpanded ? 116 : 0;
        int shrink = Math.Min(120, extra + (MultiSend ? 104 : 0));
        Terminal.AutoScrollPosition = Point.Empty;
        advancedPanel.Visible = advancedExpanded;
        advancedToggle.Text = advancedExpanded ? "收起高级参数 ▴" : "高级参数 ▾";
        traffic.Top = 150 + extra;
        incoming.Height = 240 - shrink;
        counters.Top = incoming.Bottom + 10;
        sendOptions.Top = counters.Top + 38;
        sendTabs.Top = sendOptions.Top + 44;
        sendTabs.Height = MultiSend ? 234 : 130;
        notice.Top = sendTabs.Bottom + 14;
        traffic.Height = notice.Bottom;
        Terminal.AutoScrollMinSize = new Size(792, traffic.Bottom + 16);
    }

    void BindEvents()
    {
        connect.Text = "连接串口";
        send.Text = "发送一次";
        automatic.Text = "开始定时发送";
        clear.Text = "清空记录";
        save.Text = "保存日志";
        refresh.Text = "刷新端口";
        connect.Click += async delegate
        {
            if (changing || shuttingDown) return;
            if (session.IsOpen) await ClosePort();
            else await OpenPort();
        };
        send.Click += async delegate { await SendOnce(); };
        automatic.Click += delegate { ToggleAutomatic(); };
        clear.Click += delegate { ClearRecords(); };
        save.Click += delegate { SaveLog(); };
        refresh.Click += delegate { RefreshPorts(); };
        sendTabs.SelectedIndexChanged += delegate
        {
            LayoutTerminal();
            notice.Text = MultiSend ? "勾选需要发送的条目，按表格顺序发送；每条可单独选 HEX，文本使用上方结尾。" : "单条发送：输入文本或 HEX，使用左侧发送按钮。";
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
                queue.Enqueue(new SerialRecord { Kind = "RX", Bytes = bytes });
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
    static void Title(Control page, string text) { page.Controls.Add(new Label { Text = text, Location = new Point(24, 20), Size = new Size(320, 44), Font = new Font("Microsoft YaHei UI", 22, FontStyle.Bold) }); }
    static void LabelAt(Control page, string text, int x, int y) { page.Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y) }); }
    static void Check(Control page, CheckBox box, string text, int x, int y, int width, bool value) { box.Text = text; box.SetBounds(x, y, width, 30); box.Checked = value; page.Controls.Add(box); }
    static void Combo(Control page, ComboBox box, int x, int y, int width, string[] values, int index) { box.SetBounds(x, y, width, 30); box.DropDownStyle = ComboBoxStyle.DropDownList; box.Items.AddRange(values); if (index >= 0) box.SelectedIndex = index; page.Controls.Add(box); }
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
        if (ports.Items.Count == 0) notice.Text = "未发现 COM 端口，接入 USB 串口设备后点击左侧“刷新端口”。";
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
            notice.Text = "已连接。发送按钮位于左侧，可发送文本或 HEX 字节。";
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
        if (!MultiSend) return new[] { SerialCodec.Encode(outgoing.Text, txHex.Checked, encoding.Text, newline.Text) };
        if (!commands.EndEdit()) throw new FormatException("请先完成发送内容的编辑。");
        var result = new List<byte[]>();
        int size = 0;
        foreach (DataGridViewRow row in commands.Rows)
        {
            if (!Convert.ToBoolean(row.Cells["Selected"].Value ?? false)) continue;
            byte[] bytes;
            try
            {
                bytes = SerialCodec.Encode(Convert.ToString(row.Cells["Content"].Value),
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
        history.Add(new SerialRecord { Kind = "TX", Bytes = (byte[])data.Clone() });
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
    void Info(string kind, string message) { history.Add(new SerialRecord { Kind = kind, Message = message }); Render(); }
    void ShowError(string message) { Info("ERROR", message); notice.Text = message; }
    void ClearRecords()
    {
        // Reset the queued data and RX counters together so an old receive event
        // cannot enqueue its record after the reset.
        lock (receiveLock)
        {
            SerialRecord item;
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
        SerialRecord item; bool changed = false; int bytes = 0;
        while (bytes < SerialHistory.Limit && queue.TryDequeue(out item))
        {
            Interlocked.Add(ref queuedBytes, -item.Bytes.Length);
            bytes += item.Bytes.Length;
            history.Add(item);
            changed = true;
        }
        long lost = Interlocked.Exchange(ref dropped, 0);
        if (lost > 0) { history.Add(new SerialRecord { Kind = "WARN", Message = "接收队列溢出，已丢弃 " + lost + " 字节的显示记录。" }); changed = true; }
        if (changed) Render();
        UpdateCounters();
        save.Enabled = history.Entries.Any();
    }
    void Render()
    {
        if (Terminal.IsDisposed || encoding.SelectedIndex < 0) return;
        int selection = incoming.SelectionStart;
        incoming.Text = (history.Trimmed ? "[仅显示最近 256 KB，较早记录已裁剪]\r\n" : "") + SerialCodec.Render(history.Entries, rxHex.Checked, encoding.Text, timestamps.Checked, showTx.Checked);
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

internal sealed class FakeSerialTransport : ISerialTransport
{
    public event Action<byte[]> Received; public event Action<string> Faulted;
    public bool IsOpen { get; private set; }
    internal bool Disposed, FailOpen, FailWrite; internal int Writes, ActiveWrites, PeakWrites;
    internal int FailAtWrite { get; set; }
    internal readonly ConcurrentQueue<byte[]> Sent = new ConcurrentQueue<byte[]>();
    public void Open() { if (FailOpen) throw new IOException("fixture open failure"); IsOpen = true; }
    public void Write(byte[] data)
    {
        if (!IsOpen || FailWrite || (FailAtWrite > 0 && Writes + 1 == FailAtWrite)) throw new IOException("fixture write failure");
        int active = Interlocked.Increment(ref ActiveWrites); PeakWrites = Math.Max(PeakWrites, active);
        Thread.Sleep(8); Writes++; Sent.Enqueue((byte[])data.Clone());
        Receive(data);
        Interlocked.Decrement(ref ActiveWrites);
    }
    internal void Receive(byte[] data)
    {
        var handler = Received;
        if (handler != null) handler((byte[])data.Clone());
    }
    public void Dispose() { IsOpen = false; Disposed = true; }
    internal void Fault() { var handler = Faulted; if (handler != null) handler("fixture disconnect"); }
}
internal static class SerialTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception("FAIL serial: " + message); }
    internal static async Task<string> Run()
    {
        Assert(SerialCodec.Encode("0xAA BB,0D0A", true, "UTF-8", "CRLF").SequenceEqual(new byte[] { 0xAA, 0xBB, 13, 10 }), "HEX exact bytes; no implicit suffix");
        Assert(Encoding.UTF8.GetString(SerialCodec.Encode("中文", false, "UTF-8", "CRLF")) == "中文\r\n", "UTF-8 plus newline");
        foreach (string text in new[] { "A", "0xZZ", "0x", "" })
        { bool failed = false; try { SerialCodec.Encode(text, true, "UTF-8", "无"); } catch (FormatException) { failed = true; } Assert(failed, "invalid HEX rejection"); }
        bool asciiFailed = false; try { SerialCodec.Encode("中文", false, "ASCII", "无"); } catch (EncoderFallbackException) { asciiFailed = true; }
        Assert(asciiFailed, "unsupported ASCII characters are not silently replaced");
        byte[] chinese = Encoding.UTF8.GetBytes("中文");
        var records = new[] { new SerialRecord { Kind = "RX", Bytes = chinese.Take(2).ToArray() }, new SerialRecord { Kind = "TX", Bytes = Encoding.UTF8.GetBytes("ok") }, new SerialRecord { Kind = "RX", Bytes = chinese.Skip(2).ToArray() } };
        string rendered = SerialCodec.Render(records, false, "UTF-8", false, true);
        Assert(rendered.Contains("中文") && !rendered.Contains("�"), "UTF-8 decoder preserves fragmented receive bytes");
        Assert(SerialCodec.Render(records, true, "UTF-8", false, true).Contains("E4 B8"), "HEX receive formatting");
        var history = new SerialHistory(); for (int i = 0; i < 80; i++) history.Add(new SerialRecord { Kind = "RX", Bytes = new byte[4096] });
        Assert(history.Trimmed && history.ByteCount <= SerialHistory.Limit, "bounded log retention");
        var fake = new FakeSerialTransport(); var session = new SerialSession(s => fake); var echoed = new ConcurrentQueue<byte[]>();
        session.Received += bytes => echoed.Enqueue(bytes);
        await session.Open(new SerialSettings { PortName = "COM99" });
        await Task.WhenAll(session.Send(new byte[] { 0, 255 }), session.Send(new byte[] { 13, 10 }));
        Assert(fake.Writes == 2 && fake.PeakWrites == 1 && echoed.Count == 2, "serialized sends and receive echo");
        Assert(echoed.Any(bytes => bytes.SequenceEqual(new byte[] { 0, 255 })), "binary data preserved");
        fake.FailWrite = true; bool writeFailed = false;
        try { await session.Send(new byte[] { 1 }); } catch (IOException) { writeFailed = true; }
        Assert(writeFailed && !session.IsOpen && fake.Disposed, "write failure closes transport");
        var bad = new FakeSerialTransport { FailOpen = true }; var badSession = new SerialSession(s => bad); bool openFailed = false;
        try { await badSession.Open(new SerialSettings { PortName = "COM98" }); } catch (IOException) { openFailed = true; }
        Assert(openFailed && !badSession.IsOpen && bad.Disposed, "open failure releases transport");
        await session.Close(); await badSession.Close();
        return "PASS serial HEX/text encoding, line endings and invalid input\r\nPASS fragmented UTF-8 receive and bounded logs\r\nPASS simulated serial open/send/receive/close, serialized writes and failures\r\nNOTE physical serial hardware has not been exercised.\r\n";
    }
}
