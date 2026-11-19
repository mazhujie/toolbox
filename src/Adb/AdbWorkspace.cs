using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class AdbHeader : UserControl
{
    internal readonly TextBox Executable = new TextBox(), Address = new TextBox();
    internal readonly ComboBox Devices = new ComboBox();
    internal readonly Button Browse = new Button(), Connect = new Button(), Disconnect = new Button();
    internal readonly LinkLabel Download = new LinkLabel();
    internal readonly Label State = new Label();

    internal AdbHeader()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var layout = UiTheme.Card(true);
        layout.Margin = Padding.Empty;
        Controls.Add(layout);
        Devices.DropDownStyle = ComboBoxStyle.DropDownList;
        UiTheme.Add(layout, UiTheme.Row(Executable, UiTheme.Label("ADB 程序"), Executable, UiTheme.Button(Browse, "选择…")));
        UiTheme.Add(layout, UiTheme.Row(Devices, UiTheme.Label("设备"), Devices));
        UiTheme.Add(layout, UiTheme.Row(Address, UiTheme.Label("网络地址"), Address,
            UiTheme.Button(Connect, "连接设备"), UiTheme.Button(Disconnect, "断开网络设备")));
        UiTheme.Status(State);
        Download.Text = "下载官方 Platform Tools"; Download.AutoSize = true;
        UiTheme.Add(layout, UiTheme.Row(State, State, Download));
    }

}

internal sealed class AdbWorkspace : IDisposable
{
    internal readonly UserControl ShellPage = new UserControl(), FilesPage = new UserControl();
    readonly AdbHeader shellHeader = new AdbHeader(), filesHeader = new AdbHeader();
    readonly TextBox command = new TextBox(), shellOutput = new TextBox(), fileOutput = new TextBox();
    readonly TextBox pushLocal = new TextBox(), pushRemote = new TextBox(), pullRemote = new TextBox(), pullLocal = new TextBox();
    readonly ComboBox history = new ComboBox();
    readonly Button execute = new Button(), stop = new Button(), terminal = new Button(), refresh = new Button(), clearShell = new Button(), saveShell = new Button();
    readonly Button fileStop = new Button(), fileRefresh = new Button();
    readonly Button push = new Button(), pull = new Button(), clearFiles = new Button(), openFolder = new Button();
    readonly Button pickFile = new Button(), pickDirectory = new Button(), pickDestination = new Button();
    readonly Button browsePush = new Button(), browsePull = new Button();
    readonly Label shellNotice = new Label(), fileNotice = new Label();
    readonly ProgressBar shellProgress = new ProgressBar(), fileProgress = new ProgressBar();
    readonly AdbText shellLog = new AdbText(), fileLog = new AdbText();
    readonly System.Windows.Forms.Timer display = new System.Windows.Forms.Timer { Interval = 100 };
    readonly bool rememberPath;
    AdbDevice[] devices = new AdbDevice[0];
    AdbDevice selected;
    CancellationTokenSource running;
    TaskCompletionSource<bool> completion;
    AdbBrowser browser;
    bool changing, shuttingDown, disposed, stopped;
    string executable, networkAddress = "";
    internal bool Busy { get { return running != null || browser != null; } }
    IEnumerable<AdbHeader> Headers { get { return new[] { shellHeader, filesHeader }; } }

    internal AdbWorkspace(string initialExecutable = null, bool remember = true)
    {
        rememberPath = remember;
        executable = initialExecutable ?? AdbLocator.Find();
        BuildShell();
        BuildFiles();
        BindEvents(); SyncHeaders(); UpdateControls(); display.Start();
    }

    void BuildShell()
    {
        var layout = UiTheme.Page(ShellPage);
        UiTheme.Heading(layout, "ADB Shell", UiTheme.Button(execute, "执行命令", true),
            UiTheme.Button(stop, "停止任务", warning: true), UiTheme.Button(terminal, "交互终端"), UiTheme.Button(refresh, "刷新设备"));
        UiTheme.Add(layout, shellHeader);
        var edit = UiTheme.Stack();
        history.DropDownStyle = ComboBoxStyle.DropDownList;
        history.FormattingEnabled = true;
        history.Format += delegate(object sender, ListControlConvertEventArgs e) { e.Value = e.ListItem.ToString().Replace("\r\n", " ↵ ").Replace("\n", " ↵ "); };
        UiTheme.Add(edit, UiTheme.Row(history, UiTheme.Label("命令历史"), history));
        FormatOutput(command, false);
        command.MaxLength = 12000; command.AcceptsReturn = true; command.Text = "pwd\r\nls -la";
        UiTheme.Add(edit, command, true);

        var output = UiTheme.Stack();
        var outputLabel = UiTheme.Label("Shell 输出 · Ctrl+Enter 执行命令");
        UiTheme.Add(output, UiTheme.Row(outputLabel, outputLabel, UiTheme.Button(clearShell, "清空输出"), UiTheme.Button(saveShell, "保存输出")));
        FormatOutput(shellOutput, true);
        UiTheme.Add(output, shellOutput, true);
        UiTheme.Add(layout, UiTheme.Split(edit, output, 230), true);
        shellNotice.Text = "先刷新并选择设备，再执行命令。切换页面可继续运行任务。";
        AddProgress(layout, shellProgress, shellNotice);
    }

