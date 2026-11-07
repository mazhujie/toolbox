using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class ToolboxUiTests
{
    static async Task ExerciseCapture(MainForm form, ToolShell shell)
    {
        await CaptureTests.Run(Path.Combine(output, "capture-core-" + Guid.NewGuid().ToString("N")), report);
        var page = Field<CapturePage>(form, "capturePage");
        var list = Field<ListView>(page, "packets");
        var start = Field<Button>(page, "start");
        var stop = Field<Button>(page, "stop");
        var pause = Field<CheckBox>(page, "pause");
        var adapters = Field<ComboBox>(page, "adapters");
        shell.ShowView("capture");
        Assert(!page.Busy && !start.Enabled && packetSource == null, "no automatic enumeration or capture");
        failCaptureCatalog = true; await page.RefreshAdapters();
        Assert(!start.Enabled && Field<Label>(page, "notice").Text.Contains("simulated missing Npcap"), "missing dependency is visible");
        failCaptureCatalog = false; await page.RefreshAdapters();
        Assert(start.Enabled && adapters.SelectedIndex == 0, "single adapter selection");
        start.PerformClick(); await Until(() => packetSource != null, "capture source opens");
        Assert(page.Busy && stop.Enabled && !adapters.Enabled && !start.Enabled, "capture locks settings");
        packetSource.Frames.Enqueue(CaptureTests.Tcp());
        await Until(() => list.VirtualListSize == 1, "live packet display");
        list.SelectedIndices.Add(0);
        Assert(Field<TextBox>(page, "detail").Text.Contains("192.0.2.1:12345"), "selected packet HEX details");
        Capture(form, "preview-capture-demo.png");
        pause.Checked = true;
        for (int i = 0; i < 2100; i++) packetSource.Frames.Enqueue(CaptureTests.Tcp());
        await Until(() => Field<CaptureSession>(page, "session").Count == 2101, "paused capture continues writing");
        Assert(list.VirtualListSize == 1, "pause freezes display only");
        shell.ShowView("speed"); await Task.Delay(150);
        Assert(page.Busy, "capture survives navigation");
        shell.ShowView("capture"); pause.Checked = false;
        await Until(() => list.VirtualListSize == 1000, "bounded recent-packet display resumes");
        stop.PerformClick(); await Until(() => !page.Busy, "stop capture");
        Assert(packetSource.Disposed && CaptureTests.ReadFile(Field<string>(page, "captureFile")) == 2101, "all packets saved despite display eviction");
        Assert(Field<Button>(page, "save").Enabled && adapters.Enabled, "stopped capture can be saved and settings unlocked");
        report.AppendLine("PASS capture UI: missing dependency, explicit start, live packets, HEX selection, pause/resume, hidden-page capture and bounded display with complete PCAP");
        packetSource = null; start.PerformClick(); await Until(() => packetSource != null, "restart capture");
        packetSource.Fail = true; await Until(() => !page.Busy, "read error stops capture");
        Assert(packetSource.Disposed && Field<Label>(page, "notice").Text.Contains("simulated capture failure"), "error shown and source closed");
        failCaptureCatalog = true; await page.RefreshAdapters();
        Assert(adapters.Items.Count == 0 && !start.Enabled, "failed refresh clears stale adapter");
        failCaptureCatalog = false; await page.RefreshAdapters();
        packetSource = null; start.PerformClick(); await Until(() => packetSource != null, "capture running for form-close test");
        report.AppendLine("PASS capture restart, error cleanup and stale adapter rejection");
    }

}
