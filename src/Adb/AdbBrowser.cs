using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class AdbEntry
{
    internal string Name, Path, Kind;
    internal bool Directory { get { return Kind == "D" || Kind == "d"; } }
    internal bool Selectable { get { return Directory || Kind == "F" || Kind == "f"; } }
    internal string Description { get { return Kind == "D" ? "目录" : Kind == "d" ? "目录链接" : Kind == "F" ? "文件" : Kind == "f" ? "文件链接" : Kind == "L" ? "无效链接" : "特殊文件"; } }
}

internal sealed class AdbDirectory
{
    internal string Path;
    internal AdbEntry[] Entries;
    internal bool Limited;
    internal int Skipped;
}

internal static class AdbFiles
{
    internal const int MaxEntries = 1000;
    internal static string Parent(string path)
    {
        path = AdbClient.RemotePath(path).TrimEnd('/');
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path.Substring(0, slash);
    }
    internal static string Join(string directory, string name) { return directory.TrimEnd('/') + "/" + name; }

    internal static string Command(string path)
    {
        // Use POSIX shell builtins and NUL-separated fields, not human-formatted ls output.
        // User paths are a single quoted literal; filenames are never evaluated as shell code.
        string literal = "'" + AdbClient.RemotePath(path).Replace("'", "'\"'\"'") + "'";
        return "# toolbox-directory-v1\np=" + literal + @"
if [ ! -d ""$p"" ] && { [ -e ""$p"" ] || [ -L ""$p"" ]; }; then
    p=${p%/*}; [ -n ""$p"" ] || p=/
fi
cd ""$p"" || exit 1
if [ ! -r . ] || [ ! -x . ]; then printf '%s\n' 'Permission denied: directory is not readable/searchable' >&2; exit 1; fi
dir=$(pwd -P) || exit 1
printf '%s\000' TB_ADB_DIR_V1 ""$dir""
n=0
for f in ./* ./.[!.]* ./..?*; do
    [ -e ""$f"" ] || [ -L ""$f"" ] || continue
    if [ ""$n"" -ge 1000 ]; then printf 'E\0001\000'; exit 0; fi
    t=O
    if [ -d ""$f"" ]; then t=D; [ ! -L ""$f"" ] || t=d
    elif [ -f ""$f"" ]; then t=F; [ ! -L ""$f"" ] || t=f
    elif [ -L ""$f"" ]; then t=L
    fi
    printf '%s\000%s\000' ""$t"" ""${f#./}""
    n=$((n+1))
done
printf 'E\0000\000'
".Replace("\r\n", "\n");
    }

    internal static AdbDirectory Parse(string output)
    {
        string[] fields = output.Split('\0');
        if (fields.Length < 5 || fields.Length % 2 != 1 || fields[0] != "TB_ADB_DIR_V1" || fields[fields.Length - 1] != "" ||
            fields[fields.Length - 3] != "E" || (fields[fields.Length - 2] != "0" && fields[fields.Length - 2] != "1"))
            throw new IOException("设备目录返回格式无效或不完整，未显示可能错误的路径。");
        string directory = AdbClient.RemotePath(fields[1]);
        if (directory.IndexOf('\uFFFD') >= 0 || directory.Any(Char.IsControl)) throw new IOException("目录名称包含不支持的字符或不是有效 UTF-8。");
        var entries = new List<AdbEntry>(); var names = new HashSet<string>(StringComparer.Ordinal);
        int skipped = 0;
        for (int i = 2; i < fields.Length - 3; i += 2)
        {
            string kind = fields[i], name = fields[i + 1];
            if (!new[] { "D", "d", "F", "f", "L", "O" }.Contains(kind) || String.IsNullOrEmpty(name) || name == "." || name == ".." || name.Contains("/") || !names.Add(name))
                throw new IOException("设备目录包含无效条目，未显示可能错误的路径。");
            if (names.Count > MaxEntries) throw new IOException("目录条目过多，请输入更深层目录后重试。");
            if (name.IndexOf('\uFFFD') >= 0 || name.Any(Char.IsControl)) { skipped++; continue; }
            entries.Add(new AdbEntry { Name = name, Path = Join(directory, name), Kind = kind });
        }
        return new AdbDirectory { Path = directory, Entries = entries.OrderByDescending(e => e.Directory).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray(), Limited = fields[fields.Length - 2] == "1", Skipped = skipped };
    }

    internal static async Task<AdbDirectory> Read(string executable, AdbDevice device, string path, CancellationToken token)
    {
        var args = device.Target.Concat(new[] { "shell", "-T", "-n", Command(path) }).ToArray();
        var result = await AdbClient.Run(executable, args, token, null).ConfigureAwait(false);
        result.EnsureSuccess();
        if (result.Truncated) throw new IOException("目录输出超过上限，请输入更深层目录后重试。");
        return Parse(result.Output);
    }
}

internal sealed class AdbBrowser : Form
{
    readonly TextBox address = new TextBox(), filter = new TextBox();
    readonly ListView entries = new ListView();
    readonly CheckBox hidden = new CheckBox();
    readonly Button go = new Button(), up = new Button(), root = new Button(), refresh = new Button(), choose = new Button(), current = new Button(), close = new Button();
    readonly Label status = new Label();
    readonly Func<string, CancellationToken, Task<AdbDirectory>> read;
    readonly bool directoriesOnly;
    readonly string initial;
    readonly TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>();
    AdbDirectory listing;
    CancellationTokenSource running;
    bool closing;
    internal string SelectedPath { get; private set; }
    internal bool Busy { get { return running != null; } }
    internal Task Completion { get { return closed.Task; } }

    internal AdbBrowser(string device, string initialPath, bool onlyDirectories, Func<string, CancellationToken, Task<AdbDirectory>> reader)
    {
        read = reader; directoriesOnly = onlyDirectories;
        initial = String.IsNullOrWhiteSpace(initialPath) ? "/" : initialPath;
        Text = onlyDirectories ? "浏览板端目录 · 选择推送目标" : "浏览板端文件 · 选择拉取来源";
        Font = new Font("Microsoft YaHei UI", 10); ClientSize = new Size(820, 600); MinimumSize = new Size(740, 550);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false;
        BackColor = Color.FromArgb(245, 247, 251);
        Controls.Add(new Label { Text = "设备：" + device, Location = new Point(20, 16), Size = new Size(780, 28), AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right });
        address.SetBounds(20, 56, 510, 28); address.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        address.AutoCompleteMode = AutoCompleteMode.SuggestAppend; address.AutoCompleteSource = AutoCompleteSource.CustomSource; Controls.Add(address);
        Add(go, "前往", 542, 56, 78); Add(up, "上一级", 632, 56, 78); Add(root, "根目录 /", 722, 56, 78);
        Controls.Add(new Label { Text = "筛选", Location = new Point(20, 102), AutoSize = true });
        filter.SetBounds(66, 96, 370, 28); filter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(filter);
        hidden.Text = "显示隐藏项"; hidden.SetBounds(464, 96, 168, 30); hidden.Anchor = AnchorStyles.Top | AnchorStyles.Right; Controls.Add(hidden);
        Add(refresh, "刷新", 700, 96, 100);
        entries.SetBounds(20, 138, 780, 342); entries.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        entries.View = View.Details; entries.FullRowSelect = true; entries.MultiSelect = false; entries.HideSelection = false; entries.ShowItemToolTips = true;
        entries.Columns.Add("名称", 590); entries.Columns.Add("类型", 160); Controls.Add(entries);
        status.SetBounds(20, 492, 780, 44); status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; status.ForeColor = Color.DimGray; Controls.Add(status);
        Add(current, "使用当前目录", 20, 548, 154); current.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Add(choose, onlyDirectories ? "选择此目录" : "选择此文件 / 目录", 502, 548, 186); choose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Add(close, "关闭", 700, 548, 100); close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        CancelButton = close;
        go.Click += async delegate { await Navigate(address.Text); };
        up.Click += async delegate { await Navigate(AdbFiles.Parent(listing == null ? address.Text : listing.Path)); };
        root.Click += async delegate { await Navigate("/"); };
        refresh.Click += async delegate { await Navigate(listing == null ? address.Text : listing.Path); };
        address.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Navigate(address.Text); } };
        address.TextChanged += delegate { UpdateControls(); };
        filter.TextChanged += delegate { Populate(); }; hidden.CheckedChanged += delegate { Populate(); };
        entries.SelectedIndexChanged += delegate { UpdateControls(); };
        entries.ItemActivate += async delegate {
            var item = Selected();
            if (item == null || !Ready()) return;
            if (item.Directory) await Navigate(item.Path); else if (!directoriesOnly && item.Selectable) Accept(item.Path);
        };
        choose.Click += delegate { var item = Selected(); if (Ready() && item != null && item.Selectable && (!directoriesOnly || item.Directory)) Accept(item.Path); };
        current.Click += delegate { if (Ready()) Accept(listing.Path); };
        close.Click += delegate { Close(); };
        Shown += async delegate { await Navigate(initial); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { closing = true; if (Busy) { e.Cancel = true; running.Cancel(); status.Text = "正在停止目录读取…"; } };
        FormClosed += delegate { closed.TrySetResult(true); };
        UpdateControls();
    }

    void Add(Button button, string text, int x, int y, int width)
    { button.Text = text; button.SetBounds(x, y, width, 34); button.Anchor = AnchorStyles.Top | AnchorStyles.Right; Controls.Add(button); }
    AdbEntry Selected() { return entries.SelectedItems.Count == 0 ? null : entries.SelectedItems[0].Tag as AdbEntry; }
    bool Ready() { return !Busy && !closing && listing != null && address.Text == listing.Path; }
    void UpdateControls()
    {
        bool idle = !Busy && !closing;
        address.Enabled = go.Enabled = root.Enabled = refresh.Enabled = idle;
        up.Enabled = idle && listing != null && listing.Path != "/";
        filter.Enabled = hidden.Enabled = entries.Enabled = Ready(); current.Enabled = Ready();
        var item = Selected(); choose.Enabled = Ready() && item != null && item.Selectable && (!directoriesOnly || item.Directory);
    }

    internal async Task Navigate(string path)
    {
        if (Busy || closing) return;
        var cancel = new CancellationTokenSource(15000); running = cancel;
        listing = null; entries.Items.Clear(); address.AutoCompleteCustomSource.Clear(); address.Text = path;
        status.Text = "正在读取设备目录…"; UpdateControls();
        try
        {
            var loaded = await read(path, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (closing || IsDisposed) return;
            listing = loaded; address.Text = listing.Path; filter.Clear();
            var suggestions = new AutoCompleteStringCollection();
            suggestions.AddRange(listing.Entries.Where(e => e.Directory || !directoriesOnly && e.Selectable).Select(e => e.Path + (e.Directory ? "/" : "")).ToArray());
            address.AutoCompleteCustomSource = suggestions;
            Populate();
        }
        catch (OperationCanceledException) { status.Text = closing ? "目录读取已停止。" : "读取超时，请检查设备连接后重试。"; }
        catch (Exception ex) { status.Text = ex.Message.Length > 260 ? ex.Message.Substring(0, 260) + "…" : ex.Message; }
        finally
        {
            running = null; cancel.Dispose();
            if (!IsDisposed) { UpdateControls(); if (closing) Close(); }
        }
    }

    void Populate()
    {
        entries.BeginUpdate(); entries.Items.Clear();
        if (listing != null)
        {
            foreach (var item in listing.Entries.Where(e => (hidden.Checked || !e.Name.StartsWith(".")) && e.Name.IndexOf(filter.Text, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var row = new ListViewItem(new[] { item.Name, item.Description }) { Tag = item, ToolTipText = item.Path };
                row.ForeColor = !item.Selectable || directoriesOnly && !item.Directory ? Color.Gray : item.Directory ? Color.FromArgb(35, 96, 207) : Color.Black;
                entries.Items.Add(row);
            }
            status.Text = (listing.Limited ? "目录超过 1,000 项，仅列出前 1,000 项。" : "")
                + (listing.Skipped > 0 ? "已跳过 " + listing.Skipped + " 个含控制字符或无效 UTF-8 的名称。" : "")
                + "显示 " + entries.Items.Count + " / " + listing.Entries.Length + " 项。双击目录进入，地址栏回车前往；Tab 可接受当前目录的路径补全。";
        }
        entries.EndUpdate(); UpdateControls();
    }
    void Accept(string path)
    {
        SelectedPath = directoriesOnly && path != "/" ? path.TrimEnd('/') + "/" : path;
        DialogResult = DialogResult.OK; Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { closing = true; if (running != null) running.Cancel(); closed.TrySetResult(true); }
        base.Dispose(disposing);
    }
}