    void BuildFiles()
    {
        var layout = UiTheme.Page(FilesPage);
        UiTheme.Heading(layout, "ADB 文件传输", UiTheme.Button(fileStop, "停止任务", warning: true),
            UiTheme.Button(fileRefresh, "刷新设备"));
        UiTheme.Add(layout, filesHeader);

        var send = UiTheme.Card(true);
        var sendTitle = UiTheme.Label("推送到设备"); sendTitle.Font = UiTheme.Section;
        UiTheme.Add(send, sendTitle);
        pushRemote.Text = "/sdcard/Download/";
        UiTheme.Add(send, UiTheme.Row(pushLocal, UiTheme.Label("本地来源"), pushLocal));
        UiTheme.Add(send, UiTheme.Row(null, UiTheme.Button(pickFile, "选文件"), UiTheme.Button(pickDirectory, "选目录")));
        UiTheme.Add(send, UiTheme.Row(pushRemote, UiTheme.Label("设备目标"), pushRemote, UiTheme.Button(browsePush, "浏览…")));
        UiTheme.Add(send, UiTheme.Row(null, UiTheme.Button(push, "推送到设备", true)));

        var receive = UiTheme.Card(true);
        var receiveTitle = UiTheme.Label("拉取到电脑"); receiveTitle.Font = UiTheme.Section;
        UiTheme.Add(receive, receiveTitle);
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        pullLocal.Text = Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        UiTheme.Add(receive, UiTheme.Row(pullRemote, UiTheme.Label("设备来源"), pullRemote, UiTheme.Button(browsePull, "浏览…")));
        UiTheme.Add(receive, UiTheme.Row(pullLocal, UiTheme.Label("保存目录"), pullLocal));
        UiTheme.Add(receive, UiTheme.Row(null, UiTheme.Button(pickDestination, "选择目录"), UiTheme.Button(openFolder, "打开目录"),
            UiTheme.Button(pull, "拉取到电脑", true)));
        UiTheme.Add(layout, UiTheme.Columns(true, send, receive));

        var outputLabel = UiTheme.Label("传输日志");
        UiTheme.Add(layout, UiTheme.Row(outputLabel, outputLabel, UiTheme.Button(clearFiles, "清空日志")));
        FormatOutput(fileOutput, true);
        UiTheme.Add(layout, fileOutput, true);
        fileNotice.Text = "支持文件和目录；同名目标按 ADB 规则覆盖。取消传输可能留下部分文件。";
        AddProgress(layout, fileProgress, fileNotice);
    }

    static void AddProgress(TableLayoutPanel layout, ProgressBar progress, Label notice)
    {
        progress.Height = 6; progress.Style = ProgressBarStyle.Marquee; progress.Visible = false;
        UiTheme.Add(layout, progress);
        UiTheme.Status(notice);
        UiTheme.Add(layout, notice);
    }

    static void FormatOutput(TextBox box, bool readOnly)
    {
        box.Multiline = true;
        box.ReadOnly = readOnly;
        box.BackColor = Color.White;
        box.ScrollBars = ScrollBars.Both;
        box.WordWrap = false;
        box.Font = UiTheme.Mono;
        box.MaxLength = AdbText.Limit + 1024;
    }

