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
        SetBounds(24, 78, 744, 160);
        Label("ADB 程序", 0, 4); Executable.SetBounds(96, 0, 552, 28); Controls.Add(Executable);
        Browse.Text = "选择…"; Browse.SetBounds(656, 0, 88, 28); Controls.Add(Browse);
        Label("设备", 0, 46); Devices.SetBounds(96, 40, 648, 30); Devices.DropDownStyle = ComboBoxStyle.DropDownList; Controls.Add(Devices);
        Label("网络地址", 0, 86); Address.SetBounds(96, 80, 370, 28); Controls.Add(Address);
        Connect.Text = "连接设备"; Connect.SetBounds(478, 80, 126, 30); Controls.Add(Connect);
        Disconnect.Text = "断开网络设备"; Disconnect.SetBounds(612, 80, 132, 30); Controls.Add(Disconnect);
        State.SetBounds(0, 120, 566, 38); State.ForeColor = Color.DimGray; Controls.Add(State);
        Download.Text = "下载官方 Platform Tools"; Download.SetBounds(566, 122, 178, 30); Controls.Add(Download);
    }
    void Label(string text, int x, int y) { Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y) }); }
}

internal sealed class AdbWorkspace : IDisposable
{
    internal readonly UserControl ShellPage = new UserControl(), FilesPage = new UserControl();
    readonly AdbHeader shellHeader = new AdbHeader(), filesHeader = new AdbHeader();
    readonly TextBox command = new TextBox(), shellOutput = new TextBox(), fileOutput = new TextBox();
    readonly TextBox pushLocal = new TextBox(), pushRemote = new TextBox(), pullRemote = new TextBox(), pullLocal = new TextBox();
    readonly ComboBox history = new ComboBox();
    readonly Button execute = new Button(), stop = new Button(), terminal = new Button(), refresh = new Button(), clearShell = new Button(), saveShell = new Button();
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
    internal ProjectStore Projects;
    readonly Label projectLabel = new Label { Location = new Point(340, 32), Size = new Size(428, 28), TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true };
    RunRecording activeRecording;
    int? recordedExit;
    internal void SetProjectName(string name) { projectLabel.Text = "记录归档到项目：" + name; }
    internal void PrepareCommand(string text) { if (!Busy) command.Text = text; }
    internal bool Busy { get { return running != null || browser != null; } }
    internal Button[] ShellActions { get { return new[] { execute, stop, terminal, refresh, clearShell, saveShell }; } }
    internal Button[] FileActions { get { return new[] { push, pull, stop, refresh, openFolder, clearFiles }; } }
    IEnumerable<AdbHeader> Headers { get { return new[] { shellHeader, filesHeader }; } }

