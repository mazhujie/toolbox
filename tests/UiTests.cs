using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Threading;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

internal static class ToolboxUiTests
{
    static readonly StringBuilder report = new StringBuilder();
    static readonly string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results");
    static FakePacketSource packetSource;
    static bool failCaptureCatalog;

    static T Field<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
    }

    static async Task Until(Func<bool> ready, string name)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.ElapsedMilliseconds > 4000) throw new TimeoutException(name);
            await Task.Delay(20);
        }
    }

    static void Capture(Form form, string name)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, name));
        }
    }

    static void ToggleAdvanced(LinkLabel link)
    {
        typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(link, new object[] { new LinkLabelLinkClickedEventArgs(link.Links[0]) });
    }

    static Options IpRoute(IpPage page)
    {
        return (Options)typeof(IpPage).GetMethod("ReadRoute", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, null);
    }

    static async Task Exercise(SpeedForm form, SerialSession session, Func<FakeSerialTransport> current, Func<SerialSettings> applied)
    {
        var shell = Field<ToolShell>(form, "shell");
        var workspace = Field<SerialWorkspace>(form, "serialWorkspace");
        var actions = Field<FlowLayoutPanel>(shell, "actions");
        var ports = Field<ComboBox>(workspace, "ports");
        var outgoing = Field<TextBox>(workspace, "outgoing");
        var incoming = Field<TextBox>(workspace, "incoming");
        var connect = Field<Button>(workspace, "connect");
        var advanced = Field<LinkLabel>(workspace, "advancedToggle");
        var automatic = Field<Button>(workspace, "automatic");
        var timer = Field<Timer>(workspace, "sendTimer");

        foreach (var id in new[] { "speed", "ip", "lan", "serial", "socket", "capture", "adb-shell", "adb-files", "speed" })
        {
            shell.ShowView(id);
            await Task.Delay(30);
            Assert(shell.CurrentView == id && actions.Controls.Count >= 3, "navigation: " + id);
            Assert(actions.Controls.Cast<Control>().All(c => c.Parent == actions), "left action placement");
            Capture(form, "preview-" + id + ".png");
        }
        report.AppendLine("PASS eight pages, four independent categories and left action placement");

        var ip = Field<IpPage>(form, "ipPage");
        var routeButton = Field<Button>(ip, "settings");
        var routeMenu = Field<ContextMenuStrip>(ip, "routeMenu");
        var proxy = Field<TextBox>(ip, "proxy");
        shell.ShowView("ip");
        Assert(IpRoute(ip).Mode == 1, "IP defaults to Windows system proxy");
        Field<ComboBox>(form, "mode").SelectedIndex = 2;
        Field<TextBox>(form, "proxy").Text = "http://127.0.0.1:19999";
        Assert(IpRoute(ip).Mode == 1 && IpRoute(ip).Proxy != "http://127.0.0.1:19999", "IP route independent from download settings");
        Field<ComboBox>(form, "mode").SelectedIndex = 0;
        routeButton.PerformClick();
        Assert(routeMenu.Visible && ((ToolStripMenuItem)routeMenu.Items[1]).Checked, "connection menu and default selection");
        routeMenu.Items[2].PerformClick();
        routeMenu.Close();
        Assert(proxy.Visible && proxy.Enabled && IpRoute(ip).Mode == 2, "manual proxy editor in IP page");
        proxy.Text = " https://127.0.0.1:7888 ";
        Field<Button>(ip, "query").PerformClick();
        Assert(Field<Label>(ip, "summary").Text.Contains("代理格式"), "reject invalid proxy before networking");
        proxy.Text = " http://127.0.0.1:7888 ";
        Assert(IpRoute(ip).Proxy == "http://127.0.0.1:7888", "manual route input applied and trimmed");
        Capture(form, "preview-ip-manual.png");
        Field<IpCard>(ip, "proxied").ShowInfo(new IpInfo { Ip = "203.0.113.99", Risk = 55 });
        routeMenu.Items[0].PerformClick();
        Assert(!proxy.Visible && IpRoute(ip).Mode == 0 && Field<TextBox>(Field<IpCard>(ip, "proxied"), "address").Text == "—", "route change clears stale proxy result");
        routeMenu.Items[1].PerformClick();
        routeButton.Focus();
        Capture(form, "preview-ip.png");
        report.AppendLine("PASS IP system-proxy default, independent route menu, manual validation and stale-result reset");
        var directCard = Field<IpCard>(ip, "direct"); var proxyCard = Field<IpCard>(ip, "proxied");
        var home = new IpInfo { Ip = "203.0.113.8", Location = "模拟归属地", Isp = "模拟宽带运营商", Provider = "测试数据" };
        var hosting = new IpInfo { Ip = "203.0.113.9", Location = "模拟归属地", Isp = "模拟机房运营商", Provider = "测试数据" };
        IpLookup.ApplyScamalytics(home, IpTests.Page(home.Ip, 0, "cable", "No"));
        IpLookup.ApplyScamalytics(hosting, IpTests.Page(hosting.Ip));
        directCard.ShowInfo(home); proxyCard.ShowInfo(hosting);
        Assert(Field<Label>(directCard, "networkType").Text == "IP 类型：疑似住宅 IP" && Field<Label>(proxyCard, "networkType").Text == "IP 类型：机房 IP", "both exit cards show separate network types");
        Assert(Field<TextBox>(directCard, "details").Text.Contains("类型依据：Scamalytics") && Field<TextBox>(directCard, "details").Text.Contains("连接类型：cable"), "type source and raw connection value visible");
        Capture(form, "preview-ip-types-demo.png");
        routeMenu.Items[2].PerformClick();
        Assert(Field<Label>(proxyCard, "networkType").Text == "IP 类型：未知", "route change clears stale IP type");
        proxyCard.ShowInfo(new IpInfo { Ip = "203.0.113.10", Datacenter = false });
        Assert(Field<Label>(proxyCard, "networkType").Text == "IP 类型：未知", "negative hosting flag alone remains unknown in UI");
        directCard.Reset("点击查询"); routeMenu.Items[1].PerformClick();
        report.AppendLine("PASS direct/proxy IP type labels, evidence, conservative unknown state and route-reset cleanup");

        typeof(SpeedForm).GetMethod("Busy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { true });
        Assert(!Field<Button>(form, "start").Enabled, "moved speed action busy state");
        Assert(!routeButton.Enabled && !proxy.Enabled, "IP route changes locked while network task runs");
        routeMenu.Items[0].PerformClick();
        Assert(IpRoute(ip).Mode == 1, "busy route cannot change through menu");
        shell.ShowView("lan");
        Assert(!Field<LanPage>(form, "lanPage").Actions[0].Enabled, "moved LAN action busy state");
        typeof(SpeedForm).GetMethod("Busy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { false });
        Assert(Field<LanPage>(form, "lanPage").Actions[0].Enabled, "LAN action restored");
        report.AppendLine("PASS network mutual exclusion survives navigation");

        await ExerciseSockets(form, shell);
        await ExerciseCapture(form, shell);
        await ExerciseAdb(form, shell);

        shell.ShowView("serial");
        Assert(Field<FlowLayoutPanel>(shell, "pages").Controls.Count == 1, "one serial page");
        ports.SelectedIndex = -1;
        Assert(!connect.Enabled, "connect unavailable without a port");
        ports.Items.Clear();
        ports.Items.Add("COM99");
        ports.SelectedItem = "COM99";
        Assert(connect.Enabled, "port selection enables connect");
        int sendTop = outgoing.PointToScreen(Point.Empty).Y;
        Assert(!Field<bool>(workspace, "advancedExpanded"), "advanced parameters initially collapsed");
        ToggleAdvanced(advanced);
        Assert(Field<Panel>(workspace, "advancedPanel").Visible && outgoing.PointToScreen(Point.Empty).Y == sendTop, "advanced parameters expand without moving send area");
        Capture(form, "preview-serial-advanced.png");
        Field<ComboBox>(workspace, "baud").Text = "9600";
        Field<ComboBox>(workspace, "bits").SelectedItem = "7";
        Field<ComboBox>(workspace, "parity").SelectedItem = "Even";
        Field<ComboBox>(workspace, "stops").SelectedItem = "2";
        Field<ComboBox>(workspace, "flow").SelectedIndex = 2;
        Field<CheckBox>(workspace, "dtr").Checked = true;
        Assert(!Field<CheckBox>(workspace, "rts").Enabled && Field<Label>(workspace, "config").Text.Contains("7E2"), "advanced parameter summary and RTS flow control");
        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "open simulated port");
        Assert(!ports.Enabled && connect.Text == "断开串口", "connection toggle and settings lock");
        Assert(applied().Baud == 9600 && applied().DataBits == 7 && applied().Parity == System.IO.Ports.Parity.Even && applied().StopBits == System.IO.Ports.StopBits.Two && applied().Dtr, "advanced settings reach serial transport");
        connect.PerformClick();
        await Until(() => !session.IsOpen && connect.Enabled && ports.Enabled, "disconnect via same button");
        Assert(current().Disposed && connect.Text == "连接串口", "disconnect releases transport and restores connect action");
        Field<ComboBox>(workspace, "baud").Text = "115200";
        Field<ComboBox>(workspace, "bits").SelectedItem = "8";
        Field<ComboBox>(workspace, "parity").SelectedItem = "None";
        Field<ComboBox>(workspace, "stops").SelectedItem = "1";
        Field<ComboBox>(workspace, "flow").SelectedIndex = 0;
        Field<CheckBox>(workspace, "dtr").Checked = false;
        ToggleAdvanced(advanced);
        Field<Button>(workspace, "clear").PerformClick();
        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "reconnect after editing settings");
        report.AppendLine("PASS inline serial settings, advanced foldout, applied parameters and single connect/disconnect action");

        shell.ShowView("serial");
        outgoing.Text = "界面演示：模拟串口回环，不代表真实设备数据。";
        Field<ComboBox>(workspace, "newline").SelectedItem = "CRLF";
        Field<Button>(workspace, "send").PerformClick();
        await Until(() => incoming.Text.Contains("RX") && incoming.Text.Contains("界面演示"), "text send/receive");
        Assert(current().Writes == 1, "one manual transmission");
        Assert(incoming.Text.Contains("TX") && !incoming.Text.Contains("�"), "UTF-8 receive display");
        Capture(form, "preview-serial-demo.png");

        outgoing.Text = "AA BB 0D 0A";
        Field<CheckBox>(workspace, "txHex").Checked = true;
        Field<CheckBox>(workspace, "rxHex").Checked = true;
        Field<Button>(workspace, "send").PerformClick();
        await Until(() => current().Writes == 2 && incoming.Text.Contains("AA BB 0D 0A"), "HEX send/receive");
        report.AppendLine("PASS actual UI open, settings lock, text/HEX send and receive (simulated transport)");

        await ExerciseClearRecords(workspace, session, current);

        await ExerciseMultiSend(form, workspace, session, current);

        Field<NumericUpDown>(workspace, "interval").Value = 100;
        automatic.PerformClick();
        await Until(() => current().Writes >= 4, "periodic transmission");
        Assert(timer.Enabled && !outgoing.Enabled, "periodic payload lock");
        shell.ShowView("speed");
        int before = current().Writes;
        await Until(() => current().Writes >= before + 2, "background periodic transmission");
        Assert(session.IsOpen, "connection survives network navigation");
        Field<Button>(shell, "serial").PerformClick();
        Assert(shell.CurrentView == "serial", "remember serial subpage");
        automatic.PerformClick();
        await Until(() => !Field<bool>(workspace, "writing"), "finish in-flight write");
        before = current().Writes;
        await Task.Delay(260);
        Assert(current().Writes == before && !timer.Enabled && outgoing.Enabled, "periodic stop");
        report.AppendLine("PASS periodic sends, page switching, remembered subpage and stop");

        shell.ShowView("speed");
        current().Fault();
        await Until(() => !session.IsOpen && current().Disposed, "background device failure cleanup");
        shell.ShowView("serial");
        await Until(() => connect.Enabled && connect.Text == "连接串口", "recover disconnected UI");
        Assert(incoming.Text.Contains("fixture disconnect"), "device error visible");
        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "reopen after failure");
        current().FailWrite = true;
        Field<Button>(workspace, "send").PerformClick();
        await Until(() => !session.IsOpen && connect.Enabled, "send failure closes port");
        Assert(!timer.Enabled && current().Disposed, "write error cleanup");
        report.AppendLine("PASS device disconnect on hidden page, reconnect and write failure cleanup");

        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "final open");
        automatic.PerformClick();
        await Until(() => current().Writes > 0, "final periodic send");
        // Main closes this actual form while periodic sending is active.
    }

    static bool ProcessExited(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    static async Task ExerciseAdb(SpeedForm form, ToolShell shell)
    {
        var adb = Field<AdbWorkspace>(form, "adbWorkspace");
        var header = Field<AdbHeader>(adb, "shellHeader");
        var filesHeader = Field<AdbHeader>(adb, "filesHeader");
        var input = Field<TextBox>(adb, "command");
        var outputBox = Field<TextBox>(adb, "shellOutput");
        var notice = Field<Label>(adb, "shellNotice");
        string executable = header.Executable.Text, root = Path.GetDirectoryName(executable);
        report.Append(await AdbTests.Run(executable));
        shell.ShowView("adb-shell");
        header.Executable.Text = "";
        Assert(filesHeader.Executable.Text == "" && !Field<Button>(adb, "execute").Enabled && header.State.Text.Contains("未找到"), "missing ADB setup state shared across pages");
        Capture(form, "preview-adb-missing.png");
        await adb.RefreshDevices();
        Assert(!adb.Busy && !Field<Button>(adb, "execute").Enabled, "missing executable fails without running a command");
        header.Executable.Text = executable;
        await adb.RefreshDevices();
        Assert(header.Devices.Items.Count == 3 && ((AdbDevice)header.Devices.SelectedItem).Serial == "DEMO-USB", "single ready device auto-selected");
        Assert(ReferenceEquals(header.Devices.SelectedItem, filesHeader.Devices.SelectedItem), "Shell and transfer share selected device");
        header.Devices.SelectedIndex = 1;
        Assert(!Field<Button>(adb, "execute").Enabled && !Field<Button>(adb, "push").Enabled && header.State.Text.Contains("未授权"), "unauthorized device cannot execute or transfer");
        header.Devices.SelectedIndex = 2;
        Assert(!Field<Button>(adb, "execute").Enabled && header.State.Text.Contains("离线"), "offline device cannot execute");
        header.Devices.SelectedIndex = 0;
        report.AppendLine("PASS ADB missing-tool setup, shared configuration, explicit selection and unauthorized/offline gating");

        Field<Button>(adb, "clearShell").PerformClick();
        input.Text = "pwd\r\nls -la";
        Field<Button>(adb, "execute").PerformClick();
        Assert(adb.Busy && !filesHeader.Devices.Enabled && !header.Executable.Enabled && !Field<Button>(adb, "push").Enabled, "device and settings locked while task runs");
        await Until(() => outputBox.Text.Contains("模拟设备输出"), "ADB live output");
        shell.ShowView("adb-files");
        await Until(() => !adb.Busy, "Shell completes while hidden");
        shell.ShowView("adb-shell");
        Assert(outputBox.Text.Contains("Download") && Field<ComboBox>(adb, "history").Items.Count == 1, "command output and session history");
        string[] actualArgs = File.ReadAllLines(Path.Combine(root, "last-args.txt")).Select(value => Encoding.UTF8.GetString(Convert.FromBase64String(value))).ToArray();
        Assert(actualArgs.Last() == "pwd\nls -la", "multiline commands use device LF endings");
        Capture(form, "preview-adb-shell-demo.png");
        input.Text = "fixture-error"; await adb.Execute();
        Assert(notice.Text.Contains("退出码 7") && outputBox.Text.Contains("Permission denied"), "stderr and failed exit are not marked successful");
        input.Text = "fixture-hang"; Field<Button>(adb, "execute").PerformClick();
        await Until(() => outputBox.Text.Contains("READY"), "long running ADB command");
        int pid = Int32.Parse(File.ReadAllText(Path.Combine(root, "pid.txt")));
        shell.ShowView("adb-files");
        Field<Button>(adb, "stop").PerformClick();
        await Until(() => !adb.Busy, "stop hidden Shell from transfer page");
        Assert(ProcessExited(pid) && header.Devices.Enabled, "cancel releases client and unlocks selection");
        report.AppendLine("PASS ADB streaming Shell, command history, nonzero exit, navigation and cross-page cancellation");

        string source = Path.Combine(root, "local & 中文", "a & 中文.bin");
        string destination = Path.Combine(root, "UI pull result"); Directory.CreateDirectory(destination);
        Field<TextBox>(adb, "pushLocal").Text = source;
        Field<TextBox>(adb, "pushRemote").Text = "/sdcard/UI/";
        await adb.Transfer(true);
        Assert(Field<TextBox>(adb, "fileOutput").Text.Contains("pushed"), "UI push command");
        Field<TextBox>(adb, "pullRemote").Text = "/sdcard/UI/" + Path.GetFileName(source);
        Field<TextBox>(adb, "pullLocal").Text = destination;
        await adb.Transfer(false);
        Assert(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(Path.Combine(destination, Path.GetFileName(source)))), "UI push/pull byte identity");
        Capture(form, "preview-adb-files-demo.png");
        Field<TextBox>(adb, "pushRemote").Text = "/permission-denied";
        await adb.Transfer(true);
        Assert(Field<Label>(adb, "fileNotice").Text.Contains("Permission denied"), "transfer failure surfaced");
        Field<TextBox>(adb, "pushRemote").Text = "/slow-transfer";
        Field<Button>(adb, "push").PerformClick();
        await Until(() => Field<TextBox>(adb, "fileOutput").Text.Contains("模拟传输等待"), "transfer live progress");
        pid = Int32.Parse(File.ReadAllText(Path.Combine(root, "pid.txt")));
        Field<Button>(adb, "stop").PerformClick(); await Until(() => !adb.Busy, "cancel transfer");
        Assert(ProcessExited(pid) && Field<Button>(adb, "pull").Enabled, "transfer cancellation releases process and restores buttons");
        report.AppendLine("PASS ADB UI file push/pull, exact binary contents, transfer errors and cancellation");

        filesHeader.Address.Text = "127.0.0.1:5555";
        filesHeader.Connect.PerformClick(); await Until(() => !adb.Busy, "simulated network ADB connect");
        Assert(((AdbDevice)header.Devices.SelectedItem).Serial == "127.0.0.1:5555" && header.Disconnect.Enabled, "network connect selects requested device");
        filesHeader.Disconnect.PerformClick(); await Until(() => !adb.Busy, "simulated network ADB disconnect");
        Assert(header.Devices.SelectedItem == null && header.Devices.Items.Count == 3, "disconnect only targets the selected network device");
        filesHeader.Address.Text = "fail.invalid:5555";
        filesHeader.Connect.PerformClick(); await Until(() => !adb.Busy, "network connect reports failure despite zero ADB exit");
        Assert(Field<Label>(adb, "fileNotice").Text.Contains("未发现"), "failed connect output is not reported as a successful connection");
        await adb.RefreshDevices();
        File.WriteAllText(Path.Combine(root, "mode.txt"), "removed");
        await adb.RefreshDevices();
        Assert(header.Devices.SelectedItem == null && !Field<Button>(adb, "execute").Enabled, "disappeared device never silently switches to another ready device");
        File.WriteAllText(Path.Combine(root, "mode.txt"), "invalid");
        await adb.RefreshDevices();
        Assert(header.Devices.Items.Count == 0 && !Field<Button>(adb, "execute").Enabled, "failed refresh clears stale device selection");
        File.Delete(Path.Combine(root, "mode.txt")); await adb.RefreshDevices();
        report.AppendLine("PASS ADB network connect/disconnect, stale-selection rejection and failed-refresh cleanup (simulated devices)");
        await ExerciseAdbBrowser(form, adb, root);

        // Leave a client running on the hidden page to exercise the main form's asynchronous shutdown.
        shell.ShowView("adb-shell"); Field<Button>(adb, "clearShell").PerformClick();
        Assert(outputBox.TextLength == 0, "clear output leaves device selected");
        input.Text = "fixture-hang"; Field<Button>(adb, "execute").PerformClick();
        await Until(() => outputBox.Text.Contains("READY"), "ADB command before form shutdown");
    }

    static void SelectEntry(AdbBrowser dialog, string name)
    {
        var list = Field<ListView>(dialog, "entries");
        foreach (ListViewItem row in list.Items) row.Selected = false;
        var item = list.Items.Cast<ListViewItem>().Single(row => ((AdbEntry)row.Tag).Name == name);
        item.Selected = true; item.Focused = true; list.Select();
    }

    static async Task UseBrowser(AdbWorkspace adb, Button open, Func<AdbBrowser, Task> action)
    {
        Exception failure = null;
        var finished = new TaskCompletionSource<bool>();
        using (var timer = new Timer { Interval = 50 })
        {
            timer.Tick += async delegate {
                timer.Stop(); AdbBrowser dialog = null;
                try
                {
                    dialog = Field<AdbBrowser>(adb, "browser");
                    Assert(dialog != null, "browser opened from file-transfer page");
                    await Until(() => !dialog.Busy, "initial device directory load");
                    await action(dialog);
                }
                catch (Exception ex) { failure = ex; }
                finally { if (dialog != null && !dialog.IsDisposed) dialog.Close(); finished.TrySetResult(true); }
            };
            timer.Start(); open.PerformClick();
            await finished.Task;
            if (failure != null) throw failure;
            Assert(!adb.Busy, "modal browser releases shared ADB task state");
        }
    }

    static async Task ExerciseAdbBrowser(SpeedForm form, AdbWorkspace adb, string root)
    {
        var pushRemote = Field<TextBox>(adb, "pushRemote");
        var pullRemote = Field<TextBox>(adb, "pullRemote");
        var filesHeader = Field<AdbHeader>(adb, "filesHeader");
        filesHeader.Address.Text = "";
        Field<Button>(adb, "clearFiles").PerformClick();
        string transferLog = Field<TextBox>(adb, "fileOutput").Text;
        pushRemote.Text = "/";
        await UseBrowser(adb, Field<Button>(adb, "browsePush"), async dialog => {
            var list = Field<ListView>(dialog, "entries");
            Assert(!filesHeader.Devices.Enabled && !Field<Button>(adb, "pull").Enabled, "browser locks device and transfer actions");
            Assert(!Field<Button>(dialog, "up").Enabled, "root has no parent navigation");
            SelectEntry(dialog, "init.rc"); Assert(!Field<Button>(dialog, "choose").Enabled, "push browser cannot choose a file as destination directory");
            SelectEntry(dialog, "sdcard");
            typeof(ListView).GetMethod("OnItemActivate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(list, new object[] { EventArgs.Empty });
            await Until(() => !dialog.Busy && Field<TextBox>(dialog, "address").Text == "/sdcard", "double-click enters directory");
            await dialog.Navigate("/sdcard/Download");
            Assert(Field<TextBox>(dialog, "address").AutoCompleteCustomSource.Contains("/sdcard/Download/子目录/"), "directory names available for path completion");
            Field<CheckBox>(dialog, "hidden").Checked = true;
            Assert(list.Items.Cast<ListViewItem>().Any(row => row.Text == ".hidden.txt"), "hidden files visible on demand");
            Field<TextBox>(dialog, "filter").Text = "中文";
            Assert(list.Items.Count == 1 && list.Items[0].Text == "a & 中文.bin", "filter current directory by name");
            Field<TextBox>(dialog, "filter").Clear();
            SelectEntry(dialog, "broken-link"); Assert(!Field<Button>(dialog, "choose").Enabled, "broken links cannot be chosen");
            Field<Button>(dialog, "current").PerformClick();
        });
        Assert(pushRemote.Text == "/sdcard/Download/" && Field<TextBox>(adb, "fileOutput").Text == transferLog, "directory selection fills destination without transferring");

        pullRemote.Text = "/sdcard/Download/quote' $name.txt";
        await UseBrowser(adb, Field<Button>(adb, "browsePull"), dialog => {
            Assert(Field<TextBox>(dialog, "address").Text == "/sdcard/Download", "existing file starts browsing in its parent directory");
            Field<CheckBox>(dialog, "hidden").Checked = true;
            SelectEntry(dialog, "a & 中文.bin");
            Assert(Field<Button>(dialog, "choose").Enabled, "pull browser accepts regular files");
            Capture(dialog, "preview-adb-browser-demo.png");
            Field<Button>(dialog, "choose").PerformClick();
            return Task.FromResult(true);
        });
        Assert(pullRemote.Text == "/sdcard/Download/a & 中文.bin", "file selection fills the exact Unicode remote source");
        Capture(form, "preview-adb-files-browse.png");

        await UseBrowser(adb, Field<Button>(adb, "browsePull"), dialog => {
            SelectEntry(dialog, "子目录"); Field<Button>(dialog, "choose").PerformClick(); return Task.FromResult(true);
        });
        Assert(pullRemote.Text == "/sdcard/Download/子目录", "pull browser also accepts a whole folder");
        report.AppendLine("PASS ADB board browser navigation, file/folder distinction, hidden/filter/completion and path-only selection");

        // Verify that the chosen directory can actually be pulled recursively by the transfer action.
        string deviceFolder = Path.Combine(root, "device", "sdcard", "Download", "子目录");
        Directory.CreateDirectory(Path.Combine(deviceFolder, "nested")); File.WriteAllText(Path.Combine(deviceFolder, "nested", "inside.txt"), "folder payload");
        await adb.Transfer(false);
        Assert(File.ReadAllText(Path.Combine(Field<TextBox>(adb, "pullLocal").Text, "子目录", "nested", "inside.txt")) == "folder payload", "browser-selected folder pulls recursively");
        string localFolder = Path.Combine(root, "UI folder upload");
        Directory.CreateDirectory(Path.Combine(localFolder, "nested")); File.WriteAllText(Path.Combine(localFolder, "nested", "inside.txt"), "pushed folder payload");
        Field<TextBox>(adb, "pushLocal").Text = localFolder;
        await adb.Transfer(true);
        Assert(File.ReadAllText(Path.Combine(root, "device", "sdcard", "Download", "UI folder upload", "nested", "inside.txt")) == "pushed folder payload", "local folder pushes recursively into browser-selected destination");

        string unchanged = pullRemote.Text;
        await UseBrowser(adb, Field<Button>(adb, "browsePull"), async dialog => {
            await dialog.Navigate("/denied");
            Assert(Field<ListView>(dialog, "entries").Items.Count == 0 && !Field<Button>(dialog, "current").Enabled && Field<Label>(dialog, "status").Text.Contains("Permission denied"), "permission error clears stale choices and is not an empty folder");
            await dialog.Navigate("/empty");
            Assert(Field<ListView>(dialog, "entries").Items.Count == 0 && Field<Button>(dialog, "current").Enabled, "real empty directory can be selected");
            Field<TextBox>(dialog, "address").Text = "/not-loaded";
            Assert(!Field<Button>(dialog, "current").Enabled, "typing a new address invalidates old directory selection");
            var read = dialog.Navigate("/slow-browser");
            await Until(() => File.Exists(Path.Combine(root, "browser-pid.txt")), "slow directory client started");
            int pid = Int32.Parse(File.ReadAllText(Path.Combine(root, "browser-pid.txt")));
            dialog.Close(); await read;
            Assert(ProcessExited(pid), "closing browser cancels and releases its directory client");
        });
        Assert(pullRemote.Text == unchanged, "closing browser without choosing preserves the input path");
        report.AppendLine("PASS ADB browser-selected recursive folder push/pull, permission failure, empty folder, stale-path rejection and cancel cleanup");
    }

    static async Task ExerciseSockets(SpeedForm form, ToolShell shell)
    {
        var page = Field<SocketPage>(form, "socketPage");
        var session = Field<SocketSession>(page, "session");
        var connect = Field<Button>(page, "connect");
        var send = Field<Button>(page, "send");
        var automatic = Field<Button>(page, "automatic");
        var incoming = Field<TextBox>(page, "incoming");
        var outgoing = Field<TextBox>(page, "outgoing");
        var protocol = Field<ComboBox>(page, "protocol");
        Field<Button>(shell, "debug").PerformClick();
        Assert(shell.CurrentView == "lan" && Field<FlowLayoutPanel>(shell, "pages").Controls.Count == 3, "network-debug category contains sockets, LAN scan and capture, and remembers the LAN scan page");
        shell.ShowView("socket");
        Field<ComboBox>(page, "localAddress").Text = "127.0.0.1";
        Field<NumericUpDown>(page, "localPort").Value = 0;
        using (var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            Field<NumericUpDown>(page, "remotePort").Value = ((IPEndPoint)remote.Client.LocalEndPoint).Port;
            connect.PerformClick();
            await Until(() => session.IsOpen && send.Enabled, "UDP page open");
            Assert(!protocol.Enabled && !Field<ComboBox>(page, "localAddress").Enabled, "socket connection settings locked");
            outgoing.Text = "本机 UDP 回环测试：你好";
            var packet = remote.ReceiveAsync();
            send.PerformClick();
            await Until(() => packet.IsCompleted, "UDP UI sends real datagram");
            Assert(Encoding.UTF8.GetString(packet.Result.Buffer) == outgoing.Text, "UDP page text bytes");
            await remote.SendAsync(packet.Result.Buffer, packet.Result.Buffer.Length, session.LocalEndpoint);
            await Until(() => incoming.Text.Contains("RX") && incoming.Text.Contains("回环测试"), "UDP echo visible");
            Capture(form, "preview-debug-udp.png");
            shell.ShowView("speed");
            byte[] bytes = Encoding.UTF8.GetBytes("切页后继续接收");
            await remote.SendAsync(bytes, bytes.Length, session.LocalEndpoint);
            await Until(() => incoming.Text.Contains("切页后继续接收"), "hidden socket page still receives");
            Field<Button>(shell, "debug").PerformClick();
            Assert(session.IsOpen, "category switching preserves socket");

            var pause = Field<CheckBox>(page, "pause");
            pause.Checked = true;
            string shown = incoming.Text;
            bytes = Encoding.UTF8.GetBytes("暂停后恢复显示");
            long before = Field<long>(page, "received");
            await remote.SendAsync(bytes, bytes.Length, session.LocalEndpoint);
            await Until(() => Field<long>(page, "received") == before + bytes.Length, "paused display still counts receive");
            await Task.Delay(150);
            Assert(incoming.Text == shown, "pause only freezes display");
            pause.Checked = false;
            Assert(incoming.Text.Contains("暂停后恢复显示"), "resume shows retained records");
            Field<Button>(page, "clear").PerformClick();
            Assert(incoming.TextLength == 0 && Field<long>(page, "received") == 0 && Field<long>(page, "transmitted") == 0, "network clear resets logs and counts");

            Field<CheckBox>(page, "txHex").Checked = true;
            Field<CheckBox>(page, "rxHex").Checked = true;
            outgoing.Text = "00 FF 0D 0A";
            packet = remote.ReceiveAsync();
            send.PerformClick();
            await Until(() => packet.IsCompleted, "UDP HEX send");
            Assert(packet.Result.Buffer.SequenceEqual(new byte[] { 0, 255, 13, 10 }), "UDP UI binary payload");
            await remote.SendAsync(packet.Result.Buffer, packet.Result.Buffer.Length, session.LocalEndpoint);
            await Until(() => incoming.Text.Contains("RX") && incoming.Text.Contains("00 FF 0D 0A"), "UDP HEX receive");
            Field<NumericUpDown>(page, "interval").Value = 100;
            before = Field<long>(page, "transmitted");
            automatic.PerformClick();
            await Until(() => Field<long>(page, "transmitted") >= before + 8, "UDP periodic sends");
            automatic.PerformClick();
            await Until(() => !Field<bool>(page, "writing"), "UDP periodic stop");
            before = Field<long>(page, "transmitted");
            await Task.Delay(250);
            Assert(Field<long>(page, "transmitted") == before && !Field<Timer>(page, "sendTimer").Enabled, "UDP periodic stop prevents later sends");
            connect.PerformClick();
            Assert(!session.IsOpen && protocol.Enabled, "UDP page close unlocks settings");
        }
        report.AppendLine("PASS real UDP UI text/HEX, background receive, pause/resume, clear counters and periodic send/stop");

        protocol.SelectedIndex = 2;
        Field<NumericUpDown>(page, "localPort").Value = 0;
        Field<CheckBox>(page, "txHex").Checked = false;
        Field<CheckBox>(page, "rxHex").Checked = false;
        Field<Button>(page, "clear").PerformClick();
        connect.PerformClick();
        await Until(() => session.IsOpen, "TCP server page listens");
        using (var first = new SocketSession())
        using (var second = new SocketSession())
        {
            var a = new ConcurrentQueue<DataRecord>(); var b = new ConcurrentQueue<DataRecord>();
            first.Recorded += a.Enqueue; second.Recorded += b.Enqueue;
            var options = new SocketOptions { Mode = SocketMode.TcpClient, LocalAddress = IPAddress.Loopback, RemotePort = session.LocalEndpoint.Port };
            await first.Open(options, CancellationToken.None);
            await second.Open(options, CancellationToken.None);
            var choices = Field<ComboBox>(page, "peers");
            await Until(() => choices.Items.Count == 3 && send.Enabled, "TCP clients appear in list");
            choices.SelectedIndex = 1;
            outgoing.Text = "服务端定向发送";
            send.PerformClick();
            await Until(() => a.Any(r => r.Kind == "RX"), "TCP targeted UI send");
            Assert(!b.Any(r => r.Kind == "RX"), "TCP UI target excludes second client");
            choices.SelectedIndex = 0;
            outgoing.Text = "本机 TCP 服务端：发送给全部客户端";
            send.PerformClick();
            await Until(() => b.Any(r => r.Kind == "RX"), "TCP UI broadcast");
            await Until(() => incoming.Text.Contains("发送给全部客户端"), "TCP broadcast log visible");
            Capture(form, "preview-debug-server.png");
            choices.SelectedIndex = 1;
            Field<Button>(page, "disconnect").PerformClick();
            await Until(() => !first.IsOpen && session.Peers.Length == 1, "TCP UI disconnect selected client");
            Assert(session.IsOpen && second.IsOpen, "TCP UI keeps other client and listener");
            connect.PerformClick();
            await Until(() => !second.IsOpen, "TCP server page closes accepted sockets");
        }

        using (var server = new SocketSession())
        {
            var messages = new ConcurrentQueue<DataRecord>();
            server.Recorded += messages.Enqueue;
            await server.Open(new SocketOptions { Mode = SocketMode.TcpServer, LocalAddress = IPAddress.Loopback }, CancellationToken.None);
            protocol.SelectedIndex = 1;
            Field<NumericUpDown>(page, "remotePort").Value = server.LocalEndpoint.Port;
            connect.PerformClick();
            await Until(() => session.IsOpen && send.Enabled, "TCP client page connects");
            outgoing.Text = "TCP 客户端回环测试";
            send.PerformClick();
            await Until(() => messages.Any(r => r.Kind == "RX"), "TCP client UI sends");
            await Until(() => incoming.Text.Contains("TCP 客户端回环测试"), "TCP client log visible");
            Capture(form, "preview-debug-client.png");
            connect.PerformClick();
        }
        // Leave a loopback listener open to check form shutdown below.
        protocol.SelectedIndex = 2;
        Field<NumericUpDown>(page, "localPort").Value = 0;
        connect.PerformClick();
        await Until(() => session.IsOpen, "listener before form shutdown");
        report.AppendLine("PASS real TCP UI client/server, client list, targeted/broadcast send and selected disconnect");
    }

    static async Task ExerciseCapture(SpeedForm form, ToolShell shell)
    {
        await CaptureTests.Run(Path.Combine(output, "capture-core-" + Guid.NewGuid().ToString("N")), report);
        var page = Field<CapturePage>(form, "capturePage");
        var list = Field<ListView>(page, "packets");
        var start = Field<Button>(page, "start");
        var stop = Field<Button>(page, "stop");
        var pause = Field<CheckBox>(page, "pause");
        var adapters = Field<ComboBox>(page, "adapters");
        shell.ShowView("capture");
        Assert(!page.Busy && !start.Enabled && packetSource == null, "no automatic enumeration or capture");
        failCaptureCatalog = true; await page.RefreshAdapters();
        Assert(!start.Enabled && Field<Label>(page, "notice").Text.Contains("simulated missing Npcap"), "missing dependency is visible");
        failCaptureCatalog = false; await page.RefreshAdapters();
        Assert(start.Enabled && adapters.SelectedIndex == 0, "single adapter selection");
        start.PerformClick(); await Until(() => packetSource != null, "capture source opens");
        Assert(page.Busy && stop.Enabled && !adapters.Enabled && !start.Enabled, "capture locks settings");
        packetSource.Frames.Enqueue(CaptureTests.Tcp());
        await Until(() => list.VirtualListSize == 1, "live packet display");
        list.SelectedIndices.Add(0);
        Assert(Field<TextBox>(page, "detail").Text.Contains("192.0.2.1:12345"), "selected packet HEX details");
        Capture(form, "preview-capture-demo.png");
        pause.Checked = true;
        for (int i = 0; i < 2100; i++) packetSource.Frames.Enqueue(CaptureTests.Tcp());
        await Until(() => Field<CaptureSession>(page, "session").Count == 2101, "paused capture continues writing");
        Assert(list.VirtualListSize == 1, "pause freezes display only");
        shell.ShowView("speed"); await Task.Delay(150);
        Assert(page.Busy, "capture survives navigation");
        shell.ShowView("capture"); pause.Checked = false;
        await Until(() => list.VirtualListSize == 1000, "bounded recent-packet display resumes");
        stop.PerformClick(); await Until(() => !page.Busy, "stop capture");
        Assert(packetSource.Disposed && CaptureTests.ReadFile(Field<string>(page, "captureFile")) == 2101, "all packets saved despite display eviction");
        Assert(Field<Button>(page, "save").Enabled && adapters.Enabled, "stopped capture can be saved and settings unlocked");
        report.AppendLine("PASS capture UI: missing dependency, explicit start, live packets, HEX selection, pause/resume, hidden-page capture and bounded display with complete PCAP");
        packetSource = null; start.PerformClick(); await Until(() => packetSource != null, "restart capture");
        packetSource.Fail = true; await Until(() => !page.Busy, "read error stops capture");
        Assert(packetSource.Disposed && Field<Label>(page, "notice").Text.Contains("simulated capture failure"), "error shown and source closed");
        failCaptureCatalog = true; await page.RefreshAdapters();
        Assert(adapters.Items.Count == 0 && !start.Enabled, "failed refresh clears stale adapter");
        failCaptureCatalog = false; await page.RefreshAdapters();
        packetSource = null; start.PerformClick(); await Until(() => packetSource != null, "capture running for form-close test");
        report.AppendLine("PASS capture restart, error cleanup and stale adapter rejection");
    }

    static async Task ExerciseClearRecords(SerialWorkspace workspace, SerialSession session, Func<FakeSerialTransport> current)
    {
        var displayTimer = Field<Timer>(workspace, "displayTimer");
        var incoming = Field<TextBox>(workspace, "incoming");
        var outgoing = Field<TextBox>(workspace, "outgoing");
        var counters = Field<Label>(workspace, "counters");
        var clear = Field<Button>(workspace, "clear");
        var automatic = Field<Button>(workspace, "automatic");
        string text = outgoing.Text;
        Assert(Field<long>(workspace, "received") > 0 && Field<long>(workspace, "transmitted") > 0, "nonzero RX/TX before clear");
        displayTimer.Stop();
        await Task.Run(() =>
        {
            current().Receive(Encoding.UTF8.GetBytes("OLD-QUEUED"));
            current().Receive(new byte[1048577]);
        });
        Assert(Field<int>(workspace, "queuedBytes") > 0 && Field<long>(workspace, "dropped") > 0, "receive data and overflow warning pending before clear");
        clear.PerformClick();
        Assert(incoming.TextLength == 0 && Field<long>(workspace, "received") == 0 && Field<long>(workspace, "transmitted") == 0, "clear resets visible history and both byte counts");
        Assert(Field<int>(workspace, "queuedBytes") == 0 && Field<long>(workspace, "dropped") == 0, "clear discards pending data and overflow accounting");
        Assert(counters.Text.Contains("RX  0 字节") && counters.Text.Contains("TX  0 字节") && !Field<Button>(workspace, "save").Enabled, "zero counts and empty log save state update immediately");
        Assert(session.IsOpen && outgoing.Text == text, "clear preserves connection and send content");
        displayTimer.Start();
        await Task.Delay(150);
        Assert(incoming.TextLength == 0 && Field<long>(workspace, "received") == 0, "old queued records do not reappear");

        Field<Button>(workspace, "send").PerformClick();
        await Until(() => !Field<bool>(workspace, "writing") && incoming.Text.Contains("RX"), "new traffic after clear");
        Assert(Field<long>(workspace, "received") == 4 && Field<long>(workspace, "transmitted") == 4, "RX/TX restart from zero for new traffic");

        Field<NumericUpDown>(workspace, "interval").Value = 100;
        int before = current().Writes;
        automatic.PerformClick();
        await Until(() => current().Writes > before && !Field<bool>(workspace, "writing"), "periodic send before clear");
        clear.PerformClick();
        Assert(Field<Timer>(workspace, "sendTimer").Enabled && session.IsOpen, "clear leaves periodic sending active");
        await Until(() => Field<long>(workspace, "transmitted") >= 4, "periodic counting restarts after clear");
        automatic.PerformClick();
        await Until(() => !Field<bool>(workspace, "writing"), "periodic send stopped after reset test");
        report.AppendLine("PASS clear resets RX/TX, pending receive data and warnings; new traffic counts from zero without disconnecting or stopping periodic sending");
    }

    static async Task ExerciseMultiSend(Form form, SerialWorkspace workspace, SerialSession session, Func<FakeSerialTransport> current)
    {
        var tabs = Field<TabControl>(workspace, "sendTabs");
        var grid = Field<DataGridView>(workspace, "commands");
        var send = Field<Button>(workspace, "send");
        var automatic = Field<Button>(workspace, "automatic");
        var connect = Field<Button>(workspace, "connect");
        var interval = Field<NumericUpDown>(workspace, "interval");
        var timer = Field<Timer>(workspace, "sendTimer");
        tabs.SelectedIndex = 1;
        Field<Button>(workspace, "clear").PerformClick();
        typeof(SerialWorkspace).GetMethod("Info", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(workspace, new object[] { "DEMO", "界面演示：模拟串口回环，不代表真实设备数据。" });
        grid.Rows.Clear();
        grid.Rows.Add(true, 1, "AT", false);
        grid.Rows.Add(false, 2, "GG (unchecked invalid HEX)", true);
        grid.Rows.Add(true, 3, "AA BB 0D 0A", true);
        interval.Value = 100;
        int before = current().Writes;
        grid.CurrentCell = grid.Rows[1].Cells["Content"];
        send.PerformClick();
        Assert(!tabs.Enabled && send.Enabled && send.Text == "停止发送", "batch locks editor and exposes stop action");
        await Until(() => !Field<bool>(workspace, "writing"), "selected batch completed");
        var sent = current().Sent.ToArray().Skip(before).ToArray();
        Assert(sent.Length == 2 && Encoding.UTF8.GetString(sent[0]) == "AT\r\n" && sent[1].SequenceEqual(new byte[] { 0xAA, 0xBB, 13, 10 }), "only checked rows, visual order, per-row HEX and text suffix");
        grid.CurrentCell = grid.Rows[0].Cells["Content"];
        grid.Rows[1].Cells["Content"].Value = "AA 55";
        Capture(form, "preview-serial-multi-demo.png");
        grid.Rows[1].Cells["Content"].Value = "GG (unchecked invalid HEX)";

        grid.CurrentCell = grid.Rows[1].Cells["Selected"];
        grid.BeginEdit(false);
        ((DataGridViewCheckBoxCell)grid.CurrentCell).EditingCellFormattedValue = true;
        grid.NotifyCurrentCellDirty(true);
        Assert(Convert.ToBoolean(grid.CurrentCell.Value), "checkbox changes committed immediately");
        before = current().Writes;
        send.PerformClick();
        Assert(current().Writes == before && Field<Label>(workspace, "notice").Text.Contains("第 2 条"), "validate entire selected batch before sending any bytes");
        var tools = Field<FlowLayoutPanel>(workspace, "commandTools");
        ((Button)tools.Controls[3]).PerformClick();
        send.PerformClick();
        Assert(current().Writes == before && Field<Label>(workspace, "notice").Text.Contains("请先勾选"), "empty selection sends nothing");
        grid.Rows[0].Cells["Selected"].Value = true;
        grid.Rows[2].Cells["Selected"].Value = true;
        interval.Value = 300;
        send.PerformClick();
        await Until(() => current().Writes == before + 1, "first batch item");
        send.PerformClick();
        await Until(() => !Field<bool>(workspace, "writing"), "cancel between selected rows");
        await Task.Delay(350);
        Assert(current().Writes == before + 1, "stop skips unsent rows");
        report.AppendLine("PASS multi-send selection/order, mixed text/HEX, immediate checkbox commit, prevalidation and cancellation");

        interval.Value = 100;
        before = current().Writes;
        automatic.PerformClick();
        await Until(() => current().Writes >= before + 4, "two multi-send cycles");
        automatic.PerformClick();
        await Until(() => !Field<bool>(workspace, "writing"), "stop multi-send cycle");
        sent = current().Sent.ToArray().Skip(before).ToArray();
        Assert(sent.Length >= 4 && sent[0].SequenceEqual(sent[2]) && sent[1].SequenceEqual(sent[3]) && !sent[0].SequenceEqual(sent[1]), "periodic rows cycle in order without overlap");
        before = current().Writes;
        await Task.Delay(250);
        Assert(current().Writes == before && !timer.Enabled && current().PeakWrites == 1, "cycle stop and serialized writes");

        interval.Value = 300;
        send.PerformClick();
        await Until(() => current().Writes == before + 1, "batch before disconnect");
        connect.PerformClick();
        await Until(() => !session.IsOpen && !Field<bool>(workspace, "writing") && connect.Enabled, "disconnect cancels batch");
        await Task.Delay(350);
        Assert(current().Writes == before + 1 && current().Disposed, "disconnect prevents remaining rows");
        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "reconnect after batch cancellation");

        grid.Rows.Add(true, 4, "SHOULD_NOT_SEND", false);
        current().FailAtWrite = 2;
        interval.Value = 100;
        send.PerformClick();
        await Until(() => !session.IsOpen && !Field<bool>(workspace, "writing"), "batch write failure");
        Assert(current().Writes == 1 && current().Disposed, "failure stops remaining rows and closes port");
        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "reconnect after failed batch");
        tabs.SelectedIndex = 0;
        report.AppendLine("PASS multi-send cyclic timing, disconnect cancellation and mid-batch failure cleanup");
    }

    [STAThread]
    static int Main()
    {
        Directory.CreateDirectory(output);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        FakeSerialTransport fake = null;
        SerialSettings parameters = null;
        var session = new SerialSession(settings => { parameters = settings; return fake = new FakeSerialTransport(); });
        string fixtureRoot = Path.Combine(output, "adb-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        string fixture = Path.Combine(fixtureRoot, "adb.exe");
        File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb-fixture", "adb.exe"), fixture);
        var adb = new AdbWorkspace(fixture, false);
        var capture = new CapturePage(() => {
            if (failCaptureCatalog) throw new IOException("simulated missing Npcap");
            return new[] { new CaptureAdapter { Name = "fake", Description = "模拟网卡 · TEST DATA" } };
        }, options => packetSource = new FakePacketSource(), Path.Combine(output, "capture-ui-" + Guid.NewGuid().ToString("N")));
        bool passed = false;
        using (var form = new SpeedForm(session, adb, capture))
        {
            var sockets = Field<SocketSession>(Field<SocketPage>(form, "socketPage"), "session");
            form.Shown += async delegate
            {
                try { await Exercise(form, session, () => fake, () => parameters); passed = true; }
                catch (Exception ex) { report.AppendLine(ex.ToString()); }
                form.Close();
            };
            Application.Run(form);
            passed = passed && !sockets.IsOpen && !adb.Busy && !capture.Busy;
        }
        passed = passed && !session.IsOpen && fake != null && fake.Disposed;
        report.AppendLine(passed ? "PASS form close stops sending and releases serial transport" : "FAIL UI test or shutdown");
        string pidFile = Path.Combine(fixtureRoot, "pid.txt");
        bool adbClosed = File.Exists(pidFile) && ProcessExited(Int32.Parse(File.ReadAllText(pidFile)));
        passed = passed && adbClosed;
        report.AppendLine(adbClosed ? "PASS form close cancels and awaits the active ADB client" : "FAIL ADB process shutdown");
        bool captureClosed = packetSource != null && packetSource.Disposed && !capture.Busy;
        passed = passed && captureClosed;
        report.AppendLine(captureClosed ? "PASS form close awaits capture and releases the source" : "FAIL capture shutdown");
        report.AppendLine("NOTE all serial I/O used a simulated transport; no real COM port was opened.");
        report.AppendLine("NOTE ADB used an isolated fixture executable and simulated device files; no real device was accessed.");
        report.AppendLine("NOTE capture used synthetic packets; no real adapter was opened and Npcap is not required for these tests.");
        File.WriteAllText(Path.Combine(output, "ui-test.txt"), report.ToString(), Encoding.UTF8);
        return passed ? 0 : 1;
    }
}
