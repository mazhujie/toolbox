using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class ToolboxUiTests
{
    static async Task ExerciseProjects(MainForm form, ToolShell shell)
    {
        shell.ShowView("projects");
        var workspace = Field<ProjectWorkspace>(form, "projectWorkspace"); var store = workspace.Store;
        var settings = new WorkProject { Directory = @"\\wsl.localhost\Ubuntu-Test\home\developer\demo" };
        using (var dialog = new ProjectDialog(settings, token => Task.FromResult(new[] { "Ubuntu-Test", "Debian-Test" })))
        {
            dialog.Show(form);
            await Until(() => Field<ComboBox>(dialog, "distributions").Items.Count == 2, "read WSL configuration");
            Assert(Convert.ToString(Field<ComboBox>(dialog, "distributions").SelectedItem) == "Ubuntu-Test" && Field<TextBox>(dialog, "wslDirectory").Text == "/home/developer/demo", "project settings infer WSL distro and Linux directory");
            Field<ComboBox>(dialog, "terminalMode").SelectedItem = "WSL"; Capture(dialog, "preview-project-wsl-settings.png");
            ((Button)dialog.AcceptButton).PerformClick(); dialog.Close();
        }
        Assert(settings.TerminalMode == "WSL" && settings.Mode == "WSL" && settings.WslDistribution == "Ubuntu-Test", "WSL project settings save");
        var tabs = Field<WorkspaceTabs>(workspace, "tabs"); var history = Field<WorkspaceTabs>(workspace, "historyTabs");
        var next = Field<TextBox>(workspace, "next"); next.Text = "明天继续检查电源时序";
        var first = store.Current; first.Name = "RK3576 调试板"; first.Directory = store.Root;
        workspace.NewJournal(); Field<TextBox>(workspace, "journalTitle").Text = "HDMI 黑屏 · 排查记录";
        Field<JournalEditor>(workspace, "journalText").Text = "问题现象：启动后无显示\r\n\r\n排查过程：检查连接，读取内核日志\r\n\r\n处理结果 / 下一步：确认供电时序";
        using (var image = new Bitmap(360, 160))
        {
            using (var drawing = Graphics.FromImage(image)) { drawing.Clear(Color.FromArgb(24, 35, 55)); using (var font = new Font("Microsoft YaHei UI", 18)) drawing.DrawString("截图保存测试 · 调试波形", font, Brushes.White, 12, 45); }
            workspace.AddImage(image);
            // Preserve an existing text clipboard. Rich app-owned formats are left untouched.
            var previous = Clipboard.GetDataObject();
            bool canRestore = previous == null || !previous.GetFormats(false).Except(new[] { "UnicodeText", "Text", "OEMText", "Locale" }).Any();
            if (canRestore)
            {
                string previousText = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                try
                {
                    Clipboard.SetImage(image);
                    Field<JournalEditor>(workspace, "journalText").Paste();
                    Assert(store.Data.Journals.Single().Images.Count == 2, "native WM_PASTE saves clipboard image");
                    Field<Button>(workspace, "paste").PerformClick();
                    Assert(store.Data.Journals.Single().Images.Count == 3, "paste screenshot button saves clipboard image");
                    report.AppendLine("PASS native Windows clipboard image paste via editor and button; prior text clipboard restored");
                }
                finally { if (previousText == null) Clipboard.Clear(); else Clipboard.SetText(previousText); }
            }
            else report.AppendLine("NOTE native clipboard test skipped to preserve non-text clipboard; PNG attachment path exercised directly.");
        }
        Assert(workspace.SaveNow() && store.Data.Journals.Single().Images.Count >= 1, "journal text and screenshot save");
        var original = store.Data.Journals.Single(); workspace.NewJournal(); Field<TextBox>(workspace, "journalTitle").Text = "第二篇日志"; workspace.SaveNow();
        Field<ListBox>(workspace, "journals").SelectedItem = original;
        Assert(Field<TextBox>(workspace, "journalTitle").Text == "HDMI 黑屏 · 排查记录", "select a different journal without losing text");
        Field<TextBox>(workspace, "search").Text = "内核";
        Assert(Field<ListBox>(workspace, "journals").Items.Count == 1, "journal search includes body text"); Field<TextBox>(workspace, "search").Clear();
        var selected = Field<System.Collections.Generic.Dictionary<string, Button>>(tabs, "buttons");
        Assert(selected["日志"].Height > selected["现场"].Height && selected["日志"].Font.Size > selected["现场"].Font.Size, "selected main tab larger");
        Capture(form, "preview-project-journal.png");
        var second = new WorkProject { Name = "另一台板卡", Directory = store.Root }; store.Data.Projects.Add(second);
        var selector = Field<ComboBox>(workspace, "projects"); selector.Items.Add(second); selector.SelectedItem = second;
        Assert(next.Text == "" && Field<TextBox>(workspace, "journalTitle").Text == "", "project switch isolates notes and journal");
        next.Text = "第二个项目的下一步"; selector.SelectedItem = first;
        Assert(next.Text == "明天继续检查电源时序" && Field<TextBox>(workspace, "journalTitle").Text == "HDMI 黑屏 · 排查记录" && store.Data.Journals.Any(j => j.Title == "HDMI 黑屏 · 排查记录" && j.Images.Count >= 1), "switch saves and restores selected journal");
        tabs.Select("现场"); Capture(form, "preview-project-scene.png");
        tabs.Select("命令"); Field<ComboBox>(workspace, "mode").SelectedItem = "Windows PowerShell";
        Field<TextBox>(workspace, "command").Text = "[Console]::WriteLine('项目调试成功')";
        await workspace.ExecuteCommand();
        Assert(store.Data.Runs[0].ProjectId == first.Id && store.Data.Runs[0].Status == "成功" && store.ReadRun(store.Data.Runs[0]).Contains("项目调试成功"), "local UI command records output and project");
        Field<TextBox>(workspace, "command").Text = "[Console]::WriteLine('READY'); Start-Sleep -Seconds 60";
        var pending = workspace.ExecuteCommand(); await Until(() => workspace.LocalBusy, "local task started");
        selector.SelectedItem = second; Field<Button>(workspace, "stop").PerformClick(); await pending;
        Assert(store.Data.Runs[0].ProjectId == first.Id && store.Data.Runs[0].Status == "已停止", "switch while running does not reassign the record");
        string wsl = Environment.GetEnvironmentVariable("TOOLBOX_TEST_WSL");
        if (!String.IsNullOrWhiteSpace(wsl))
        {
            selector.SelectedItem = first; first.WslDistribution = wsl; first.WslDirectory = "/tmp";
            Field<ComboBox>(workspace, "mode").SelectedItem = "WSL";
            Field<TextBox>(workspace, "command").Text = "pwd\r\nprintf 'WSL 中文调试成功\\n'";
            await workspace.ExecuteCommand();
            Assert(store.Data.Runs[0].Status == "成功" && store.Data.Runs[0].Target == "WSL · " + wsl && store.ReadRun(store.Data.Runs[0]).Contains("WSL 中文调试成功"), "real WSL UI output and history");
            await Until(() => Field<TextBox>(workspace, "output").Text.Contains("[成功]"), "WSL output flushed");
            Assert(Field<TextBox>(workspace, "output").Text.Contains("/tmp\r\nWSL 中文调试成功"), "Linux newlines render as separate Windows lines");
            Capture(form, "preview-project-wsl-command.png");
            Field<TextBox>(workspace, "command").Text = "echo READY; sleep 60";
            pending = workspace.ExecuteCommand(); await Until(() => Field<AdbText>(workspace, "localOutput").Read(false).Contains("READY"), "WSL live output");
            selector.SelectedItem = second; Field<Button>(workspace, "stop").PerformClick(); await pending;
            Assert(store.Data.Runs[0].Status == "已停止" && store.Data.Runs[0].ProjectId == first.Id, "WSL cancellation preserves original project");
            report.AppendLine("PASS real WSL UI execution, Chinese output, persistent device/environment history and cancellation across project switches");
        }
        selector.SelectedItem = first; tabs.Select("历史");
        var nested = Field<System.Collections.Generic.Dictionary<string, Button>>(history, "buttons");
        history.Select("收工断点"); Assert(nested["收工断点"].Height > nested["调试记录"].Height, "nested history tab larger when selected");
        history.Select("调试记录"); Capture(form, "preview-project-history.png");
        form.Size = form.MinimumSize; tabs.Select("日志"); Capture(form, "preview-project-compact.png");
        Assert(Field<JournalEditor>(workspace, "journalText").Height > 150 && Field<Button>(workspace, "paste").Visible, "journal editor and paste action fit at minimum size");
        form.Size = new Size(1100, 820);
        report.AppendLine("PASS project UI: selected-large main/history tabs, screenshot attachments, journal save, project isolation, local execution and cancellation during project switch");
    }
}