    internal AdbWorkspace(string initialExecutable = null, bool remember = true)
    {
        rememberPath = remember;
        executable = initialExecutable ?? AdbLocator.Find();
        Page(ShellPage, "ADB Shell", shellHeader, shellProgress, shellNotice);
        ShellPage.Controls.Add(projectLabel); projectLabel.BringToFront();
        Page(FilesPage, "ADB 文件传输", filesHeader, fileProgress, fileNotice);
        Label(ShellPage, "设备 Shell 命令", 24, 250);
        Label(ShellPage, "历史", 296, 250);
        history.SetBounds(348, 244, 420, 30); history.DropDownStyle = ComboBoxStyle.DropDownList; ShellPage.Controls.Add(history);
        history.FormattingEnabled = true;
        history.Format += delegate(object sender, ListControlConvertEventArgs e) { e.Value = e.ListItem.ToString().Replace("\r\n", " ↵ ").Replace("\n", " ↵ "); };
        Text(ShellPage, command, 24, 284, 744, 80, true);
        command.MaxLength = 12000; command.AcceptsReturn = true; command.Text = "pwd\r\nls -la";
        Label(ShellPage, "Ctrl+Enter 执行；每次为独立 shell。top / vi 请使用左侧“交互终端”。", 24, 374);
        Text(ShellPage, shellOutput, 24, 408, 744, 256, true); shellOutput.ReadOnly = true; shellOutput.BackColor = Color.White;
        shellNotice.Text = "先刷新并选择设备，再执行命令。切换页面不停止当前任务。";

        Label(FilesPage, "推送到设备  ·  push", 24, 248);
        Label(FilesPage, "本地来源", 24, 288); Text(FilesPage, pushLocal, 120, 282, 444, 28, false);
        Button(FilesPage, pickFile, "选文件", 576, 282, 90); Button(FilesPage, pickDirectory, "选目录", 678, 282, 90);
        Label(FilesPage, "设备目标", 24, 330); Text(FilesPage, pushRemote, 120, 324, 546, 28, false); pushRemote.Text = "/sdcard/Download/";
        Button(FilesPage, browsePush, "浏览…", 678, 324, 90);
        Label(FilesPage, "拉取到电脑  ·  pull", 24, 376);
        Label(FilesPage, "设备来源", 24, 416); Text(FilesPage, pullRemote, 120, 410, 546, 28, false);
        Button(FilesPage, browsePull, "浏览…", 678, 410, 90);
        Label(FilesPage, "保存目录", 24, 458); Text(FilesPage, pullLocal, 120, 452, 546, 28, false);
        Button(FilesPage, pickDestination, "选目录", 678, 452, 90);
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        pullLocal.Text = Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Label(FilesPage, "支持文件和目录；同名目标按 ADB 规则覆盖。取消传输可能留下部分文件。", 24, 494);
        Text(FilesPage, fileOutput, 24, 528, 744, 136, true); fileOutput.ReadOnly = true; fileOutput.BackColor = Color.White;
        fileNotice.Text = "点击“浏览…”选择板端文件或目录；本地来源也可拖入文件或目录。";
        execute.Text = "执行命令"; stop.Text = "停止当前任务"; terminal.Text = "打开交互终端";
        refresh.Text = "刷新设备"; clearShell.Text = "清空 Shell 输出"; saveShell.Text = "保存 Shell 输出";
        push.Text = "推送到设备"; pull.Text = "拉取到电脑"; clearFiles.Text = "清空传输日志"; openFolder.Text = "打开保存目录";
        BindEvents(); SyncHeaders(); UpdateControls(); display.Start();
    }

