using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

internal static partial class ToolboxUiTests
{
    static async Task ExerciseSerial(MainForm form, ToolShell shell, SerialSession session, Func<FakeSerialTransport> current, Func<SerialSettings> applied)
    {
        var workspace = Field<SerialWorkspace>(form, "serialWorkspace");
        var ports = Field<ComboBox>(workspace, "ports");
        var outgoing = Field<TextBox>(workspace, "outgoing");
        var incoming = Field<TextBox>(workspace, "incoming");
        var connect = Field<Button>(workspace, "connect");
        var advanced = Field<LinkLabel>(workspace, "advancedToggle");
        var automatic = Field<Button>(workspace, "automatic");
        var timer = Field<Timer>(workspace, "sendTimer");
        shell.ShowView("serial");
        Assert(Field<FlowLayoutPanel>(shell, "pages").Controls.Count == 1, "one serial page");
        ports.SelectedIndex = -1;
        Assert(!connect.Enabled, "connect unavailable without a port");
        ports.Items.Clear();
        ports.Items.Add("COM99");
        ports.SelectedItem = "COM99";
        Assert(connect.Enabled, "port selection enables connect");
        int sendTop = outgoing.PointToScreen(Point.Empty).Y;
        Assert(!Field<bool>(workspace, "advancedExpanded"), "advanced parameters initially collapsed");
        ToggleAdvanced(advanced);
        Assert(Field<Panel>(workspace, "advancedPanel").Visible && outgoing.PointToScreen(Point.Empty).Y == sendTop, "advanced parameters expand without moving send area");
        Capture(form, "preview-serial-advanced.png");
        Field<ComboBox>(workspace, "baud").Text = "9600";
        Field<ComboBox>(workspace, "bits").SelectedItem = "7";
        Field<ComboBox>(workspace, "parity").SelectedItem = "Even";
        Field<ComboBox>(workspace, "stops").SelectedItem = "2";
        Field<ComboBox>(workspace, "flow").SelectedIndex = 2;
        Field<CheckBox>(workspace, "dtr").Checked = true;
        Assert(!Field<CheckBox>(workspace, "rts").Enabled && Field<Label>(workspace, "config").Text.Contains("7E2"), "advanced parameter summary and RTS flow control");
        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "open simulated port");
        Assert(!ports.Enabled && connect.Text == "断开串口", "connection toggle and settings lock");
        Assert(applied().Baud == 9600 && applied().DataBits == 7 && applied().Parity == System.IO.Ports.Parity.Even && applied().StopBits == System.IO.Ports.StopBits.Two && applied().Dtr, "advanced settings reach serial transport");
        connect.PerformClick();
        await Until(() => !session.IsOpen && connect.Enabled && ports.Enabled, "disconnect via same button");
        Assert(current().Disposed && connect.Text == "连接串口", "disconnect releases transport and restores connect action");
        Field<ComboBox>(workspace, "baud").Text = "115200";
        Field<ComboBox>(workspace, "bits").SelectedItem = "8";
        Field<ComboBox>(workspace, "parity").SelectedItem = "None";
        Field<ComboBox>(workspace, "stops").SelectedItem = "1";
        Field<ComboBox>(workspace, "flow").SelectedIndex = 0;
        Field<CheckBox>(workspace, "dtr").Checked = false;
        ToggleAdvanced(advanced);
        Field<Button>(workspace, "clear").PerformClick();
        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "reconnect after editing settings");
        report.AppendLine("PASS inline serial settings, advanced foldout, applied parameters and single connect/disconnect action");

        shell.ShowView("serial");
        outgoing.Text = "界面演示：模拟串口回环，不代表真实设备数据。";
        Field<ComboBox>(workspace, "newline").SelectedItem = "CRLF";
        Field<Button>(workspace, "send").PerformClick();
        await Until(() => incoming.Text.Contains("RX") && incoming.Text.Contains("界面演示"), "text send/receive");
        Assert(current().Writes == 1, "one manual transmission");
        Assert(incoming.Text.Contains("TX") && !incoming.Text.Contains("�"), "UTF-8 receive display");
        Capture(form, "preview-serial-demo.png");

        outgoing.Text = "AA BB 0D 0A";
        Field<CheckBox>(workspace, "txHex").Checked = true;
        Field<CheckBox>(workspace, "rxHex").Checked = true;
        Field<Button>(workspace, "send").PerformClick();
        await Until(() => current().Writes == 2 && incoming.Text.Contains("AA BB 0D 0A"), "HEX send/receive");
        report.AppendLine("PASS actual UI open, settings lock, text/HEX send and receive (simulated transport)");

        await ExerciseClearRecords(workspace, session, current);

        await ExerciseMultiSend(form, workspace, session, current);

        Field<NumericUpDown>(workspace, "interval").Value = 100;
        automatic.PerformClick();
        await Until(() => current().Writes >= 4, "periodic transmission");
        Assert(timer.Enabled && !outgoing.Enabled, "periodic payload lock");
        shell.ShowView("speed");
        int before = current().Writes;
        await Until(() => current().Writes >= before + 2, "background periodic transmission");
        Assert(session.IsOpen, "connection survives network navigation");
        Field<Button>(shell, "serial").PerformClick();
        Assert(shell.CurrentView == "serial", "remember serial subpage");
        automatic.PerformClick();
        await Until(() => !Field<bool>(workspace, "writing"), "finish in-flight write");
        before = current().Writes;
        await Task.Delay(260);
        Assert(current().Writes == before && !timer.Enabled && outgoing.Enabled, "periodic stop");
        report.AppendLine("PASS periodic sends, page switching, remembered subpage and stop");

        shell.ShowView("speed");
        current().Fault();
        await Until(() => !session.IsOpen && current().Disposed, "background device failure cleanup");
        shell.ShowView("serial");
        await Until(() => connect.Enabled && connect.Text == "连接串口", "recover disconnected UI");
        Assert(incoming.Text.Contains("fixture disconnect"), "device error visible");
        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "reopen after failure");
        current().FailWrite = true;
        Field<Button>(workspace, "send").PerformClick();
        await Until(() => !session.IsOpen && connect.Enabled, "send failure closes port");
        Assert(!timer.Enabled && current().Disposed, "write error cleanup");
        report.AppendLine("PASS device disconnect on hidden page, reconnect and write failure cleanup");

        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "final open");
        automatic.PerformClick();
        await Until(() => current().Writes > 0, "final periodic send");
        // Main closes this actual form while periodic sending is active.
    }

    static async Task ExerciseClearRecords(SerialWorkspace workspace, SerialSession session, Func<FakeSerialTransport> current)
    {
        var displayTimer = Field<Timer>(workspace, "displayTimer");
        var incoming = Field<TextBox>(workspace, "incoming");
        var outgoing = Field<TextBox>(workspace, "outgoing");
        var counters = Field<Label>(workspace, "counters");
        var clear = Field<Button>(workspace, "clear");
        var automatic = Field<Button>(workspace, "automatic");
        string text = outgoing.Text;
        Assert(Field<long>(workspace, "received") > 0 && Field<long>(workspace, "transmitted") > 0, "nonzero RX/TX before clear");
        displayTimer.Stop();
        await Task.Run(() =>
        {
            current().Receive(Encoding.UTF8.GetBytes("OLD-QUEUED"));
            current().Receive(new byte[1048577]);
        });
        Assert(Field<int>(workspace, "queuedBytes") > 0 && Field<long>(workspace, "dropped") > 0, "receive data and overflow warning pending before clear");
        clear.PerformClick();
        Assert(incoming.TextLength == 0 && Field<long>(workspace, "received") == 0 && Field<long>(workspace, "transmitted") == 0, "clear resets visible history and both byte counts");
        Assert(Field<int>(workspace, "queuedBytes") == 0 && Field<long>(workspace, "dropped") == 0, "clear discards pending data and overflow accounting");
        Assert(counters.Text.Contains("RX  0 字节") && counters.Text.Contains("TX  0 字节") && !Field<Button>(workspace, "save").Enabled, "zero counts and empty log save state update immediately");
        Assert(session.IsOpen && outgoing.Text == text, "clear preserves connection and send content");
        displayTimer.Start();
        await Task.Delay(150);
        Assert(incoming.TextLength == 0 && Field<long>(workspace, "received") == 0, "old queued records do not reappear");

        Field<Button>(workspace, "send").PerformClick();
        await Until(() => !Field<bool>(workspace, "writing") && incoming.Text.Contains("RX"), "new traffic after clear");
        Assert(Field<long>(workspace, "received") == 4 && Field<long>(workspace, "transmitted") == 4, "RX/TX restart from zero for new traffic");

        Field<NumericUpDown>(workspace, "interval").Value = 100;
        int before = current().Writes;
        automatic.PerformClick();
        await Until(() => current().Writes > before && !Field<bool>(workspace, "writing"), "periodic send before clear");
        clear.PerformClick();
        Assert(Field<Timer>(workspace, "sendTimer").Enabled && session.IsOpen, "clear leaves periodic sending active");
        await Until(() => Field<long>(workspace, "transmitted") >= 4, "periodic counting restarts after clear");
        automatic.PerformClick();
        await Until(() => !Field<bool>(workspace, "writing"), "periodic send stopped after reset test");
        report.AppendLine("PASS clear resets RX/TX, pending receive data and warnings; new traffic counts from zero without disconnecting or stopping periodic sending");
    }

    static async Task ExerciseMultiSend(Form form, SerialWorkspace workspace, SerialSession session, Func<FakeSerialTransport> current)
    {
        var tabs = Field<TabControl>(workspace, "sendTabs");
        var grid = Field<DataGridView>(workspace, "commands");
        var send = Field<Button>(workspace, "send");
        var automatic = Field<Button>(workspace, "automatic");
        var connect = Field<Button>(workspace, "connect");
        var interval = Field<NumericUpDown>(workspace, "interval");
        var timer = Field<Timer>(workspace, "sendTimer");
        tabs.SelectedIndex = 1;
        Field<Button>(workspace, "clear").PerformClick();
        typeof(SerialWorkspace).GetMethod("Info", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(workspace, new object[] { "DEMO", "界面演示：模拟串口回环，不代表真实设备数据。" });
        grid.Rows.Clear();
        grid.Rows.Add(true, 1, "AT", false);
        grid.Rows.Add(false, 2, "GG (unchecked invalid HEX)", true);
        grid.Rows.Add(true, 3, "AA BB 0D 0A", true);
        interval.Value = 100;
        int before = current().Writes;
        grid.CurrentCell = grid.Rows[1].Cells["Content"];
        send.PerformClick();
        Assert(!tabs.Enabled && send.Enabled && send.Text == "停止发送", "batch locks editor and exposes stop action");
        await Until(() => !Field<bool>(workspace, "writing"), "selected batch completed");
        var sent = current().Sent.ToArray().Skip(before).ToArray();
        Assert(sent.Length == 2 && Encoding.UTF8.GetString(sent[0]) == "AT\r\n" && sent[1].SequenceEqual(new byte[] { 0xAA, 0xBB, 13, 10 }), "only checked rows, visual order, per-row HEX and text suffix");
        grid.CurrentCell = grid.Rows[0].Cells["Content"];
        grid.Rows[1].Cells["Content"].Value = "AA 55";
        Capture(form, "preview-serial-multi-demo.png");
        grid.Rows[1].Cells["Content"].Value = "GG (unchecked invalid HEX)";

        grid.CurrentCell = grid.Rows[1].Cells["Selected"];
        grid.BeginEdit(false);
        ((DataGridViewCheckBoxCell)grid.CurrentCell).EditingCellFormattedValue = true;
        grid.NotifyCurrentCellDirty(true);
        Assert(Convert.ToBoolean(grid.CurrentCell.Value), "checkbox changes committed immediately");
        before = current().Writes;
        send.PerformClick();
        Assert(current().Writes == before && Field<Label>(workspace, "notice").Text.Contains("第 2 条"), "validate entire selected batch before sending any bytes");
        var tools = Field<FlowLayoutPanel>(workspace, "commandTools");
        ((Button)tools.Controls[3]).PerformClick();
        send.PerformClick();
        Assert(current().Writes == before && Field<Label>(workspace, "notice").Text.Contains("请先勾选"), "empty selection sends nothing");
        grid.Rows[0].Cells["Selected"].Value = true;
        grid.Rows[2].Cells["Selected"].Value = true;
        interval.Value = 300;
        send.PerformClick();
        await Until(() => current().Writes == before + 1, "first batch item");
        send.PerformClick();
        await Until(() => !Field<bool>(workspace, "writing"), "cancel between selected rows");
        await Task.Delay(350);
        Assert(current().Writes == before + 1, "stop skips unsent rows");
        report.AppendLine("PASS multi-send selection/order, mixed text/HEX, immediate checkbox commit, prevalidation and cancellation");

        interval.Value = 100;
        before = current().Writes;
        automatic.PerformClick();
        await Until(() => current().Writes >= before + 4, "two multi-send cycles");
        automatic.PerformClick();
        await Until(() => !Field<bool>(workspace, "writing"), "stop multi-send cycle");
        sent = current().Sent.ToArray().Skip(before).ToArray();
        Assert(sent.Length >= 4 && sent[0].SequenceEqual(sent[2]) && sent[1].SequenceEqual(sent[3]) && !sent[0].SequenceEqual(sent[1]), "periodic rows cycle in order without overlap");
        before = current().Writes;
        await Task.Delay(250);
        Assert(current().Writes == before && !timer.Enabled && current().PeakWrites == 1, "cycle stop and serialized writes");

        interval.Value = 300;
        send.PerformClick();
        await Until(() => current().Writes == before + 1, "batch before disconnect");
        connect.PerformClick();
        await Until(() => !session.IsOpen && !Field<bool>(workspace, "writing") && connect.Enabled, "disconnect cancels batch");
        await Task.Delay(350);
        Assert(current().Writes == before + 1 && current().Disposed, "disconnect prevents remaining rows");
        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "reconnect after batch cancellation");

        grid.Rows.Add(true, 4, "SHOULD_NOT_SEND", false);
        current().FailAtWrite = 2;
        interval.Value = 100;
        send.PerformClick();
        await Until(() => !session.IsOpen && !Field<bool>(workspace, "writing"), "batch write failure");
        Assert(current().Writes == 1 && current().Disposed, "failure stops remaining rows and closes port");
        connect.PerformClick();
        await Until(() => session.IsOpen && connect.Enabled, "reconnect after failed batch");
        tabs.SelectedIndex = 0;
        report.AppendLine("PASS multi-send cyclic timing, disconnect cancellation and mid-batch failure cleanup");
    }

}
