using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class MainForm : Form
{
    readonly SpeedPage speedPage;
    readonly IpPage ipPage;
    readonly LanPage lanPage;
    readonly ToolShell shell;
    readonly SerialWorkspace serialWorkspace;
    readonly SocketPage socketPage;
    readonly AdbWorkspace adbWorkspace;
    readonly CapturePage capturePage;
    public MainForm() : this(null) { }
    internal MainForm(SerialSession serialConnection, AdbWorkspace adbTools = null, CapturePage captureTools = null)
    {
        Text = "工具箱 " + Program.Version + " · 网络服务 / 串口助手 / 网络调试 / ADB"; ClientSize = new Size(1034, 760);
        MinimumSize = new Size(1050, 759); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.FromArgb(245, 247, 251);
        shell = new ToolShell();
        speedPage = new SpeedPage(Busy);
        ipPage = new IpPage(Busy);
        lanPage = new LanPage(Busy);
        serialWorkspace = new SerialWorkspace(serialConnection);
        socketPage = new SocketPage();
        adbWorkspace = adbTools ?? new AdbWorkspace();
        capturePage = captureTools ?? new CapturePage();
        shell.Add("speed", "network", "下载测速", speedPage, speedPage.Actions);
        shell.Add("ip", "network", "出口 IP / 风险", ipPage, ipPage.Actions);
        shell.Add("serial", "serial", "串口收发", serialWorkspace.Terminal, serialWorkspace.TerminalActions);
        shell.Add("socket", "debug", "TCP / UDP 收发", socketPage, socketPage.Actions);
        shell.Add("lan", "debug", "局域网扫描", lanPage, lanPage.Actions);
        shell.Add("capture", "debug", "实时抓包", capturePage, capturePage.Actions);
        shell.Add("adb-shell", "adb", "Shell 命令", adbWorkspace.ShellPage, adbWorkspace.ShellActions);
        shell.Add("adb-files", "adb", "文件传输", adbWorkspace.FilesPage, adbWorkspace.FileActions);
        Controls.Add(shell); shell.ShowView("speed");
        bool closing = false;
        FormClosing += async delegate(object sender, FormClosingEventArgs e) {
            if (closing) return; closing = true; e.Cancel = true;
            speedPage.Cancel(); ipPage.Cancel(); lanPage.Cancel();
            socketPage.Shutdown();
            await Task.WhenAll(serialWorkspace.Shutdown(), adbWorkspace.Shutdown(), capturePage.Shutdown()); serialWorkspace.Dispose();
            if (!IsDisposed) BeginInvoke(new Action(Close));
        };
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (speedPage != null) speedPage.Cancel();
            if (serialWorkspace != null) serialWorkspace.Dispose();
            if (socketPage != null) socketPage.Shutdown();
            if (adbWorkspace != null) adbWorkspace.Dispose();
            if (capturePage != null) capturePage.Shutdown();
        }
        base.Dispose(disposing);
    }
    void Busy(bool busy)
    {
        speedPage.SetExternalBusy(busy);
        ipPage.SetSpeedBusy(busy);
        lanPage.SetExternalBusy(busy);
    }
}
