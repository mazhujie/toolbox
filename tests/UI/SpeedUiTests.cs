using System;
using System.Collections.Generic;
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
            Field<NumericUpDown>(page, "seconds").Value = 3;
            Field<Button>(page, "start").PerformClick();
            await Until(() => Field<long>(page, "previousBytes") > 0, "speed page receives loopback download progress");
            Assert(!Field<Button>(Field<IpPage>(form, "ipPage"), "query").Enabled, "download locks IP lookup");
            shell.ShowView("lan");
            Assert(!Field<LanPage>(form, "lanPage").Actions[0].Enabled && Field<CancellationTokenSource>(page, "running") != null, "download continues while LAN scan is locked on another page");
            shell.ShowView("speed");
            Field<Button>(page, "stop").PerformClick();
            await Until(() => Field<CancellationTokenSource>(page, "running") == null, "speed page cancellation completes");
            Assert(Field<List<string[]>>(page, "history").Count == 1 && Field<Button>(page, "start").Enabled, "cancelled download retains metrics and unlocks page");
            Assert(Field<Button>(Field<IpPage>(form, "ipPage"), "query").Enabled && Field<LanPage>(form, "lanPage").Actions[0].Enabled, "download completion unlocks other network pages");
        }
        url.Text = previous;
        report.AppendLine("PASS independent speed page: real loopback download, progress, cross-page mutual exclusion, cancellation and metrics");
    }
}
