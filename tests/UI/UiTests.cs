using System;
using System.Collections.Generic;
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

    static void PressSendShortcut(TextBox editor)
    {
        var key = new KeyEventArgs(Keys.Control | Keys.Enter);
        typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(editor, new object[] { key });
        Assert(key.SuppressKeyPress, "Ctrl+Enter handled without inserting a newline");
    }

    static Options IpRoute(IpPage page)
    {
        return (Options)typeof(IpPage).GetMethod("ReadRoute", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, null);
    }

    static IEnumerable<Button> PageButtons(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (!child.Visible) continue;
            var button = child as Button;
            if (button != null) yield return button;
            foreach (var nested in PageButtons(child)) yield return nested;
        }
    }

    static async Task VerifyLayouts(MainForm form, ToolShell shell)
    {
        var subPages = Field<FlowLayoutPanel>(shell, "pages");
        var content = Field<Panel>(shell, "content");
        var side = Field<Panel>(shell, "side");
        var ids = new[] { "speed", "ip", "lan", "serial", "socket", "capture", "adb-shell", "adb-files" };
        Size original = form.Size;
        Assert(side.Controls.OfType<Button>().Count() == 4 && subPages.Parent != side, "four left categories and top subpages");
        var packets = Field<ListView>(Field<CapturePage>(form, "capturePage"), "packets");
        int compactContentWidth = 0, compactSummaryWidth = 0;
        foreach (var size in new[] { new Size(1050, 759), new Size(1440, 900) })
        {
            form.Size = size;
            string suffix = "-" + size.Width + "x" + size.Height;
            foreach (var id in ids)
            {
                shell.ShowView(id);
                await Task.Delay(35);
                Control page = content.Controls.Cast<Control>().Single(c => c.Visible);
                var actions = PageButtons(page).ToArray();
                Assert(shell.CurrentView == id && subPages.Controls.Count > 0 && actions.Length >= 3, "navigation and page actions: " + id);
                Capture(form, "preview-" + id + suffix + ".png");
                var occupied = new List<Rectangle>();
                foreach (Button button in actions)
                {
                    Rectangle bounds = button.RectangleToScreen(button.ClientRectangle);
                    Assert(!occupied.Any(previous => previous.IntersectsWith(bounds)), id + " actions do not overlap: " + button.Text);
                    occupied.Add(bounds);
                    Assert(bounds.Width > 0 && bounds.Height > 0, id + " action has a visible area: " + button.Text);
                    for (Control parent = button.Parent; parent != form; parent = parent.Parent)
                        Assert(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds), id + " action inside " + parent.GetType().Name + ": " + button.Text);
                }
            }
            if (size.Width == 1050)
            {
                compactContentWidth = content.Width;
                compactSummaryWidth = packets.Columns[6].Width;
            }
            else Assert(content.Width > compactContentWidth && packets.Columns[6].Width > compactSummaryWidth,
                "page and capture summary grow with the form");
        }
        form.Size = original;
        report.AppendLine("PASS four left categories, top subpages and visible page actions at both window sizes");
    }

    static async Task Exercise(MainForm form, SerialSession session, Func<FakeSerialTransport> current, Func<SerialSettings> applied)
    {
        var shell = Field<ToolShell>(form, "shell");
        await VerifyLayouts(form, shell);
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
        string settingsPath = Path.Combine(Path.GetTempPath(), "toolbox-ui-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(settingsPath, "{\"View\":\"projects\"}");
        using (var form = new MainForm(session, adb, capture, settingsPath))
        {
            var sockets = Field<SocketSession>(Field<SocketPage>(form, "socketPage"), "session");
            var shell = Field<ToolShell>(form, "shell");
            form.Shown += async delegate
            {
                try
                {
                    Assert(shell.CurrentView == "speed", "unknown saved view falls back to speed test");
                    await Exercise(form, session, () => fake, () => parameters);
                    passed = true;
                }
                catch (Exception ex) { report.AppendLine(ex.ToString()); File.WriteAllText(Path.Combine(output, "ui-test.txt"), report.ToString(), Encoding.UTF8); }
                form.Close();
            };
            Application.Run(form);
            passed = passed && !sockets.IsOpen && !adb.Busy && !capture.Busy;
            var saved = UiSettings.Load(settingsPath);
            bool settingsSaved = saved.View == shell.CurrentView && saved.Width == form.Width && saved.Height == form.Height;
            passed = passed && settingsSaved;
            report.AppendLine(settingsSaved ? "PASS window size and view saved separately on close" : "FAIL saving window settings");
        }
        File.Delete(settingsPath);
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
