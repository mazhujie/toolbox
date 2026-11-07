using System;
using System.IO.Ports;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal sealed class SerialSettings
{
    public string PortName;
    public int Baud = 115200, DataBits = 8;
    public Parity Parity = Parity.None;
    public StopBits StopBits = StopBits.One;
    public Handshake Handshake = Handshake.None;
    public bool Dtr, Rts;

    internal void Validate()
    {
        if (String.IsNullOrWhiteSpace(PortName) || !Regex.IsMatch(PortName, "^COM[1-9][0-9]*$", RegexOptions.IgnoreCase))
            throw new ArgumentException("请选择有效 COM 端口。");
        if (Baud < 1 || Baud > 4000000 || DataBits < 5 || DataBits > 8 || StopBits == StopBits.None)
            throw new ArgumentException("串口参数超出范围。");
    }
}
internal interface ISerialTransport : IDisposable
{
    event Action<byte[]> Received;
    event Action<string> Faulted;
    bool IsOpen { get; }
    void Open();
    void Write(byte[] bytes);
}
internal sealed class WindowsSerialTransport : ISerialTransport
{
    readonly SerialPort port;
    volatile bool closing;
    Task reader;
    public event Action<byte[]> Received;
    public event Action<string> Faulted;
    public bool IsOpen { get { return !closing && port.IsOpen; } }

    internal WindowsSerialTransport(SerialSettings s)
    {
        port = new SerialPort(s.PortName, s.Baud, s.Parity, s.DataBits, s.StopBits)
        {
            Handshake = s.Handshake, DtrEnable = s.Dtr,
            ReadTimeout = 200, WriteTimeout = 1000,
            ReadBufferSize = 65536, WriteBufferSize = 65536
        };
        if (s.Handshake != Handshake.RequestToSend && s.Handshake != Handshake.RequestToSendXOnXOff)
            port.RtsEnable = s.Rts;
    }

    public void Open()
    {
        port.Open();
        closing = false;
        reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    void ReadLoop()
    {
        var buffer = new byte[4096];
        while (!closing)
        {
            try
            {
                int n = port.Read(buffer, 0, buffer.Length);
                if (n <= 0) continue;
                var copy = new byte[n];
                Buffer.BlockCopy(buffer, 0, copy, 0, n);
                var handler = Received;
                if (handler != null) handler(copy);
            }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                var handler = Faulted;
                if (!closing && handler != null) handler(ex.Message);
                break;
            }
        }
    }

    public void Write(byte[] bytes) { port.Write(bytes, 0, bytes.Length); }

    public void Dispose()
    {
        closing = true;
        try { if (port.IsOpen) port.Close(); }
        finally
        {
            if (reader != null)
            {
                try { reader.Wait(1500); }
                catch (AggregateException) { }
            }
            port.Dispose();
        }
    }
}
internal sealed class SerialSession
{
    readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    readonly Func<SerialSettings, ISerialTransport> factory;
    volatile ISerialTransport current;
    internal event Action<byte[]> Received;
    internal event Action<string> Faulted;
    internal bool IsOpen { get { var port = current; return port != null && port.IsOpen; } }

    internal SerialSession(Func<SerialSettings, ISerialTransport> make = null)
    {
        factory = make ?? (s => new WindowsSerialTransport(s));
    }

    internal async Task Open(SerialSettings settings)
    {
        settings.Validate();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (current != null) throw new InvalidOperationException("请先关闭当前串口。");
            var candidate = factory(settings);
            current = candidate;
            candidate.Received += bytes =>
            {
                var handler = Received;
                if (ReferenceEquals(current, candidate) && handler != null) handler(bytes);
            };
            candidate.Faulted += error =>
            {
                var handler = Faulted;
                if (ReferenceEquals(current, candidate) && handler != null) handler(error);
            };
            // C# 5 cannot await cleanup inside a catch block.
            Exception failure = null;
            try { await Task.Run(() => candidate.Open()).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            if (failure != null)
            {
                current = null;
                await Task.Run(() => { try { candidate.Dispose(); } catch { } }).ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally { gate.Release(); }
    }

    async Task CloseLocked()
    {
        var old = current;
        current = null;
        if (old != null) await Task.Run(() => old.Dispose()).ConfigureAwait(false);
    }

    internal async Task Close()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await CloseLocked().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    internal async Task Send(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > 65536)
            throw new ArgumentException("单次发送应为 1–65,536 字节。");
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var port = current;
            if (port == null || !port.IsOpen) throw new InvalidOperationException("串口尚未打开。");
            Exception failure = null;
            try { await Task.Run(() => port.Write(bytes)).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            if (failure != null)
            {
                await CloseLocked().ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally { gate.Release(); }
    }
}
