using System.Net;
using System.Windows.Forms;

internal static class Program
{
    internal const string Version = "3.3.4";

    [System.STAThread]
    static void Main()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.DefaultConnectionLimit = 32;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new MainForm()) Application.Run(form);
    }
}
