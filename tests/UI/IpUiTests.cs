using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class ToolboxUiTests
{
    static async Task ExerciseNetworkPages(MainForm form, ToolShell shell)
    {
        var ip = Field<IpPage>(form, "ipPage");
        var routeButton = Field<Button>(ip, "settings");
        var routeMenu = Field<ContextMenuStrip>(ip, "routeMenu");
        var proxy = Field<TextBox>(ip, "proxy");
        shell.ShowView("ip");
        Assert(IpRoute(ip).Mode == 1, "IP defaults to Windows system proxy");
        Field<ComboBox>(Field<SpeedPage>(form, "speedPage"), "mode").SelectedIndex = 2;
        Field<TextBox>(Field<SpeedPage>(form, "speedPage"), "proxy").Text = "http://127.0.0.1:19999";
        Assert(IpRoute(ip).Mode == 1 && IpRoute(ip).Proxy != "http://127.0.0.1:19999", "IP route independent from download settings");
        Field<ComboBox>(Field<SpeedPage>(form, "speedPage"), "mode").SelectedIndex = 0;
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

        await ExerciseSpeed(form, shell);
        typeof(MainForm).GetMethod("Busy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { true });
        Assert(!Field<Button>(Field<SpeedPage>(form, "speedPage"), "start").Enabled, "moved speed action busy state");
        Assert(!routeButton.Enabled && !proxy.Enabled, "IP route changes locked while network task runs");
        routeMenu.Items[0].PerformClick();
        Assert(IpRoute(ip).Mode == 1, "busy route cannot change through menu");
        shell.ShowView("lan");
        Assert(!Field<LanPage>(form, "lanPage").Actions[0].Enabled, "moved LAN action busy state");
        typeof(MainForm).GetMethod("Busy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { false });
        Assert(Field<LanPage>(form, "lanPage").Actions[0].Enabled, "LAN action restored");
        report.AppendLine("PASS network mutual exclusion survives navigation");

    }
}
