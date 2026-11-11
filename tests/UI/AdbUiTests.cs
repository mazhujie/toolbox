using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

internal static partial class ToolboxUiTests
{
    static bool ProcessExited(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    static async Task ExerciseAdb(MainForm form, ToolShell shell)
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
        Assert(adb.Projects.Data.Runs[0].ExitCode == 7 && adb.Projects.Data.Runs[0].Status == "失败" && adb.Projects.Data.Runs[0].Target.Contains("DEMO-USB") && adb.Projects.ReadRun(adb.Projects.Data.Runs[0]).Contains("Permission denied"), "ADB failure and full output persist with target device");
        input.Text = "fixture-hang"; Field<Button>(adb, "execute").PerformClick();
        await Until(() => outputBox.Text.Contains("READY"), "long running ADB command");
        int pid = Int32.Parse(File.ReadAllText(Path.Combine(root, "pid.txt")));
        shell.ShowView("adb-files");
        Field<Button>(adb, "stop").PerformClick();
        await Until(() => !adb.Busy, "stop hidden Shell from transfer page");
        Assert(ProcessExited(pid) && header.Devices.Enabled, "cancel releases client and unlocks selection");
        Assert(adb.Projects.Data.Runs[0].Status == "已停止", "cancelled ADB run persisted");
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

    static async Task ExerciseAdbBrowser(MainForm form, AdbWorkspace adb, string root)
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

}