    void BindEvents()
    {
        foreach (var header in Headers)
        {
            var current = header;
            current.Executable.TextChanged += delegate {
                if (changing || Busy) return;
                executable = current.Executable.Text; devices = new AdbDevice[0]; selected = null; SyncHeaders(); UpdateControls();
            };
            current.Address.TextChanged += delegate { if (!changing) { networkAddress = current.Address.Text; SyncHeaders(); } };
            current.Devices.SelectedIndexChanged += delegate {
                if (!changing && !Busy) { selected = current.Devices.SelectedItem as AdbDevice; SyncHeaders(); UpdateControls(); }
            };
            current.Browse.Click += delegate { BrowseExecutable(); };
            current.Download.LinkClicked += delegate { Open("https://developer.android.com/tools/releases/platform-tools"); };
            current.Connect.Click += async delegate { await ConnectNetwork(false); };
            current.Disconnect.Click += async delegate { await ConnectNetwork(true); };
        }
        execute.Click += async delegate { await Execute(); };
        command.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Execute(); } };
        EventHandler stopTask = delegate { stopped = true; if (running != null) running.Cancel(); };
        stop.Click += stopTask; fileStop.Click += stopTask;
        EventHandler refreshList = async delegate { await RefreshDevices(); };
        refresh.Click += refreshList; fileRefresh.Click += refreshList;
        terminal.Click += delegate { OpenTerminal(); };
        push.Click += async delegate { await Transfer(true); }; pull.Click += async delegate { await Transfer(false); };
        clearShell.Click += delegate { shellLog.Clear(); Flush(); }; clearFiles.Click += delegate { fileLog.Clear(); Flush(); };
        saveShell.Click += delegate { SaveShell(); };
        openFolder.Click += delegate { if (Directory.Exists(pullLocal.Text)) Open(Path.GetFullPath(pullLocal.Text)); else Notice("本地保存目录不存在。"); };
        history.SelectedIndexChanged += delegate { if (history.SelectedItem != null && !Busy && !changing) command.Text = history.SelectedItem.ToString(); };
        pickFile.Click += delegate {
            using (var dialog = new OpenFileDialog { Filter = "所有文件|*.*" }) if (dialog.ShowDialog() == DialogResult.OK) pushLocal.Text = dialog.FileName;
        };
        pickDirectory.Click += delegate { PickFolder(pushLocal); }; pickDestination.Click += delegate { PickFolder(pullLocal); };
        browsePush.Click += delegate { BrowseRemote(true); }; browsePull.Click += delegate { BrowseRemote(false); };
        pushLocal.AllowDrop = true;
        pushLocal.DragEnter += delegate(object sender, DragEventArgs e) { e.Effect = !Busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
        pushLocal.DragDrop += delegate(object sender, DragEventArgs e) {
            if (Busy) return;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length == 1) pushLocal.Text = paths[0]; else Notice("一次拖入一个文件或目录。");
        };
        display.Tick += delegate { Flush(); };
    }

    void SyncHeaders()
    {
        changing = true;
        foreach (var header in Headers)
        {
            if (header.Executable.Text != executable) header.Executable.Text = executable;
            if (header.Address.Text != networkAddress) header.Address.Text = networkAddress;
            if (header.Devices.Items.Count != devices.Length || !header.Devices.Items.Cast<AdbDevice>().SequenceEqual(devices))
            { header.Devices.Items.Clear(); header.Devices.Items.AddRange(devices); }
            header.Devices.SelectedItem = selected;
        }
        changing = false;
    }

    void UpdateControls()
    {
        bool idle = !Busy && !shuttingDown, ready = idle && selected != null && selected.Ready;
        foreach (var header in Headers)
        {
            header.Executable.Enabled = header.Browse.Enabled = header.Devices.Enabled = header.Address.Enabled = idle;
            header.Connect.Enabled = idle; header.Disconnect.Enabled = idle && selected != null && selected.Network;
            header.State.Text = String.IsNullOrWhiteSpace(executable) ? "未找到 ADB：下载并解压 Platform Tools，再选择 adb.exe。"
                : selected != null ? selected.ToString() : devices.Length == 0 ? "点击“刷新设备”；新电脑可将 platform-tools 放在 EXE 旁。" : "请选择设备；未授权时需在设备上确认 USB 调试。";
        }
        execute.Enabled = terminal.Enabled = push.Enabled = pull.Enabled = browsePush.Enabled = browsePull.Enabled = ready;
        stop.Enabled = fileStop.Enabled = Busy && !shuttingDown; refresh.Enabled = fileRefresh.Enabled = idle;
        foreach (Control control in new Control[] { command, history, pushLocal, pushRemote, pullLocal, pullRemote, pickFile, pickDirectory, pickDestination }) control.Enabled = idle;
        shellProgress.Visible = fileProgress.Visible = Busy;
    }

    AdbDevice RequireDevice()
    {
        if (selected == null || !selected.Ready) throw new IOException("请先刷新并选择在线且已授权的设备。");
        return selected;
    }

    void Notice(string text)
    {
        if (shuttingDown) return;
        if (text.Length > 500) text = text.Substring(0, 500) + "…（完整输出见日志）";
        shellNotice.Text = fileNotice.Text = text;
    }

    async Task Perform(string name, AdbText log, Func<string, CancellationToken, Task> action, int timeout = 0)
    {
        if (Busy || shuttingDown) return;
        var cancel = new CancellationTokenSource(); running = cancel; stopped = false;
        var finished = new TaskCompletionSource<bool>(); completion = finished;
        if (timeout > 0) cancel.CancelAfter(timeout);
        UpdateControls(); Notice(name + "…");
        log.Append("\r\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + name + (selected == null ? "" : " · " + selected.Serial) + "\r\n");
        try
        {
            string path = AdbLocator.Validate(executable);
            await action(path, cancel.Token);
            Notice(name + "完成。"); log.Append("\r\n[完成]\r\n");
        }
        catch (OperationCanceledException)
        {
            string message = stopped || shuttingDown ? "已停止本次 ADB 客户端；设备端命令可能仍在运行，传输可能留下部分文件。" : "ADB 操作超时，请检查连接后刷新设备。";
            log.Append("\r\n[停止] " + message + "\r\n"); Notice(message);
        }
        catch (Exception ex) { log.Append("\r\n[错误] " + ex.Message + "\r\n"); Notice(ex.Message); }
        finally
        {
            running = null; cancel.Dispose();
            if (!shuttingDown) { Flush(); UpdateControls(); }
            finished.TrySetResult(true);
        }
    }

    Task<AdbResult> Run(string path, string[] args, CancellationToken token, AdbText log)
    {
        return AdbClient.Run(path, args, token, (text, error) => log.Append(error ? "[stderr] " + text : text));
    }

    internal Task RefreshDevices()
    {
        return Perform("刷新设备", ShellPage.Visible ? shellLog : fileLog, async (path, token) => {
            await QueryDevices(path, token, null);
            if (shuttingDown) return;
            executable = path; SyncHeaders();
            if (rememberPath)
            {
                try { AdbLocator.Remember(path); }
                catch (Exception ex) { shellLog.Append("[设置未保存] " + ex.Message + "\r\n"); }
            }
        }, 15000);
    }

    async Task QueryDevices(string path, CancellationToken token, string prefer)
    {
        string previous = selected == null ? null : selected.Serial;
        string transport = selected == null ? null : selected.Transport;
        try
        {
            var result = await AdbClient.Run(path, new[] { "devices", "-l" }, token, null);
            result.EnsureSuccess();
            if (result.Truncated) throw new IOException("设备列表输出过长，未更新列表。");
            devices = AdbClient.ParseDevices(result.Output);
            var matches = devices.Where(d => d.Serial == (prefer ?? previous)).ToArray();
            selected = matches.Length == 1 ? matches[0] : matches.FirstOrDefault(d => d.Transport != null && d.Transport == transport);
            if (selected == null && previous == null && prefer == null && devices.Count(d => d.Ready) == 1) selected = devices.Single(d => d.Ready);
        }
        catch { devices = new AdbDevice[0]; selected = null; if (!shuttingDown) SyncHeaders(); throw; }
        if (!shuttingDown) SyncHeaders();
    }

    async Task ConnectNetwork(bool disconnect)
    {
        if (Busy || shuttingDown) return;
        string address;
        try
        {
            if (disconnect && (selected == null || !selected.Network)) throw new IOException("请选择要断开的网络设备。");
            address = disconnect ? selected.Serial : AdbClient.NetworkAddress(networkAddress);
        }
        catch (Exception ex) { Notice(ex.Message); return; }
        AdbText log = ShellPage.Visible ? shellLog : fileLog;
        await Perform(disconnect ? "断开网络设备" : "连接网络设备（结果见日志）", log, async (path, token) => {
            var result = await Run(path, new[] { disconnect ? "disconnect" : "connect", address }, token, log);
            result.EnsureSuccess();
            await QueryDevices(path, token, disconnect ? null : address);
            if (!disconnect && (selected == null || !selected.Ready)) throw new IOException("连接后未发现对应的在线设备，请查看日志并确认设备的调试、授权或配对状态。");
        }, 20000);
    }

    internal async Task Execute()
    {
        if (Busy || shuttingDown) return;
        string[] args;
        string text = command.Text;
        try
        {
            var device = RequireDevice();
            if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("请输入设备 Shell 命令。");
            args = device.Target.Concat(new[] { "shell", "-T", "-n", text.Replace("\r\n", "\n") }).ToArray();
            AdbClient.Arguments(args);
        }
        catch (Exception ex) { Notice(ex.Message); return; }
        changing = true;
        try
        {
            history.Items.Remove(text);
            history.Items.Insert(0, text);
            if (history.Items.Count > 30) history.Items.RemoveAt(30);
            history.SelectedIndex = 0;
        }
        finally { changing = false; }
        shellLog.Append("\r\n$ " + text + "\r\n");
        await Perform("执行 Shell", shellLog, async (path, token) => { (await Run(path, args, token, shellLog)).EnsureSuccess(); });
    }

    internal async Task Transfer(bool pushing)
    {
        if (Busy || shuttingDown) return;
        string[] args;
        try
        {
            var device = RequireDevice();
            args = pushing ? AdbClient.Push(device, pushLocal.Text, pushRemote.Text) : AdbClient.Pull(device, pullRemote.Text, pullLocal.Text);
        }
        catch (Exception ex) { Notice(ex.Message); return; }
        fileLog.Append("\r\n" + args[args.Length - 2] + " → " + args[args.Length - 1] + "\r\n");
        await Perform(pushing ? "推送到设备" : "拉取到电脑", fileLog, async (path, token) => { (await Run(path, args, token, fileLog)).EnsureSuccess(); });
    }

    void OpenTerminal()
    {
        if (Busy || shuttingDown) return;
        try
        {
            string path = AdbLocator.Validate(executable);
            var args = RequireDevice().Target.Concat(new[] { "shell" });
            using (var process = Process.Start(new ProcessStartInfo(path, AdbClient.Arguments(args)) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) })) { }
            Notice("已打开独立的 ADB 交互终端；请在终端输入 exit 或关闭窗口结束。工具箱的停止按钮只控制内置任务。");
        }
        catch (Exception ex) { Notice(ex.Message); }
    }

    void BrowseExecutable()
    {
        using (var dialog = new OpenFileDialog { Filter = "Android Debug Bridge|adb.exe", Title = "选择完整 Platform Tools 文件夹内的 adb.exe" })
        if (dialog.ShowDialog() == DialogResult.OK) shellHeader.Executable.Text = dialog.FileName;
    }
    void BrowseRemote(bool pushing)
    {
        if (Busy || shuttingDown) return;
        try
        {
            var device = RequireDevice();
            string path = AdbLocator.Validate(executable);
            TextBox target = pushing ? pushRemote : pullRemote;
            using (var dialog = new AdbBrowser(device.ToString(), target.Text, pushing, (directory, token) => AdbFiles.Read(path, device, directory, token)))
            {
                browser = dialog; UpdateControls();
                if (dialog.ShowDialog(FilesPage.FindForm()) == DialogResult.OK && !shuttingDown)
                {
                    target.Text = dialog.SelectedPath;
                    Notice("已选择" + (pushing ? "推送目录：" : "拉取来源：") + dialog.SelectedPath + "；点击“" + (pushing ? "推送到设备" : "拉取到电脑") + "”后开始传输。");
                }
            }
        }
        catch (Exception ex) { Notice(ex.Message); }
        finally { browser = null; if (!shuttingDown) UpdateControls(); }
    }
    static void PickFolder(TextBox box)
    {
        using (var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(box.Text) ? box.Text : "" })
        if (dialog.ShowDialog() == DialogResult.OK) box.Text = dialog.SelectedPath;
    }
    void Open(string path)
    {
        try { using (var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { } }
        catch (Exception ex) { Notice(ex.Message); }
    }
    void SaveShell()
    {
        using (var dialog = new SaveFileDialog { Filter = "文本日志|*.txt", FileName = "adb-shell-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" })
        if (dialog.ShowDialog() == DialogResult.OK)
        try { File.WriteAllText(dialog.FileName, shellLog.Read(false), new UTF8Encoding(true)); Notice("Shell 输出已保存。"); }
        catch (Exception ex) { Notice(ex.Message); }
    }
    void Flush()
    {
        Flush(shellLog, shellOutput); Flush(fileLog, fileOutput);
    }
    static void Flush(AdbText log, TextBox output)
    {
        string text = log.Read(true);
        if (text == null || output.IsDisposed) return;
        output.Text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        output.SelectionStart = output.TextLength; output.ScrollToCaret();
    }
    internal Task Shutdown()
    {
        var dialog = browser;
        if (!shuttingDown) { shuttingDown = true; display.Stop(); if (running != null) running.Cancel(); if (dialog != null) dialog.Close(); }
        return Task.WhenAll(completion == null ? Task.FromResult(true) : completion.Task, dialog == null ? Task.FromResult(true) : dialog.Completion);
    }
    public void Dispose()
    {
        if (disposed) return;
        Shutdown(); disposed = true; display.Dispose();
    }
}
