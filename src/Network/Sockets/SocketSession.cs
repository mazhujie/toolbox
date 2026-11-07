using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal enum SocketMode { Udp, TcpClient, TcpServer }

internal sealed class SocketOptions
{
    public SocketMode Mode;
    public IPAddress LocalAddress = IPAddress.Any;
    public int LocalPort, RemotePort = 8080;
    public string RemoteHost = "127.0.0.1";

    internal void Validate()
    {
        if (LocalAddress == null || LocalAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("本地地址必须是 IPv4。");
        if (LocalPort < 0 || LocalPort > 65535) throw new ArgumentException("本地端口应为 0–65535。");
        if (Mode == SocketMode.TcpClient && (String.IsNullOrWhiteSpace(RemoteHost) || RemotePort < 1 || RemotePort > 65535))
            throw new ArgumentException("请填写目标地址和 1–65535 的目标端口。");
    }
}

internal sealed class SocketPeer
{
    internal long Id;
    internal string Address;
    internal TcpClient Client;
    public override string ToString() { return Id == 0 ? "全部客户端" : "#" + Id + "  " + Address; }
}

internal sealed class SocketSession : IDisposable
{
    readonly object sync = new object();
    readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
    readonly Dictionary<long, SocketPeer> peers = new Dictionary<long, SocketPeer>();
    UdpClient udp;
    TcpListener listener;
    TcpClient pending;
    int generation;
    long nextPeer;
    bool open;
    SocketMode mode;
    IPEndPoint local;
    internal event Action<DataRecord> Recorded;

    internal bool IsOpen { get { lock (sync) return open; } }
    internal IPEndPoint LocalEndpoint { get { lock (sync) return local; } }
    internal SocketPeer[] Peers { get { lock (sync) return peers.Values.OrderBy(p => p.Id).ToArray(); } }

    void Record(string kind, string peer, byte[] data, string message, bool datagram)
    {
        var handler = Recorded;
        if (handler != null) handler(new DataRecord { Kind = kind, Peer = peer, Bytes = data, Message = message, Datagram = datagram });
    }

    internal async Task Open(SocketOptions options, CancellationToken token)
    {
        options.Validate();
        Close();
        int version;
        TcpClient client = null;
        lock (sync)
        {
            version = generation;
            mode = options.Mode;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (version != generation) throw new OperationCanceledException();
                var endpoint = new IPEndPoint(options.LocalAddress, options.LocalPort);
                if (mode == SocketMode.Udp)
                {
                    udp = new UdpClient(AddressFamily.InterNetwork);
                    udp.ExclusiveAddressUse = true;
                    udp.EnableBroadcast = true;
                    udp.Client.Bind(endpoint);
                    local = (IPEndPoint)udp.Client.LocalEndPoint;
                    open = true;
                    Observe(ReceiveUdp(udp, version));
                }
                else if (mode == SocketMode.TcpServer)
                {
                    listener = new TcpListener(endpoint);
                    listener.ExclusiveAddressUse = true;
                    listener.Start();
                    local = (IPEndPoint)listener.LocalEndpoint;
                    open = true;
                    Observe(AcceptClients(listener, version));
                }
                else
                {
                    pending = client = new TcpClient(AddressFamily.InterNetwork);
                    client.Client.Bind(endpoint);
                }
            }
            if (client != null)
            {
                using (token.Register(() => client.Close()))
                {
                    IPAddress address = await Resolve(options.RemoteHost, token).ConfigureAwait(false);
                    await Timed(client.ConnectAsync(address, options.RemotePort), token, client.Close).ConfigureAwait(false);
                }
                lock (sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (version != generation) throw new OperationCanceledException();
                    pending = null;
                    local = (IPEndPoint)client.Client.LocalEndPoint;
                    open = true;
                    AddPeer(client, version);
                }
            }
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (version != generation) throw new OperationCanceledException();
                Record("OPEN", "", null, mode + " 已打开：" + local, false);
            }
        }
        catch
        {
            if (client != null) client.Close();
            lock (sync) { if (version == generation) Close(); }
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    static void Observe(Task task)
    {
        task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    internal static async Task Timed(Task task, CancellationToken token, Action abort)
    {
        using (var delay = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            if (await Task.WhenAny(task, Task.Delay(5000, delay.Token)).ConfigureAwait(false) == task)
            {
                delay.Cancel();
                await task.ConfigureAwait(false);
                return;
            }
            if (abort != null) abort();
            Observe(task);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("网络操作超时（5 秒）。");
        }
    }

    static async Task<IPAddress> Resolve(string host, CancellationToken token)
    {
        if (String.IsNullOrWhiteSpace(host)) throw new ArgumentException("目标地址不能为空。");
        IPAddress address;
        if (IPAddress.TryParse(host, out address))
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("当前仅支持 IPv4。");
            return address;
        }
        var lookup = Dns.GetHostAddressesAsync(host);
        await Timed(lookup, token, null).ConfigureAwait(false);
        address = lookup.Result.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
        if (address == null) throw new IOException("目标没有可用的 IPv4 地址。");
        return address;
    }

    async Task ReceiveUdp(UdpClient socket, int version)
    {
        try
        {
            while (true)
            {
                var packet = await socket.ReceiveAsync().ConfigureAwait(false);
                lock (sync)
                {
                    if (version != generation || !open) return;
                    Record("RX", packet.RemoteEndPoint.ToString(), packet.Buffer, null, true);
                }
            }
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (version != generation) return;
                Record("ERROR", "", null, ex.Message, true);
                Close();
            }
        }
    }

