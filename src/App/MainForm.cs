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
    readonly ProjectStore projectStore;
    readonly ProjectWorkspace projectWorkspace;
    readonly Icon appIcon;
    public MainForm() : this(null) { }
    internal MainForm(SerialSession serialConnection, AdbWorkspace adbTools = null, CapturePage captureTools = null, ProjectStore projects = null)
    {
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Toolbox.AppIcon")) appIcon = new Icon(stream);
        Icon = appIcon;
        projectStore = projects ?? new ProjectStore();
        Text = "工具箱 " + Program.Version + " · 网络服务 / 串口 / 网络调试 / ADB / 项目助手"; ClientSize = new Size(1034, 760);
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
        adbWorkspace.Projects = projectStore;
        projectWorkspace = new ProjectWorkspace(projectStore, adbWorkspace, () => shell.ShowView("adb-shell"));
        shell.Add("speed", "network", "下载测速", speedPage, speedPage.Actions);
        shell.Add("ip", "network", "出口 IP / 风险", ipPage, ipPage.Actions);
        shell.Add("serial", "serial", "串口收发", serialWorkspace.Terminal, serialWorkspace.TerminalActions);
        shell.Add("socket", "debug", "TCP / UDP 收发", socketPage, socketPage.Actions);
        shell.Add("lan", "debug", "局域网扫描", lanPage, lanPage.Actions);
        shell.Add("capture", "debug", "实时抓包", capturePage, capturePage.Actions);
        shell.Add("adb-shell", "adb", "Shell 命令", adbWorkspace.ShellPage, adbWorkspace.ShellActions);
        shell.Add("adb-files", "adb", "文件传输", adbWorkspace.FilesPage, adbWorkspace.FileActions);
        shell.Add("projects", "projects", "项目助手", projectWorkspace);
        Controls.Add(shell);
        var screen = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(Math.Min(screen.Width, Math.Max(MinimumSize.Width, projectStore.Data.Width)), Math.Min(screen.Height, Math.Max(MinimumSize.Height, projectStore.Data.Height)));
        shell.ShowView(projectStore.Data.View);
        if (shell.CurrentView == null) shell.ShowView("projects");
        bool closing = false;
        FormClosing += async delegate(object sender, FormClosingEventArgs e) {
            if (closing) return; e.Cancel = true;
            projectStore.Data.View = shell.CurrentView;
            Size normal = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            projectStore.Data.Width = normal.Width; projectStore.Data.Height = normal.Height;
            if (!projectWorkspace.SaveNow())
            { MessageBox.Show(this, "项目内容未能保存，请检查窗口底部的错误提示后重试关闭。", "保存失败"); return; }
            closing = true;
            speedPage.Cancel(); ipPage.Cancel(); lanPage.Cancel();
            socketPage.Shutdown();
            await Task.WhenAll(serialWorkspace.Shutdown(), adbWorkspace.Shutdown(), capturePage.Shutdown(), projectWorkspace.Shutdown()); serialWorkspace.Dispose();
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
            if (projectWorkspace != null) projectWorkspace.Dispose();
            if (projectStore != null) projectStore.Dispose();
        }
        base.Dispose(disposing);
        if (disposing && appIcon != null) appIcon.Dispose();
    }
    void Busy(bool busy)
    {
        speedPage.SetExternalBusy(busy);
        ipPage.SetSpeedBusy(busy);
        lanPage.SetExternalBusy(busy);
    }
}
