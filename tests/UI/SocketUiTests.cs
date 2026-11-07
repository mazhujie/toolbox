using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

internal static partial class ToolboxUiTests
{
    static async Task ExerciseSockets(MainForm form, ToolShell shell)
    {
        var page = Field<SocketPage>(form, "socketPage");
        var session = Field<SocketSession>(page, "session");
        var connect = Field<Button>(page, "connect");
        var send = Field<Button>(page, "send");
        var automatic = Field<Button>(page, "automatic");
        var incoming = Field<TextBox>(page, "incoming");
        var outgoing = Field<TextBox>(page, "outgoing");
        var protocol = Field<ComboBox>(page, "protocol");
        Field<Button>(shell, "debug").PerformClick();
        Assert(shell.CurrentView == "lan" && Field<FlowLayoutPanel>(shell, "pages").Controls.Count == 3, "network-debug category contains sockets, LAN scan and capture, and remembers the LAN scan page");
        shell.ShowView("socket");
        Field<ComboBox>(page, "localAddress").Text = "127.0.0.1";
        Field<NumericUpDown>(page, "localPort").Value = 0;
        using (var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            Field<NumericUpDown>(page, "remotePort").Value = ((IPEndPoint)remote.Client.LocalEndPoint).Port;
            connect.PerformClick();
            await Until(() => session.IsOpen && send.Enabled, "UDP page open");
            Assert(!protocol.Enabled && !Field<ComboBox>(page, "localAddress").Enabled, "socket connection settings locked");
            outgoing.Text = "本机 UDP 回环测试：你好";
            var packet = remote.ReceiveAsync();
            send.PerformClick();
            await Until(() => packet.IsCompleted, "UDP UI sends real datagram");
            Assert(Encoding.UTF8.GetString(packet.Result.Buffer) == outgoing.Text, "UDP page text bytes");
            await remote.SendAsync(packet.Result.Buffer, packet.Result.Buffer.Length, session.LocalEndpoint);
            await Until(() => incoming.Text.Contains("RX") && incoming.Text.Contains("回环测试"), "UDP echo visible");
            Capture(form, "preview-debug-udp.png");
            shell.ShowView("speed");
            byte[] bytes = Encoding.UTF8.GetBytes("切页后继续接收");
            await remote.SendAsync(bytes, bytes.Length, session.LocalEndpoint);
            await Until(() => incoming.Text.Contains("切页后继续接收"), "hidden socket page still receives");
            Field<Button>(shell, "debug").PerformClick();
            Assert(session.IsOpen, "category switching preserves socket");

            var pause = Field<CheckBox>(page, "pause");
            pause.Checked = true;
            string shown = incoming.Text;
            bytes = Encoding.UTF8.GetBytes("暂停后恢复显示");
            long before = Field<long>(page, "received");
            await remote.SendAsync(bytes, bytes.Length, session.LocalEndpoint);
            await Until(() => Field<long>(page, "received") == before + bytes.Length, "paused display still counts receive");
            await Task.Delay(150);
            Assert(incoming.Text == shown, "pause only freezes display");
            pause.Checked = false;
            Assert(incoming.Text.Contains("暂停后恢复显示"), "resume shows retained records");
            Field<Button>(page, "clear").PerformClick();
            Assert(incoming.TextLength == 0 && Field<long>(page, "received") == 0 && Field<long>(page, "transmitted") == 0, "network clear resets logs and counts");

            Field<CheckBox>(page, "txHex").Checked = true;
            Field<CheckBox>(page, "rxHex").Checked = true;
            outgoing.Text = "00 FF 0D 0A";
            packet = remote.ReceiveAsync();
            send.PerformClick();
            await Until(() => packet.IsCompleted, "UDP HEX send");
            Assert(packet.Result.Buffer.SequenceEqual(new byte[] { 0, 255, 13, 10 }), "UDP UI binary payload");
            await remote.SendAsync(packet.Result.Buffer, packet.Result.Buffer.Length, session.LocalEndpoint);
            await Until(() => incoming.Text.Contains("RX") && incoming.Text.Contains("00 FF 0D 0A"), "UDP HEX receive");
            Field<NumericUpDown>(page, "interval").Value = 100;
            before = Field<long>(page, "transmitted");
            automatic.PerformClick();
            await Until(() => Field<long>(page, "transmitted") >= before + 8, "UDP periodic sends");
            automatic.PerformClick();
            await Until(() => !Field<bool>(page, "writing"), "UDP periodic stop");
            before = Field<long>(page, "transmitted");
            await Task.Delay(250);
            Assert(Field<long>(page, "transmitted") == before && !Field<Timer>(page, "sendTimer").Enabled, "UDP periodic stop prevents later sends");
            connect.PerformClick();
            Assert(!session.IsOpen && protocol.Enabled, "UDP page close unlocks settings");
        }
        report.AppendLine("PASS real UDP UI text/HEX, background receive, pause/resume, clear counters and periodic send/stop");

        protocol.SelectedIndex = 2;
        Field<NumericUpDown>(page, "localPort").Value = 0;
        Field<CheckBox>(page, "txHex").Checked = false;
        Field<CheckBox>(page, "rxHex").Checked = false;
        Field<Button>(page, "clear").PerformClick();
        connect.PerformClick();
        await Until(() => session.IsOpen, "TCP server page listens");
        using (var first = new SocketSession())
        using (var second = new SocketSession())
        {
            var a = new ConcurrentQueue<DataRecord>(); var b = new ConcurrentQueue<DataRecord>();
            first.Recorded += a.Enqueue; second.Recorded += b.Enqueue;
            var options = new SocketOptions { Mode = SocketMode.TcpClient, LocalAddress = IPAddress.Loopback, RemotePort = session.LocalEndpoint.Port };
            await first.Open(options, CancellationToken.None);
            await second.Open(options, CancellationToken.None);
            var choices = Field<ComboBox>(page, "peers");
            await Until(() => choices.Items.Count == 3 && send.Enabled, "TCP clients appear in list");
            choices.SelectedIndex = 1;
            outgoing.Text = "服务端定向发送";
            send.PerformClick();
            await Until(() => a.Any(r => r.Kind == "RX") && send.Enabled, "TCP targeted UI send completes and unlocks controls");
            Assert(!b.Any(r => r.Kind == "RX"), "TCP UI target excludes second client");
            choices.SelectedIndex = 0;
            outgoing.Text = "本机 TCP 服务端：发送给全部客户端";
            send.PerformClick();
            await Until(() => b.Any(r => r.Kind == "RX"), "TCP UI broadcast");
            await Until(() => incoming.Text.Contains("发送给全部客户端"), "TCP broadcast log visible");
            Capture(form, "preview-debug-server.png");
            choices.SelectedIndex = 1;
            Field<Button>(page, "disconnect").PerformClick();
            await Until(() => !first.IsOpen && session.Peers.Length == 1, "TCP UI disconnect selected client");
            Assert(session.IsOpen && second.IsOpen, "TCP UI keeps other client and listener");
            connect.PerformClick();
            await Until(() => !second.IsOpen, "TCP server page closes accepted sockets");
        }

        using (var server = new SocketSession())
        {
            var messages = new ConcurrentQueue<DataRecord>();
            server.Recorded += messages.Enqueue;
            await server.Open(new SocketOptions { Mode = SocketMode.TcpServer, LocalAddress = IPAddress.Loopback }, CancellationToken.None);
            protocol.SelectedIndex = 1;
            Field<NumericUpDown>(page, "remotePort").Value = server.LocalEndpoint.Port;
            connect.PerformClick();
            await Until(() => session.IsOpen && send.Enabled, "TCP client page connects");
            outgoing.Text = "TCP 客户端回环测试";
            send.PerformClick();
            await Until(() => messages.Any(r => r.Kind == "RX"), "TCP client UI sends");
            await Until(() => incoming.Text.Contains("TCP 客户端回环测试"), "TCP client log visible");
            Capture(form, "preview-debug-client.png");
            connect.PerformClick();
        }
        // Leave a loopback listener open to check form shutdown below.
        protocol.SelectedIndex = 2;
        Field<NumericUpDown>(page, "localPort").Value = 0;
        connect.PerformClick();
        await Until(() => session.IsOpen, "listener before form shutdown");
        report.AppendLine("PASS real TCP UI client/server, client list, targeted/broadcast send and selected disconnect");
    }

}
