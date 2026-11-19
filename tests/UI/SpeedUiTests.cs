using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class ToolboxUiTests
{
    static async Task ExerciseSpeed(MainForm form, ToolShell shell)
    {
        var page = Field<SpeedPage>(form, "speedPage");
        var url = Field<TextBox>(page, "url");
        string previous = url.Text;
        using (var server = new TestServer())
        {
            shell.ShowView("speed");
            url.Text = server.Url;
            Assert(Field<ComboBox>(page, "source").SelectedIndex == 6, "editing URL selects custom source");
            Field<NumericUpDown>(page, "seconds").Value = 3;
            Field<Button>(page, "start").PerformClick();
            await Until(() => Field<long>(page, "previousBytes") > 0, "speed page receives loopback download progress");
            Assert(!Field<Button>(Field<IpPage>(form, "ipPage"), "query").Enabled, "download locks IP lookup");
            shell.ShowView("lan");
            Assert(!Field<Button>(Field<LanPage>(form, "lanPage"), "start").Enabled && Field<CancellationTokenSource>(page, "running") != null, "download continues while LAN scan is locked on another page");
            var banner = Field<TableLayoutPanel>(shell, "mutexBanner");
            Assert(banner.Visible, "LAN explains why the start action is disabled");
            banner.Controls.OfType<Button>().Single().PerformClick();
            Assert(shell.CurrentView == "speed" && !banner.Visible, "banner navigates to the running task");
            Field<Button>(page, "stop").PerformClick();
            await Until(() => Field<CancellationTokenSource>(page, "running") == null, "speed page cancellation completes");
            Assert(Field<List<string[]>>(page, "history").Count == 1 && Field<Button>(page, "start").Enabled, "cancelled download retains metrics and unlocks page");
            Assert(Field<Button>(Field<IpPage>(form, "ipPage"), "query").Enabled && Field<Button>(Field<LanPage>(form, "lanPage"), "start").Enabled, "download completion unlocks other network pages");
        }
        foreach (string path in new[] { "/rate-limit", "/rate-limit-after-data", "/error" })
        using (var server = new TestServer())
        {
            url.Text = server.Url.Replace("/data", path);
            Field<NumericUpDown>(page, "connections").Value = 1;
            Field<Button>(page, "start").PerformClick();
            await Until(() => Field<CancellationTokenSource>(page, "running") == null, "speed error is shown and controls unlock");
            bool limited = path != "/error";
            Assert(Field<Label>(page, "speed").Text == (limited ? "测速源限流" : "测速失败"), "429 is distinct from other HTTP failures");
            string message = Field<Label>(page, "status").Text;
            Assert(limited ? message.Contains("429") && message.Contains("降低并发") && !message.Contains("检查地址或代理") : !message.Contains("限流"), "status explains the actual failure");
            Assert(Field<List<string[]>>(page, "history").Count == 1 && Field<Button>(page, "start").Enabled, "failed tests are not recorded as successful and can be restarted");
            if (limited)
            {
                Assert(Field<TextBox>(page, "log").Text.Contains("测速源限流（HTTP 429）") && Field<Label>(page, "detail").Text.Contains("已中断"), "log and detail identify interruption");
                Capture(form, "preview-speed-rate-limit.png");
            }
        }
        url.Text = previous;
        report.AppendLine("PASS independent speed page: real loopback download, progress, cross-page mutual exclusion, cancellation and metrics");
        report.AppendLine("PASS HTTP 429 UI guidance during latency and download, interrupted-result label and ordinary HTTP failure distinction");
    }
}
