using System.Net;
using System.Windows.Forms;

internal static class Program
{
    internal const string Version = "3.4.0";

    [System.STAThread]
    static void Main()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.DefaultConnectionLimit = 32;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { using (var form = new MainForm()) Application.Run(form); }
        catch (System.Exception ex) { MessageBox.Show("无法打开工具箱：" + ex.Message, "工具箱", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
