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
    readonly Icon appIcon;
    public MainForm() : this(null) { }
    internal MainForm(SerialSession serialConnection, AdbWorkspace adbTools = null, CapturePage captureTools = null, string settingsPath = null)
    {
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Toolbox.AppIcon")) appIcon = new Icon(stream);
        Icon = appIcon;
        Text = "工具箱 " + Program.Version + " · 网络服务 / 串口 / 网络调试 / ADB"; ClientSize = new Size(1034, 760);
        MinimumSize = new Size(1050, 759); StartPosition = FormStartPosition.CenterScreen;
        Font = UiTheme.Body; BackColor = UiTheme.Background;
        shell = new ToolShell();
        speedPage = new SpeedPage(busy => SetNetworkBusy("speed", busy));
        ipPage = new IpPage(busy => SetNetworkBusy("ip", busy));
        lanPage = new LanPage(busy => SetNetworkBusy("lan", busy));
        serialWorkspace = new SerialWorkspace(serialConnection);
        socketPage = new SocketPage();
        adbWorkspace = adbTools ?? new AdbWorkspace();
        capturePage = captureTools ?? new CapturePage();
        shell.Add("speed", "network", "下载测速", speedPage);
        shell.Add("ip", "network", "出口 IP / 风险", ipPage);
        shell.Add("serial", "serial", "串口收发", serialWorkspace.Terminal);
        shell.Add("socket", "debug", "TCP / UDP 收发", socketPage);
        shell.Add("lan", "debug", "局域网扫描", lanPage);
        shell.Add("capture", "debug", "实时抓包", capturePage);
        shell.Add("adb-shell", "adb", "Shell 命令", adbWorkspace.ShellPage);
        shell.Add("adb-files", "adb", "文件传输", adbWorkspace.FilesPage);
        Controls.Add(shell);
        settingsPath = settingsPath ?? UiSettings.DefaultPath;
        var settings = UiSettings.Load(settingsPath);
        var screen = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(Math.Min(screen.Width, Math.Max(MinimumSize.Width, settings.Width)), Math.Min(screen.Height, Math.Max(MinimumSize.Height, settings.Height)));
        shell.ShowView(settings.View ?? "speed");
        if (shell.CurrentView == null) shell.ShowView("speed");
        bool closing = false, readyToClose = false;
        FormClosing += async delegate(object sender, FormClosingEventArgs e) {
            if (readyToClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            settings.View = shell.CurrentView;
            Size normal = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            settings.Width = normal.Width; settings.Height = normal.Height;
            settings.Save(settingsPath);
            speedPage.Cancel(); ipPage.Cancel(); lanPage.Cancel();
            socketPage.Shutdown();
            await Task.WhenAll(serialWorkspace.Shutdown(), adbWorkspace.Shutdown(), capturePage.Shutdown()); serialWorkspace.Dispose();
            readyToClose = true;
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
        if (disposing && appIcon != null) appIcon.Dispose();
    }
    string networkBusyOwner;
    void SetNetworkBusy(string owner, bool busy)
    {
        if (busy) networkBusyOwner = owner;
        else if (networkBusyOwner == owner) networkBusyOwner = null;

        speedPage.SetExternalBusy(networkBusyOwner != null && networkBusyOwner != "speed");
        ipPage.SetSpeedBusy(networkBusyOwner != null && networkBusyOwner != "ip");
        lanPage.SetExternalBusy(networkBusyOwner != null && networkBusyOwner != "lan");
        shell.ShowNetworkBusy(networkBusyOwner);
    }
}
