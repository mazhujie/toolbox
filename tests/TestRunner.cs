using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class TestRunner
{
    [STAThread]
    static int Main(string[] args)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.DefaultConnectionLimit = 32;
        string command = args.Length == 0 ? "--self-test" : args[0];
        string filename;
        switch (command)
        {
            case "--self-test": filename = "self-test.txt"; break;
            case "--check-universities": filename = "university-test.txt"; break;
            case "--check-ip": filename = "ip-test.txt"; break;
            case "--check-lan": filename = "lan-test.txt"; break;
            case "--smoke-test": return SmokeTest();
            default: Console.Error.WriteLine("Unknown test command: " + command); return 1;
        }
        string report;
        try
        {
            report = (command == "--check-universities" ? SpeedTests.CheckUniversities()
                : command == "--check-ip" ? IpTests.Live()
                : command == "--check-lan" ? LanTests.Live() : Run()).GetAwaiter().GetResult();
        }
        catch (Exception ex) { report = "FAIL " + ex; }
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename), report, Encoding.UTF8);
        Console.Write(report);
        return report.Contains("FAIL ") ? 1 : 0;
    }

    static async Task<string> Run()
    {
        var report = new StringBuilder(await SpeedTests.Run());
        report.Append(await IpTests.Run());
        report.Append(await LanTests.Run());
        report.Append(await SerialTests.Run());
        report.Append(await SocketTests.Run());
        return report.ToString();
    }

    static int SmokeTest()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new MainForm())
        using (var timer = new Timer { Interval = 800 })
        {
            timer.Tick += delegate {
                timer.Stop();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png"));
                }
                form.Close();
            };
            timer.Start();
            Application.Run(form);
        }
        return 0;
    }
}
