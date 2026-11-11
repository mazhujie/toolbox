using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

internal sealed class ProjectWorkspace : UserControl
{
    readonly ProjectStore store;
    readonly AdbWorkspace adb;
    readonly Action openAdb;
    readonly ComboBox projects = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    readonly WorkspaceTabs tabs = new WorkspaceTabs(), historyTabs = new WorkspaceTabs();
    readonly TextBox done = TextArea(), blocked = TextArea(), next = TextArea(), command = TextArea(), output = TextArea();
    readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly ListBox commands = List(), runs = List(), checkpoints = List(), journals = List();
    readonly TextBox runText = TextArea(), checkpointText = TextArea(), journalTitle = new TextBox { Dock = DockStyle.Fill };
    readonly TextBox search = new TextBox { Dock = DockStyle.Fill };
    readonly JournalEditor journalText = new JournalEditor { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MaxLength = 2000000 };
    readonly FlowLayoutPanel images = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 32, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label location = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label commandHint = Label("");
    readonly Button run = Button("运行"), stop = Button("停止"), save = Button("保存"), paste = Button("粘贴截图");
    readonly Timer autosave = new Timer { Interval = 1200 }, display = new Timer { Interval = 100 };
    readonly AdbText localOutput = new AdbText();
    WorkProject current;
    WorkJournal journal;
    bool loading, shuttingDown;
    CancellationTokenSource localCancel;
    Task localTask = Task.FromResult(true);
    internal ProjectStore Store { get { return store; } }
    internal bool LocalBusy { get { return localCancel != null; } }

