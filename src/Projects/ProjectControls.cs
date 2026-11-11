using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class WorkspaceTabs : UserControl
{
    readonly FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, WrapContents = false };
    readonly Panel body = new Panel { Dock = DockStyle.Fill };
    readonly Dictionary<string, Control> views = new Dictionary<string, Control>();
    readonly Dictionary<string, Button> buttons = new Dictionary<string, Button>();
    readonly Font large = new Font("Microsoft YaHei UI", 12, FontStyle.Bold), small = new Font("Microsoft YaHei UI", 10);
    internal string Selected { get; private set; }
    internal event Action<string> SelectedChanged;
    internal WorkspaceTabs() { Dock = DockStyle.Fill; Controls.Add(body); Controls.Add(bar); }
    internal void Add(string name, Control view)
    {
        view.Dock = DockStyle.Fill; view.Visible = false; body.Controls.Add(view); views.Add(name, view);
        var button = new Button { Text = name, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 0) };
        button.FlatAppearance.BorderSize = 0; button.Click += delegate { Select(name); }; buttons.Add(name, button); bar.Controls.Add(button);
        if (Selected == null) Select(name);
    }
    internal void Select(string name)
    {
        if (!views.ContainsKey(name)) return;
        Selected = name;
        foreach (var item in buttons)
        {
            bool active = item.Key == name; var button = item.Value;
            button.Font = active ? large : small; button.Height = active ? 44 : 34;
            button.Width = TextRenderer.MeasureText(item.Key, button.Font).Width + (active ? 36 : 24);
            button.Margin = new Padding(0, active ? 0 : 5, 8, 0);
            button.BackColor = active ? Color.FromArgb(49, 102, 215) : Color.FromArgb(231, 237, 247);
            button.ForeColor = active ? Color.White : Color.FromArgb(55, 69, 91);
            views[item.Key].Visible = active;
        }
        views[name].BringToFront(); if (SelectedChanged != null) SelectedChanged(name);
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) { large.Dispose(); small.Dispose(); } }
}

internal sealed class JournalEditor : TextBox
{
    internal Action PasteImage;
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x302 && PasteImage != null)
        {
            try { if (Clipboard.ContainsImage()) { PasteImage(); return; } }
            catch (System.Runtime.InteropServices.ExternalException) { PasteImage(); return; }
        }
        base.WndProc(ref message);
    }
}

