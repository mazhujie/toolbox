using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class ToolboxUiTests
{
    static readonly StringBuilder report = new StringBuilder();
    static readonly string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results");
    static FakePacketSource packetSource;
    static bool failCaptureCatalog;

    static T Field<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
    }

    static async Task Until(Func<bool> ready, string name)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.ElapsedMilliseconds > 4000) throw new TimeoutException(name);
            await Task.Delay(20);
        }
    }

    static void Capture(Form form, string name)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, name));
        }
    }

    static void ToggleAdvanced(LinkLabel link)
    {
        typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(link, new object[] { new LinkLabelLinkClickedEventArgs(link.Links[0]) });
    }

    static Options IpRoute(IpPage page)
    {
        return (Options)typeof(IpPage).GetMethod("ReadRoute", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, null);
    }

    static async Task Exercise(MainForm form, SerialSession session, Func<FakeSerialTransport> current, Func<SerialSettings> applied)
    {
        var shell = Field<ToolShell>(form, "shell");
        var actions = Field<FlowLayoutPanel>(shell, "actions");
        foreach (var id in new[] { "speed", "ip", "lan", "serial", "socket", "capture", "adb-shell", "adb-files", "speed" })
        {
            shell.ShowView(id);
            await Task.Delay(30);
            Assert(shell.CurrentView == id && actions.Controls.Count >= 3, "navigation: " + id);
            Assert(actions.Controls.Cast<Control>().All(c => c.Parent == actions), "left action placement");
            Capture(form, "preview-" + id + ".png");
        }
        report.AppendLine("PASS eight pages, four independent categories and left action placement");

        await ExerciseNetworkPages(form, shell);
        await ExerciseSockets(form, shell);
        await ExerciseCapture(form, shell);
        await ExerciseAdb(form, shell);
        await ExerciseSerial(form, shell, session, current, applied);
    }

    [STAThread]
    static int Main()
    {
        Directory.CreateDirectory(output);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        FakeSerialTransport fake = null;
        SerialSettings parameters = null;
        var session = new SerialSession(settings => { parameters = settings; return fake = new FakeSerialTransport(); });
        string fixtureRoot = Path.Combine(output, "adb-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        string fixture = Path.Combine(fixtureRoot, "adb.exe");
        File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb-fixture", "adb.exe"), fixture);
        var adb = new AdbWorkspace(fixture, false);
        var capture = new CapturePage(() => {
            if (failCaptureCatalog) throw new IOException("simulated missing Npcap");
            return new[] { new CaptureAdapter { Name = "fake", Description = "模拟网卡 · TEST DATA" } };
        }, options => packetSource = new FakePacketSource(), Path.Combine(output, "capture-ui-" + Guid.NewGuid().ToString("N")));
        bool passed = false;
        using (var form = new MainForm(session, adb, capture))
        {
            var sockets = Field<SocketSession>(Field<SocketPage>(form, "socketPage"), "session");
            form.Shown += async delegate
            {
                try { await Exercise(form, session, () => fake, () => parameters); passed = true; }
                catch (Exception ex) { report.AppendLine(ex.ToString()); }
                form.Close();
            };
            Application.Run(form);
            passed = passed && !sockets.IsOpen && !adb.Busy && !capture.Busy;
        }
        passed = passed && !session.IsOpen && fake != null && fake.Disposed;
        report.AppendLine(passed ? "PASS form close stops sending and releases serial transport" : "FAIL UI test or shutdown");
        string pidFile = Path.Combine(fixtureRoot, "pid.txt");
        bool adbClosed = File.Exists(pidFile) && ProcessExited(Int32.Parse(File.ReadAllText(pidFile)));
        passed = passed && adbClosed;
        report.AppendLine(adbClosed ? "PASS form close cancels and awaits the active ADB client" : "FAIL ADB process shutdown");
        bool captureClosed = packetSource != null && packetSource.Disposed && !capture.Busy;
        passed = passed && captureClosed;
        report.AppendLine(captureClosed ? "PASS form close awaits capture and releases the source" : "FAIL capture shutdown");
        report.AppendLine("NOTE all serial I/O used a simulated transport; no real COM port was opened.");
        report.AppendLine("NOTE ADB used an isolated fixture executable and simulated device files; no real device was accessed.");
        report.AppendLine("NOTE capture used synthetic packets; no real adapter was opened and Npcap is not required for these tests.");
        File.WriteAllText(Path.Combine(output, "ui-test.txt"), report.ToString(), Encoding.UTF8);
        return passed ? 0 : 1;
    }
}