    internal ProjectWorkspace(ProjectStore store, AdbWorkspace adb, Action openAdb)
    {
        this.store = store; this.adb = adb; this.openAdb = openAdb;
        Dock = DockStyle.Fill; Padding = new Padding(20, 16, 20, 12); BackColor = Color.FromArgb(245, 247, 251);
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, WrapContents = false };
        header.Controls.Add(new Label { Text = "当前项目", Width = 76, Height = 30, TextAlign = ContentAlignment.MiddleLeft }); header.Controls.Add(projects);
        header.Controls.Add(ActionButton("新建项目", NewProject)); header.Controls.Add(ActionButton("项目设置", EditProject));
        header.Controls.Add(save); save.Click += delegate { SaveNow(); };
        tabs.Add("现场", BuildScene()); tabs.Add("命令", BuildCommands()); tabs.Add("历史", BuildHistory()); tabs.Add("日志", BuildJournal());
        Controls.Add(tabs); Controls.Add(header); Controls.Add(status);
        projects.SelectedIndexChanged += delegate {
            if (loading || projects.SelectedItem == null) return;
            if (!SaveNow()) { loading = true; projects.SelectedItem = current; loading = false; return; }
            current = (WorkProject)projects.SelectedItem; store.Data.SelectedProject = current.Id; LoadProject(); SaveNow();
        };
        foreach (var box in new TextBox[] { done, blocked, next, command, journalTitle, journalText }) box.TextChanged += delegate { MarkDirty(); };
        mode.SelectedIndexChanged += delegate {
            if (!loading && current != null && Convert.ToString(mode.SelectedItem) != "ADB Shell") current.TerminalMode = Convert.ToString(mode.SelectedItem);
            UpdateCommandHint(); MarkDirty();
        };
        tabs.SelectedChanged += delegate(string name) { store.Data.Page = name; MarkDirty(); };
        autosave.Tick += delegate { autosave.Stop(); SaveNow(); };
        display.Tick += delegate { string text = localOutput.Read(true); if (text != null) { output.Text = WindowsLines(text); output.SelectionStart = output.TextLength; output.ScrollToCaret(); } };
        display.Start(); store.Changed += RefreshHistory;
        RefreshProjects(); current = store.Current; LoadProject(); tabs.Select(store.Data.Page);
    }

    static TextBox TextArea() { return new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MaxLength = 2000000 }; }
    static string WindowsLines(string text) { return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n"); }
    static ListBox List() { return new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true }; }
    static Button Button(string text) { return new Button { Text = text, AutoSize = true, Height = 32, Margin = new Padding(0, 2, 8, 2), FlatStyle = FlatStyle.Flat, BackColor = Color.White }; }
    static Button ActionButton(string text, Action action) { var button = Button(text); button.Click += delegate { action(); }; return button; }
    static FlowLayoutPanel Toolbar(params Control[] controls)
    { var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true }; bar.Controls.AddRange(controls); return bar; }
    static TableLayoutPanel Rows(params float[] heights)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length, Margin = Padding.Empty, Padding = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (float height in heights) table.RowStyles.Add(new RowStyle(height < 0 ? SizeType.Percent : SizeType.Absolute, Math.Abs(height)));
        return table;
    }
    static Control Split(Control left, Control right)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); table.Controls.Add(left, 0, 0); table.Controls.Add(right, 1, 0); return table;
    }
    static Label Label(string text) { return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }; }
    Control BuildScene()
    {
        var table = Rows(34, 40, 27, -33, 27, -33, 27, -34, 42);
        table.Controls.Add(location, 0, 0);
        table.Controls.Add(Toolbar(ActionButton("启动项目", Launch), ActionButton("打开目录", () => Guard(() => OpenDirectory())), ActionButton("ADB 调试", openAdb)), 0, 1);
        table.Controls.Add(Label("已完成"), 0, 2); table.Controls.Add(done, 0, 3);
        table.Controls.Add(Label("遇到的问题 / 当前卡点"), 0, 4); table.Controls.Add(blocked, 0, 5);
        table.Controls.Add(Label("下一步 · 下次打开时从这里接着做"), 0, 6); table.Controls.Add(next, 0, 7);
        table.Controls.Add(Toolbar(ActionButton("保存收工断点", Checkpoint), ActionButton("写一篇调试日志", NewJournal)), 0, 8); return table;
    }
    Control BuildCommands()
    {
        mode.Items.AddRange(new object[] { "ADB Shell", "WSL", "Windows PowerShell" }); mode.SelectedIndex = 0;
        var table = Rows(40, 48, -35, 40, -65); output.ReadOnly = true; output.BackColor = Color.FromArgb(24, 35, 55); output.ForeColor = Color.FromArgb(225, 234, 248);
        table.Controls.Add(Toolbar(mode, run, stop, ActionButton("环境设置", EditProject)), 0, 0);
        table.Controls.Add(commandHint, 0, 1);
        table.Controls.Add(command, 0, 2);
        table.Controls.Add(Toolbar(ActionButton("保存为常用命令", SaveCommand), ActionButton("删除所选命令", RemoveCommand)), 0, 3);
        table.Controls.Add(output, 0, 4);
        commands.SelectedIndexChanged += delegate { if (loading || commands.SelectedItem == null) return; var selected = (WorkCommand)commands.SelectedItem; command.Text = WindowsLines(selected.Text); mode.SelectedItem = selected.Mode; };
        run.Click += async delegate { await ExecuteCommand(); }; stop.Enabled = false; stop.Click += delegate { if (localCancel != null) localCancel.Cancel(); };
        command.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ExecuteCommand(); } };
        return Split(commands, table);
    }
    Control BuildHistory()
    {
        runText.ReadOnly = checkpointText.ReadOnly = true;
        var runPanel = Rows(-100, 40); runPanel.Controls.Add(runText, 0, 0);
        runPanel.Controls.Add(Toolbar(ActionButton("记为问题日志", JournalFromRun), ActionButton("完整输出", () => Guard(() => { var record = runs.SelectedItem as WorkRun; if (record != null) Open(store.FilePath(record.Log)); }))), 0, 1);
        var checkpointPanel = Rows(-100, 40); checkpointPanel.Controls.Add(checkpointText, 0, 0);
        checkpointPanel.Controls.Add(Toolbar(ActionButton("恢复到现场", RestoreCheckpoint)), 0, 1);
        historyTabs.Add("调试记录", Split(runs, runPanel)); historyTabs.Add("收工断点", Split(checkpoints, checkpointPanel));
        runs.SelectedIndexChanged += delegate { if (runs.SelectedItem != null) Guard(() => runText.Text = WindowsLines(store.ReadRun((WorkRun)runs.SelectedItem))); else runText.Clear(); };
        checkpoints.SelectedIndexChanged += delegate { var item = checkpoints.SelectedItem as WorkCheckpoint; checkpointText.Text = item == null ? "" : item.Created + "\r\n\r\n已完成\r\n" + item.Done + "\r\n\r\n卡点\r\n" + item.Blocked + "\r\n\r\n下一步\r\n" + item.Next; };
        return historyTabs;
    }
    Control BuildJournal()
    {
        var table = Rows(33, 26, -100, 100, 42);
        table.Controls.Add(journalTitle, 0, 0); table.Controls.Add(Label("记录现象、排查过程和结论；截图可直接 Ctrl+V 粘贴。"), 0, 1);
        table.Controls.Add(journalText, 0, 2); table.Controls.Add(images, 0, 3);
        table.Controls.Add(Toolbar(ActionButton("新日志", NewJournal), paste, ActionButton("添加图片", PickImage), ActionButton("保存日志", () => SaveNow())), 0, 4);
        paste.Click += delegate { PasteImage(); }; journalText.PasteImage = PasteImage;
        journals.SelectedIndexChanged += delegate {
            if (loading) return;
            var selected = journals.SelectedItem as WorkJournal;
            if (!SaveNow()) { loading = true; journals.SelectedItem = journal; loading = false; return; }
            journal = selected; current.JournalId = journal == null ? "" : journal.Id; RefreshJournals(); LoadJournal(); MarkDirty();
        };
        var listing = Rows(26, 32, -100); listing.Controls.Add(Label("搜索日志标题 / 正文"), 0, 0); listing.Controls.Add(search, 0, 1); listing.Controls.Add(journals, 0, 2);
        search.TextChanged += delegate { if (!loading) RefreshJournals(); };
        return Split(listing, table);
    }
    void MarkDirty() { if (loading || shuttingDown) return; status.Text = "正在编辑…"; autosave.Stop(); autosave.Start(); }
    internal bool SaveNow()
    {
        autosave.Stop(); if (current == null) return true;
        try
        {
            current.Done = done.Text; current.Blocked = blocked.Text; current.Next = next.Text; current.Draft = command.Text; current.Mode = Convert.ToString(mode.SelectedItem);
            if (journal != null)
            {
                current.JournalId = journal.Id;
                bool changed = journal.Title != journalTitle.Text || journal.Text != journalText.Text;
                journal.Title = journalTitle.Text; journal.Text = journalText.Text; if (changed) journal.Updated = ProjectStore.Now();
            }
            store.Save(); status.ForeColor = Color.FromArgb(53, 97, 73); status.Text = "已保存到本机 · " + DateTime.Now.ToString("HH:mm:ss");
            RefreshJournals(); return true;
        }
        catch (Exception ex) { Error("保存失败，内容仍在窗口中：" + ex.Message); return false; }
    }
    void RefreshProjects()
    {
        loading = true; projects.Items.Clear(); projects.Items.AddRange(store.Data.Projects.Where(p => !p.Archived).Cast<object>().ToArray()); projects.SelectedItem = store.Current; loading = false;
    }
    void LoadProject()
    {
        loading = true;
        WslClient.Infer(current);
        done.Text = WindowsLines(current.Done); blocked.Text = WindowsLines(current.Blocked); next.Text = WindowsLines(current.Next); command.Text = WindowsLines(current.Draft);
        mode.SelectedItem = current.Mode; if (mode.SelectedIndex < 0) mode.SelectedIndex = 0;
        location.Text = String.IsNullOrWhiteSpace(current.Directory) ? "尚未设置项目目录 · 可先写日志，之后在“项目设置”中补充。" : current.Directory;
        commands.Items.Clear(); commands.Items.AddRange(current.Commands.Cast<object>().ToArray());
        search.Clear(); journal = store.Data.Journals.FirstOrDefault(j => j.ProjectId == current.Id && j.Id == current.JournalId) ?? store.Data.Journals.FirstOrDefault(j => j.ProjectId == current.Id); RefreshJournals(); LoadJournal();
        loading = false; RefreshHistory(); adb.SetProjectName(current.Name); UpdateCommandHint();
    }
    void UpdateCommandHint()
    {
        string selected = Convert.ToString(mode.SelectedItem);
        if (selected == WslClient.Mode && current != null)
        {
            WslClient.Infer(current);
            commandHint.Text = "WSL · " + (String.IsNullOrWhiteSpace(current.WslDistribution) ? "请在环境设置选择发行版" : current.WslDistribution) + "\r\n" + (String.IsNullOrWhiteSpace(current.WslDirectory) ? "沿用项目目录" : current.WslDirectory) + " · Bash 登录配置";
        }
        else commandHint.Text = selected == "ADB Shell" ? "直接填写板端命令，无需加 adb shell。" : "在项目目录执行 Windows PowerShell 脚本。";
    }
    void RefreshJournals()
    {
        bool previous = loading; loading = true;
        string query = search.Text.Trim();
        journals.BeginUpdate(); journals.Items.Clear(); journals.Items.AddRange(store.Data.Journals.Where(j => j.ProjectId == current.Id &&
            ((j.Title ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (j.Text ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).OrderByDescending(j => j.Updated).Cast<object>().ToArray());
        journals.SelectedItem = journal; journals.EndUpdate(); loading = previous;
    }
    void LoadJournal()
    {
        bool previous = loading; loading = true;
        journalTitle.Text = journal == null ? "" : journal.Title; journalText.Text = journal == null ? "" : WindowsLines(journal.Text);
        journalTitle.Enabled = journalText.Enabled = journal != null; loading = previous; RefreshImages();
    }
    void RefreshHistory()
    {
        if (current == null || IsDisposed || shuttingDown) return;
        var selected = runs.SelectedItem; runs.BeginUpdate(); runs.Items.Clear(); runs.Items.AddRange(store.Data.Runs.Where(r => r.ProjectId == current.Id).Cast<object>().ToArray());
        if (selected != null && runs.Items.Contains(selected)) runs.SelectedItem = selected; else if (runs.Items.Count > 0) runs.SelectedIndex = 0; runs.EndUpdate();
        selected = checkpoints.SelectedItem; checkpoints.Items.Clear(); checkpoints.Items.AddRange(store.Data.Checkpoints.Where(c => c.ProjectId == current.Id).Cast<object>().ToArray());
        if (selected != null && checkpoints.Items.Contains(selected)) checkpoints.SelectedItem = selected; else if (checkpoints.Items.Count > 0) checkpoints.SelectedIndex = 0;
    }
    void RefreshImages()
    {
        foreach (Control control in images.Controls.Cast<Control>().ToArray())
        { images.Controls.Remove(control); var picture = control as PictureBox; if (picture != null && picture.Image != null) picture.Image.Dispose(); control.Dispose(); }
        if (journal == null) return;
        foreach (string relative in journal.Images)
        {
            string attachment = relative;
            try
            {
                using (var source = Image.FromFile(store.FilePath(relative)))
                {
                    var thumbnail = new Bitmap(112, 80);
                    using (var graphics = Graphics.FromImage(thumbnail))
                    {
                        graphics.Clear(Color.White); float scale = Math.Min(112f / source.Width, 80f / source.Height);
                        float width = source.Width * scale, height = source.Height * scale;
                        graphics.DrawImage(source, (112 - width) / 2, (80 - height) / 2, width, height);
                    }
                    var picture = new PictureBox { Width = 112, Height = 80, SizeMode = PictureBoxSizeMode.Zoom, Image = thumbnail, Cursor = Cursors.Hand, BorderStyle = BorderStyle.FixedSingle };
                    var menu = new ContextMenuStrip(); menu.Items.Add("打开原图", null, delegate { Guard(() => Open(store.FilePath(attachment))); });
                    menu.Items.Add("移除此附件", null, delegate {
                        if (MessageBox.Show(this, "从这篇日志移除截图？原图仍保留在数据目录中。", "移除附件", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                        journal.Images.Remove(attachment); SaveNow(); RefreshImages();
                    }); picture.ContextMenuStrip = menu; picture.Disposed += delegate { menu.Dispose(); };
                    picture.Click += delegate { Guard(() => Open(store.FilePath(attachment))); }; images.Controls.Add(picture);
                }
            }
            catch (Exception ex) { images.Controls.Add(new Label { Text = "图片无法读取\r\n" + Path.GetFileName(relative), Width = 112, Height = 80 }); Error(ex.Message); }
        }
    }
    void NewProject()
    {
        if (!SaveNow()) return;
        var project = new WorkProject(); using (var dialog = new ProjectDialog(project)) if (dialog.ShowDialog(this) != DialogResult.OK) return;
        store.Data.Projects.Add(project); store.Data.SelectedProject = project.Id; current = project; RefreshProjects(); LoadProject(); SaveNow();
    }
    void EditProject()
    {
        if (!SaveNow()) return;
        using (var dialog = new ProjectDialog(current)) if (dialog.ShowDialog(this) != DialogResult.OK) return;
        RefreshProjects(); LoadProject(); SaveNow();
    }
    void Checkpoint()
    {
        if (!SaveNow()) return;
        store.Data.Checkpoints.Insert(0, new WorkCheckpoint { ProjectId = current.Id, Done = current.Done, Blocked = current.Blocked, Next = current.Next });
        if (SaveNow()) { RefreshHistory(); status.Text = "收工断点已保存，下次打开即可接着做。"; }
    }
    void RestoreCheckpoint()
    {
        var checkpoint = checkpoints.SelectedItem as WorkCheckpoint; if (checkpoint == null || !SaveNow()) return;
        if (MessageBox.Show(this, "恢复这份断点到现场？当前现场会先保存成新的断点。", "恢复断点", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        Checkpoint(); done.Text = checkpoint.Done; blocked.Text = checkpoint.Blocked; next.Text = checkpoint.Next; SaveNow(); tabs.Select("现场");
    }
    internal void NewJournal()
    {
        if (!SaveNow()) return;
        search.Clear(); journal = store.NewJournal(current.Id); journal.Text = "问题现象：\r\n\r\n排查过程：\r\n\r\n处理结果 / 下一步：\r\n";
        LoadJournal(); SaveNow(); tabs.Select("日志"); journalTitle.Focus(); journalTitle.SelectAll();
    }
    void JournalFromRun()
    {
        var record = runs.SelectedItem as WorkRun; if (record == null || !SaveNow()) return;
        Guard(() => {
            string text = store.ReadRun(record); journal = store.NewJournal(current.Id); journal.RunId = record.Id; journal.Title = "调试问题 · " + record.Target;
            journal.Text = "问题现象：\r\n\r\n排查记录：\r\n" + (text.Length > 16000 ? text.Substring(0, 16000) + "\r\n[完整输出见历史中的原始记录]" : text) + "\r\n\r\n结论 / 下一步：\r\n";
            LoadJournal(); SaveNow(); tabs.Select("日志");
        });
    }
    internal void PasteImage()
    {
        Guard(() => {
            if (!Clipboard.ContainsImage()) throw new IOException("剪贴板里没有图片，请先截图或复制图片。");
            using (var image = Clipboard.GetImage()) { if (image == null) throw new IOException("未能读取剪贴板图片，请再复制一次。"); AddImage(image); }
        });
    }
    internal void AddImage(Image image)
    {
        if (journal == null) NewJournal(); if (journal == null || !SaveNow()) return;
        store.AddImage(journal, image); RefreshImages(); status.Text = "截图已保存 · 点击缩略图查看原图，右键可移除。";
    }
    void PickImage()
    {
        using (var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Multiselect = true })
        if (dialog.ShowDialog(this) == DialogResult.OK) Guard(() => { foreach (string path in dialog.FileNames) using (var image = Image.FromFile(path)) AddImage(image); });
    }
    void SaveCommand()
    {
        if (String.IsNullOrWhiteSpace(command.Text)) { Error("请先输入命令。"); return; }
        string name = command.Lines.FirstOrDefault() ?? "命令"; if (name.Length > 50) name = name.Substring(0, 50);
        var item = new WorkCommand { Name = name, Text = command.Text, Mode = Convert.ToString(mode.SelectedItem) };
        current.Commands.Add(item); commands.Items.Add(item); SaveNow();
    }
    void RemoveCommand()
    {
        var item = commands.SelectedItem as WorkCommand; if (item == null) return;
        if (MessageBox.Show(this, "删除常用命令“" + item.Name + "”？", "删除命令", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        current.Commands.Remove(item); commands.Items.Remove(item); SaveNow();
    }
    internal async Task ExecuteCommand()
    {
        if (LocalBusy || shuttingDown || !SaveNow()) return;
        if (String.IsNullOrWhiteSpace(command.Text)) { Error("请先输入命令。"); return; }
        if (Convert.ToString(mode.SelectedItem) == "ADB Shell")
        {
            if (adb.Busy) { Error("ADB 正在执行任务，请等待或在 ADB 页停止任务。"); return; }
            adb.PrepareCommand(command.Text); openAdb(); await adb.Execute(); return;
        }
        localTask = RunLocal(current, command.Text); await localTask;
    }
    static string PowerShell { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"); } }
    internal static string LocalScript(string directory, string text)
    {
        return "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=New-Object System.Text.UTF8Encoding; $OutputEncoding=[Console]::OutputEncoding; Set-Location -LiteralPath '" + directory.Replace("'", "''") + "'; & {\r\n" + text + "\r\n}; if (-not $?) { exit 1 }; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }";
    }
    async Task RunLocal(WorkProject project, string text)
    {
        RunRecording recording = null; int? exit = null; string result = "失败";
        bool wsl = project.Mode == WslClient.Mode;
        string distribution = project.WslDistribution, directory = project.Directory;
        try
        {
            if (wsl) { directory = WslClient.DirectoryFor(project); WslClient.TerminalArguments(distribution, directory); }
            else if (!Directory.Exists(directory)) throw new IOException("请先在项目设置中选择存在的项目目录。");
            recording = store.Begin(project.Id, text, wsl ? "WSL · " + distribution : "Windows PowerShell", directory);
            localCancel = new CancellationTokenSource(); run.Enabled = false; stop.Enabled = true; localOutput.Clear();
            Action<string, bool> receive = (chunk, error) => { recording.Append(chunk, error); localOutput.Append(error ? "[stderr] " + chunk : chunk); };
            var response = wsl ? await WslClient.Run(distribution, directory, text, localCancel.Token, receive)
                : await AdbClient.Run(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-Command", LocalScript(directory, text) }, localCancel.Token, receive, true);
            exit = response.ExitCode; result = exit == 0 ? "成功" : "失败";
        }
        catch (OperationCanceledException) { result = "已停止"; }
        catch (Exception ex) { Error(ex.Message); localOutput.Append("\r\n[错误] " + ex.Message); if (recording != null) recording.Append("\r\n[错误] " + ex.Message, true); }
        finally
        {
            if (recording != null) Guard(() => recording.Finish(result, exit));
            if (localCancel != null) { localCancel.Dispose(); localCancel = null; }
            run.Enabled = true; stop.Enabled = false; localOutput.Append("\r\n[" + result + "]" + (exit.HasValue ? " 退出码 " + exit : "") + "\r\n");
        }
    }
    void OpenDirectory() { if (!Directory.Exists(current.Directory)) throw new IOException("项目目录不存在，请修改项目设置。"); Open(current.Directory); }
    void Launch()
    {
        if (!SaveNow()) return;
        Guard(() => {
            OpenDirectory();
            if (!String.IsNullOrWhiteSpace(current.Editor)) using (var process = Process.Start(new ProcessStartInfo(current.Editor, AdbClient.Quote(current.Directory)) { UseShellExecute = true })) { }
            if (current.Terminal)
            {
                if (current.TerminalMode == WslClient.Mode)
                    using (var process = Process.Start(new ProcessStartInfo(WslClient.Executable, WslClient.Arguments(WslClient.TerminalArguments(current.WslDistribution, WslClient.DirectoryFor(current)))) { UseShellExecute = true })) { }
                else
                {
                    string script = "Set-Location -LiteralPath '" + current.Directory.Replace("'", "''") + "'";
                    using (var process = Process.Start(new ProcessStartInfo(PowerShell, "-NoLogo -NoProfile -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) { UseShellExecute = true })) { }
                }
            }
            foreach (string link in current.Urls.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            { Uri uri; if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http")) throw new IOException("网址需以 http:// 或 https:// 开头。"); Open(uri.AbsoluteUri); }
            status.Text = "项目目录、编辑器、终端和网址已按设置打开。";
        });
    }
    static void Open(string path) { using (var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { } }
    void Guard(Action action) { try { action(); } catch (Exception ex) { Error(ex.Message); } }
    void Error(string message) { status.ForeColor = Color.Firebrick; status.Text = message; }
    internal async Task Shutdown()
    {
        shuttingDown = true; autosave.Stop(); display.Stop(); if (localCancel != null) localCancel.Cancel(); await localTask;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { store.Changed -= RefreshHistory; autosave.Dispose(); display.Dispose(); if (localCancel != null) localCancel.Cancel(); foreach (PictureBox picture in images.Controls.OfType<PictureBox>()) if (picture.Image != null) picture.Image.Dispose(); }
        base.Dispose(disposing);
    }
}
