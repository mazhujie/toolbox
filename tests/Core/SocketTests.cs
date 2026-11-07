using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class SocketTests
{
    static void Assert(bool value, string name) { if (!value) throw new Exception("FAIL socket: " + name); }
    static async Task Until(Func<bool> ready)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.ElapsedMilliseconds > 4000) throw new TimeoutException("socket fixture");
            await Task.Delay(10);
        }
    }
    static SocketOptions Options(SocketMode mode, int remotePort = 8080, int localPort = 0)
    { return new SocketOptions { Mode = mode, LocalAddress = IPAddress.Loopback, LocalPort = localPort, RemoteHost = "127.0.0.1", RemotePort = remotePort }; }
    static byte[] Received(ConcurrentQueue<DataRecord> records, string peer = null)
    { return records.Where(r => r.Kind == "RX" && (peer == null || r.Peer == peer)).SelectMany(r => r.Bytes).ToArray(); }

    internal static async Task<string> Run()
    {
        byte[] word = Encoding.UTF8.GetBytes("中文");
        var records = new[] {
            new DataRecord { Kind = "RX", Peer = "A", Bytes = word.Take(2).ToArray() },
            new DataRecord { Kind = "RX", Peer = "B", Bytes = Encoding.UTF8.GetBytes("另一个连接") },
            new DataRecord { Kind = "RX", Peer = "A", Bytes = word.Skip(2).ToArray() }
        };
        string text = DataCodec.RenderNetwork(records, false, "UTF-8", false, false);
        Assert(text == "另一个连接中文", "independent TCP stream decoders preserve split UTF-8");
        Assert(DataCodec.RenderNetwork(new[] { new DataRecord { Kind = "TX", Bytes = word }, new DataRecord { Kind = "RX", Bytes = word, Datagram = true } }, false, "UTF-8", false, false) == "中文", "plain receive view excludes outgoing bytes");
        Assert(DataCodec.RenderNetwork(new[] { new DataRecord { Kind = "RX", Bytes = new byte[0], Datagram = true } }, true, "UTF-8", true, true).Contains("空数据报"), "empty UDP datagram remains visible in logs");
        Assert(DataCodec.ExpandEscapes("A\\r\\n\\t\\0\\\\") == "A\r\n\t\0\\", "text escape parsing");
        bool invalid = false;
        try { DataCodec.ExpandEscapes("\\xFF"); } catch (FormatException) { invalid = true; }
        Assert(invalid, "unsupported escapes rejected");

        using (var a = new SocketSession())
        using (var b = new SocketSession())
        {
            var ar = new ConcurrentQueue<DataRecord>(); var br = new ConcurrentQueue<DataRecord>();
            a.Recorded += ar.Enqueue; b.Recorded += br.Enqueue;
            await a.Open(Options(SocketMode.Udp), CancellationToken.None);
            await b.Open(Options(SocketMode.Udp), CancellationToken.None);
            byte[] bytes = new byte[] { 0, 255, 13, 10 };
            await a.Send(bytes, "127.0.0.1", b.LocalEndpoint.Port, 0, CancellationToken.None);
            await Until(() => Received(br).Length == 4);
            Assert(Received(br).SequenceEqual(bytes) && ar.Any(r => r.Kind == "TX"), "UDP datagram and TX accounting");
            await b.Send(word, "127.0.0.1", a.LocalEndpoint.Port, 0, CancellationToken.None);
            await Until(() => Received(ar).Length == word.Length);
            Assert(Received(ar).SequenceEqual(word), "UDP return traffic");
            bool tooLarge = false;
            try { await a.Send(new byte[65508], "127.0.0.1", b.LocalEndpoint.Port, 0, CancellationToken.None); }
            catch (ArgumentException) { tooLarge = true; }
            Assert(tooLarge && a.IsOpen, "UDP oversize rejected before sending");
            int port = a.LocalEndpoint.Port;
            using (var duplicate = new SocketSession())
            {
                bool rejected = false;
                try { await duplicate.Open(Options(SocketMode.Udp, 8080, port), CancellationToken.None); }
                catch (SocketException) { rejected = true; }
                Assert(rejected && !duplicate.IsOpen && a.IsOpen, "bind conflict leaves original UDP session working");
            }
            a.Close();
            await a.Open(Options(SocketMode.Udp, 8080, port), CancellationToken.None);
            int before = Received(ar).Length;
            await b.Send(bytes, "127.0.0.1", port, 0, CancellationToken.None);
            await Until(() => Received(ar).Length == before + bytes.Length);
            Assert(a.IsOpen, "UDP close cancels pending receive and allows immediate rebind");
        }

        using (var server = new SocketSession())
        using (var first = new SocketSession())
        using (var second = new SocketSession())
        {
            var sr = new ConcurrentQueue<DataRecord>(); var fr = new ConcurrentQueue<DataRecord>(); var tr = new ConcurrentQueue<DataRecord>();
            server.Recorded += sr.Enqueue; first.Recorded += fr.Enqueue; second.Recorded += tr.Enqueue;
            await server.Open(Options(SocketMode.TcpServer), CancellationToken.None);
            int port = server.LocalEndpoint.Port;
            await first.Open(Options(SocketMode.TcpClient, port), CancellationToken.None);
            await second.Open(Options(SocketMode.TcpClient, port), CancellationToken.None);
            await Until(() => server.Peers.Length == 2);
            var peer = server.Peers.Single(p => p.Address == first.LocalEndpoint.ToString());
            await first.Send(word, "", 0, 0, CancellationToken.None);
            await Until(() => Received(sr, peer.ToString()).Length == word.Length);
            Assert(Received(sr, peer.ToString()).SequenceEqual(word), "TCP client to server data");
            await server.Send(new byte[] { 1, 2 }, "", 0, peer.Id, CancellationToken.None);
            await Until(() => Received(fr).Length == 2);
            Assert(Received(tr).Length == 0, "TCP targeted send excludes other clients");
            await server.Send(new byte[] { 3 }, "", 0, 0, CancellationToken.None);
            await Until(() => Received(fr).Length == 3 && Received(tr).Length == 1);
            Assert(Received(fr).SequenceEqual(new byte[] { 1, 2, 3 }), "TCP server broadcast reaches every client");
            byte[] x = Enumerable.Repeat((byte)0xAA, 8192).ToArray(), y = Enumerable.Repeat((byte)0xBB, 8192).ToArray();
            string secondPeer = server.Peers.Single(p => p.Address == second.LocalEndpoint.ToString()).ToString();
            await Task.WhenAll(second.Send(x, "", 0, 0, CancellationToken.None), second.Send(y, "", 0, 0, CancellationToken.None));
            await Until(() => Received(sr, secondPeer).Length == x.Length + y.Length);
            Assert(Received(sr, secondPeer).SequenceEqual(x.Concat(y)) || Received(sr, secondPeer).SequenceEqual(y.Concat(x)), "concurrent TCP sends do not interleave");
            server.Disconnect(peer.Id);
            await Until(() => !first.IsOpen && server.Peers.Length == 1);
            Assert(server.IsOpen && second.IsOpen, "selected disconnect preserves listener and other client");
            server.Close();
            await Until(() => !second.IsOpen);
            await server.Open(Options(SocketMode.TcpServer, 8080, port), CancellationToken.None);
            Assert(server.IsOpen, "TCP close releases clients and listening port");
        }
        using (var cancelled = new SocketSession())
        using (var token = new CancellationTokenSource())
        {
            token.Cancel();
            bool stopped = false;
            try { await cancelled.Open(Options(SocketMode.TcpClient), token.Token); } catch (OperationCanceledException) { stopped = true; }
            Assert(stopped && !cancelled.IsOpen, "cancelled connect allocates no active socket");
        }
        using (var token = new CancellationTokenSource(30))
        {
            bool stopped = false, aborted = false;
            var task = new TaskCompletionSource<bool>();
            try { await SocketSession.Timed(task.Task, token.Token, () => aborted = true); }
            catch (OperationCanceledException) { stopped = true; }
            Assert(stopped && aborted, "pending network operation responds to cancellation");
        }
        return "PASS network text escapes and per-client UTF-8 stream decoding\r\nPASS real UDP loopback, binary datagrams, size limit, bind conflict and reopen\r\nPASS real TCP clients/server, targeted send, broadcast, serialized writes and disconnect\r\nPASS pending receive/accept cleanup, port reuse and cancellation\r\n";
    }
}