    static void Page(UserControl page, string title, AdbHeader header, ProgressBar progress, Label notice)
    {
        page.BackColor = Color.FromArgb(245, 247, 251); page.Font = new Font("Microsoft YaHei UI", 10); page.AutoScroll = true;
        page.Controls.Add(new Label { Text = title, Font = new Font(page.Font.FontFamily, 21, FontStyle.Bold), Location = new Point(24, 20), Size = new Size(740, 44) });
        page.Controls.Add(header); progress.SetBounds(24, 682, 744, 6); progress.Style = ProgressBarStyle.Marquee; progress.Visible = false; page.Controls.Add(progress);
        notice.SetBounds(24, 700, 744, 48); notice.ForeColor = Color.DimGray; page.Controls.Add(notice);
    }
    static void Label(Control page, string text, int x, int y) { page.Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y) }); }
    static void Text(Control page, TextBox box, int x, int y, int width, int height, bool multiline)
    {
        box.SetBounds(x, y, width, height); box.Multiline = multiline; box.MaxLength = AdbText.Limit + 1024;
        if (multiline) { box.ScrollBars = ScrollBars.Both; box.WordWrap = false; box.Font = new Font("Consolas", 10); }
        page.Controls.Add(box);
    }
    static void Button(Control page, Button button, string text, int x, int y, int width)
    { button.Text = text; button.SetBounds(x, y, width, 30); page.Controls.Add(button); }

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
        stop.Click += delegate { stopped = true; if (running != null) running.Cancel(); };
        refresh.Click += async delegate { await RefreshDevices(); };
        terminal.Click += delegate { OpenTerminal(); };
        push.Click += async delegate { await Transfer(true); }; pull.Click += async delegate { await Transfer(false); };
        clearShell.Click += delegate { shellLog.Clear(); Flush(); }; clearFiles.Click += delegate { fileLog.Clear(); Flush(); };
        saveShell.Click += delegate { SaveShell(); };
        openFolder.Click += delegate { if (Directory.Exists(pullLocal.Text)) Open(Path.GetFullPath(pullLocal.Text)); else Notice("本地保存目录不存在。"); };
        history.SelectedIndexChanged += delegate { if (history.SelectedItem != null && !Busy) command.Text = history.SelectedItem.ToString(); };
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
                : selected != null ? selected.ToString() : devices.Length == 0 ? "点击左侧“刷新设备”；新电脑可将 platform-tools 放在 EXE 旁。" : "请选择设备；未授权时需在设备上确认 USB 调试。";
        }
        execute.Enabled = terminal.Enabled = push.Enabled = pull.Enabled = browsePush.Enabled = browsePull.Enabled = ready;
        stop.Enabled = Busy && !shuttingDown; refresh.Enabled = idle;
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

    async Task Perform(string name, AdbText log, Func<string, CancellationToken, Task> action, int timeout = 0, Func<RunRecording> beginRecording = null)
    {
        if (Busy || shuttingDown) return;
        var cancel = new CancellationTokenSource(); running = cancel; stopped = false;
        var finished = new TaskCompletionSource<bool>(); completion = finished;
        if (timeout > 0) cancel.CancelAfter(timeout);
        UpdateControls(); Notice(name + "…");
        log.Append("\r\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + name + (selected == null ? "" : " · " + selected.Serial) + "\r\n");
        string outcome = "失败"; recordedExit = null;
        try
        {
            string path = AdbLocator.Validate(executable);
            if (beginRecording != null) activeRecording = beginRecording();
            await action(path, cancel.Token);
            outcome = "成功";
            Notice(name + "完成。"); log.Append("\r\n[完成]\r\n");
        }
        catch (OperationCanceledException)
        {
            outcome = "已停止";
            string message = stopped || shuttingDown ? "已停止本次 ADB 客户端；设备端命令可能仍在运行，传输可能留下部分文件。" : "ADB 操作超时，请检查连接后刷新设备。";
            log.Append("\r\n[停止] " + message + "\r\n"); Notice(message);
        }
        catch (Exception ex) { log.Append("\r\n[错误] " + ex.Message + "\r\n"); if (activeRecording != null) activeRecording.Append("\r\n[错误] " + ex.Message, true); Notice(ex.Message); }
        finally
        {
            if (activeRecording != null)
            {
                try { activeRecording.Finish(outcome, recordedExit); }
                catch (Exception ex) { log.Append("\r\n[记录保存失败] " + ex.Message); Notice("记录保存失败：" + ex.Message); }
                activeRecording = null;
            }
            running = null; cancel.Dispose();
            if (!shuttingDown) { Flush(); UpdateControls(); }
            finished.TrySetResult(true);
        }
    }

    async Task<AdbResult> Run(string path, string[] args, CancellationToken token, AdbText log)
    {
        var result = await AdbClient.Run(path, args, token, (text, error) => {
            log.Append(error ? "[stderr] " + text : text);
            if (activeRecording != null) activeRecording.Append(text, error);
        });
        if (activeRecording != null) recordedExit = result.ExitCode;
        return result;
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
        string target = "";
        try
        {
            var device = RequireDevice();
            target = "ADB · " + device.Serial + " · transport " + device.Transport;
            if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("请输入设备 Shell 命令。");
            args = device.Target.Concat(new[] { "shell", "-T", "-n", text.Replace("\r\n", "\n") }).ToArray();
            AdbClient.Arguments(args);
        }
        catch (Exception ex) { Notice(ex.Message); return; }
        history.Items.Remove(text); history.Items.Insert(0, text); if (history.Items.Count > 30) history.Items.RemoveAt(30); history.SelectedIndex = 0;
        shellLog.Append("\r\n$ " + text + "\r\n");
        var project = Projects == null ? null : Projects.Current;
        await Perform("执行 Shell", shellLog, async (path, token) => { (await Run(path, args, token, shellLog)).EnsureSuccess(); }, 0,
            project == null ? (Func<RunRecording>)null : () => Projects.Begin(project.Id, text, target, project.Directory));
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