    async Task AcceptClients(TcpListener server, int version)
    {
        try
        {
            while (true)
            {
                var client = await server.AcceptTcpClientAsync().ConfigureAwait(false);
                lock (sync)
                {
                    if (version != generation || !open) { client.Close(); return; }
                    if (peers.Count >= 64)
                    {
                        client.Close();
                        Record("WARN", "", null, "已达到 64 个 TCP 客户端的上限。", false);
                        continue;
                    }
                    AddPeer(client, version);
                }
            }
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (version != generation) return;
                Record("ERROR", "", null, ex.Message, false);
                Close();
            }
        }
    }

    void AddPeer(TcpClient client, int version)
    {
        client.NoDelay = true;
        var peer = new SocketPeer { Id = ++nextPeer, Address = client.Client.RemoteEndPoint.ToString(), Client = client };
        peers.Add(peer.Id, peer);
        Record("OPEN", peer.ToString(), null, "TCP 连接已建立。", false);
        Observe(ReadPeer(peer, version));
    }

    async Task ReadPeer(SocketPeer peer, int version)
    {
        try
        {
            var stream = peer.Client.GetStream();
            var buffer = new byte[4096];
            while (true)
            {
                int n = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (n == 0) break;
                var copy = new byte[n];
                Buffer.BlockCopy(buffer, 0, copy, 0, n);
                lock (sync)
                {
                    if (version != generation || !peers.ContainsKey(peer.Id)) return;
                    Record("RX", peer.ToString(), copy, null, false);
                }
            }
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (version == generation && peers.ContainsKey(peer.Id)) Record("ERROR", peer.ToString(), null, ex.Message, false);
            }
        }
        finally
        {
            lock (sync) { if (version == generation) RemovePeer(peer.Id); }
            peer.Client.Close();
        }
    }

    void RemovePeer(long id)
    {
        SocketPeer peer;
        if (!peers.TryGetValue(id, out peer)) return;
        peers.Remove(id);
        peer.Client.Close();
        if (mode == SocketMode.TcpClient) open = false;
        Record("CLOSE", peer.ToString(), null, "TCP 连接已断开。", false);
    }

    internal void Disconnect(long id)
    {
        lock (sync)
        {
            if (id == 0) foreach (long key in peers.Keys.ToArray()) RemovePeer(key);
            else RemovePeer(id);
        }
    }

    internal async Task Send(byte[] data, string host, int port, long peerId, CancellationToken token)
    {
        if (data == null || data.Length == 0 || data.Length > 65536) throw new ArgumentException("单次发送应为 1–65,536 字节。");
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            UdpClient socket;
            SocketPeer[] targets;
            int version;
            lock (sync)
            {
                if (!open) throw new InvalidOperationException("请先打开网络连接。");
                socket = udp;
                version = generation;
                targets = peers.Values.Where(p => peerId == 0 || p.Id == peerId).ToArray();
            }
            if (socket != null)
            {
                if (data.Length > 65507) throw new ArgumentException("IPv4 UDP 单个数据报最多 65,507 字节。");
                if (port < 1 || port > 65535) throw new ArgumentException("目标端口应为 1–65535。");
                var endpoint = new IPEndPoint(await Resolve(host, token).ConfigureAwait(false), port);
                token.ThrowIfCancellationRequested();
                Task<int> send;
                lock (sync)
                {
                    if (version != generation || !open) throw new OperationCanceledException();
                    send = socket.SendAsync(data, data.Length, endpoint);
                }
                await Timed(send, token, () => CloseVersion(version)).ConfigureAwait(false);
                lock (sync) { if (version == generation) Record("TX", endpoint.ToString(), (byte[])data.Clone(), null, true); }
            }
            else
            {
                if (targets.Length == 0) throw new InvalidOperationException("没有可发送的 TCP 客户端。");
                foreach (var peer in targets)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        Task send;
                        lock (sync)
                        {
                            if (version != generation || !peers.ContainsKey(peer.Id)) throw new IOException("目标连接已断开。");
                            send = peer.Client.GetStream().WriteAsync(data, 0, data.Length);
                        }
                        await Timed(send, token, peer.Client.Close).ConfigureAwait(false);
                        lock (sync) { if (version == generation) Record("TX", peer.ToString(), (byte[])data.Clone(), null, false); }
                    }
                    catch
                    {
                        lock (sync) { if (version == generation) RemovePeer(peer.Id); }
                        throw;
                    }
                }
            }
        }
        finally { sendGate.Release(); }
    }

    void CloseVersion(int version) { lock (sync) { if (version == generation) Close(); } }

    internal void Close()
    {
        lock (sync)
        {
            generation++;
            open = false;
            if (pending != null) { pending.Close(); pending = null; }
            if (udp != null) { udp.Close(); udp = null; }
            if (listener != null) { listener.Stop(); listener = null; }
            foreach (var peer in peers.Values) peer.Client.Close();
            peers.Clear();
            local = null;
        }
    }

    public void Dispose() { Close(); }
}