internal sealed class ProjectDialog : Form
{
    readonly TextBox name = new TextBox(), directory = new TextBox(), editor = new TextBox(), urls = new TextBox();
    readonly CheckBox terminal = new CheckBox { Text = "启动项目时打开此终端", AutoSize = true };
    readonly ComboBox terminalMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, distributions = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox wslDirectory = new TextBox();
    readonly Button refresh = new Button { Text = "读取配置" };
    readonly Label notice = new Label { ForeColor = Color.DimGray };
    readonly CancellationTokenSource discovery = new CancellationTokenSource();
    readonly Func<CancellationToken, Task<string[]>> readDistributions;
    bool released;
    readonly WorkProject project;
    internal ProjectDialog(WorkProject project, Func<CancellationToken, Task<string[]>> readDistributions = null)
    {
        this.readDistributions = readDistributions ?? WslClient.Distributions;
        this.project = project; Text = "项目设置"; Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(640, 660); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        Add("项目名称", name, 20); Add("项目目录", directory, 75); Add("编辑器 EXE（可留空）", editor, 130);
        directory.Width = editor.Width = 470;
        var folder = new Button { Text = "浏览…", Left = 510, Top = 96, Width = 100 };
        folder.Click += delegate { using (var dialog = new FolderBrowserDialog()) if (dialog.ShowDialog(this) == DialogResult.OK) directory.Text = dialog.SelectedPath; }; Controls.Add(folder);
        var exe = new Button { Text = "浏览…", Left = 510, Top = 151, Width = 100 };
        exe.Click += delegate { using (var dialog = new OpenFileDialog { Filter = "程序|*.exe" }) if (dialog.ShowDialog(this) == DialogResult.OK) editor.Text = dialog.FileName; }; Controls.Add(exe);
        Controls.Add(new Label { Text = "本地终端", Left = 20, Top = 202, Width = 90, Height = 28 });
        terminalMode.SetBounds(110, 200, 220, 28); terminalMode.Items.AddRange(new object[] { "WSL", "Windows PowerShell" }); Controls.Add(terminalMode);
        terminal.SetBounds(350, 200, 270, 30); Controls.Add(terminal);
        Controls.Add(new Label { Text = "WSL 发行版（使用该发行版配置的默认用户）", Left = 20, Top = 245, Width = 590, Height = 23 });
        distributions.SetBounds(20, 270, 470, 28); Controls.Add(distributions); refresh.SetBounds(510, 268, 100, 32); Controls.Add(refresh);
        Add("WSL 工作目录（例如 /home/developer/work；留空沿用项目目录）", wslDirectory, 315);
        Controls.Add(new Label { Text = "命令使用 Bash 登录环境，读取 .bash_profile 或 .profile。\r\n交互式配置和别名请在独立终端使用，或在命令里显式 source 脚本。", Left = 20, Top = 376, Width = 590, Height = 48, ForeColor = Color.DimGray });
        Add("常用网址（每行一个 http / https 链接）", urls, 435); urls.Multiline = true; urls.Height = 95; urls.ScrollBars = ScrollBars.Vertical;
        notice.SetBounds(20, 565, 590, 42); Controls.Add(notice);
        var save = new Button { Text = "保存", Left = 400, Top = 615, Width = 100, Height = 34 };
        var cancel = new Button { Text = "取消", Left = 510, Top = 615, Width = 100, Height = 34, DialogResult = DialogResult.Cancel };
        Controls.Add(save); Controls.Add(cancel); AcceptButton = save; CancelButton = cancel;
        name.Text = project.Name; directory.Text = project.Directory; editor.Text = project.Editor; urls.Text = project.Urls; terminal.Checked = project.Terminal;
        terminalMode.SelectedItem = project.TerminalMode;
        if (terminalMode.SelectedIndex < 0) terminalMode.SelectedItem = "Windows PowerShell";
        string inferredDistribution, inferredDirectory;
        WslClient.TryUnc(project.Directory, out inferredDistribution, out inferredDirectory);
        string selectedDistribution = String.IsNullOrWhiteSpace(project.WslDistribution) ? inferredDistribution : project.WslDistribution;
        if (!String.IsNullOrWhiteSpace(selectedDistribution)) { distributions.Items.Add(selectedDistribution); distributions.SelectedIndex = 0; }
        wslDirectory.Text = String.IsNullOrWhiteSpace(project.WslDirectory) ? inferredDirectory : project.WslDirectory;
        refresh.Click += async delegate { await ReadWsl(); }; Shown += async delegate { await ReadWsl(); };
        FormClosed += delegate { if (!released) discovery.Cancel(); };
        save.Click += delegate {
            if (String.IsNullOrWhiteSpace(name.Text)) { MessageBox.Show(this, "请填写项目名称。"); return; }
            foreach (string link in urls.Lines.Where(s => !String.IsNullOrWhiteSpace(s)))
            { Uri uri; if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http")) { MessageBox.Show(this, "网址需以 http:// 或 https:// 开头。"); return; } }
            if (Convert.ToString(terminalMode.SelectedItem) == WslClient.Mode)
            {
                try
                {
                    var candidate = new WorkProject { Directory = directory.Text.Trim(), WslDistribution = Convert.ToString(distributions.SelectedItem), WslDirectory = wslDirectory.Text.Trim() };
                    WslClient.TerminalArguments(candidate.WslDistribution, WslClient.DirectoryFor(candidate));
                }
                catch (Exception ex) { notice.Text = ex.Message; return; }
            }
            string selectedMode = Convert.ToString(terminalMode.SelectedItem);
            if (selectedMode != project.TerminalMode) project.Mode = selectedMode;
            project.TerminalMode = selectedMode; project.WslDistribution = Convert.ToString(distributions.SelectedItem); project.WslDirectory = wslDirectory.Text.Trim();
            project.Name = name.Text.Trim(); project.Directory = directory.Text.Trim(); project.Editor = editor.Text.Trim(); project.Urls = urls.Text; project.Terminal = terminal.Checked;
            DialogResult = DialogResult.OK;
        };
    }
    internal async Task ReadWsl()
    {
        if (!refresh.Enabled || IsDisposed) return;
        refresh.Enabled = false; notice.Text = "正在读取本机 WSL 发行版…";
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(discovery.Token))
            {
                timeout.CancelAfter(10000);
                string previous = Convert.ToString(distributions.SelectedItem);
                var names = await readDistributions(timeout.Token); if (IsDisposed) return;
                distributions.Items.Clear(); distributions.Items.AddRange(names);
                if (names.Contains(previous)) distributions.SelectedItem = previous;
                else if (String.IsNullOrWhiteSpace(previous) && names.Length == 1) distributions.SelectedIndex = 0;
                notice.Text = names.Length == 0 ? "未找到已安装的 WSL 发行版。" : previous.Length > 0 && !names.Contains(previous) ? "原发行版已不存在，请重新选择。" : "已读取 " + names.Length + " 个发行版；WSL 和用户配置保持原样。";
            }
        }
        catch (OperationCanceledException) { if (!IsDisposed) notice.Text = "读取 WSL 配置超时，可点击“读取配置”重试。"; }
        catch (Exception ex) { if (!IsDisposed) notice.Text = ex.Message; }
        finally { if (!IsDisposed) refresh.Enabled = true; }
    }
    protected override void Dispose(bool disposing) { if (disposing && !released) { released = true; discovery.Cancel(); discovery.Dispose(); } base.Dispose(disposing); }
    void Add(string title, TextBox input, int y)
    { Controls.Add(new Label { Text = title, Left = 20, Top = y, Width = 590, Height = 23 }); input.SetBounds(20, y + 23, 590, 28); Controls.Add(input); }
}
