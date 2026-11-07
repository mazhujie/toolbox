using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

internal sealed class FakeSerialTransport : ISerialTransport
{
    public event Action<byte[]> Received; public event Action<string> Faulted;
    public bool IsOpen { get; private set; }
    internal bool Disposed, FailOpen, FailWrite; internal int Writes, ActiveWrites, PeakWrites;
    internal int FailAtWrite { get; set; }
    internal readonly ConcurrentQueue<byte[]> Sent = new ConcurrentQueue<byte[]>();
    public void Open() { if (FailOpen) throw new IOException("fixture open failure"); IsOpen = true; }
    public void Write(byte[] data)
    {
        if (!IsOpen || FailWrite || (FailAtWrite > 0 && Writes + 1 == FailAtWrite)) throw new IOException("fixture write failure");
        int active = Interlocked.Increment(ref ActiveWrites); PeakWrites = Math.Max(PeakWrites, active);
        Thread.Sleep(8); Writes++; Sent.Enqueue((byte[])data.Clone());
        Receive(data);
        Interlocked.Decrement(ref ActiveWrites);
    }
    internal void Receive(byte[] data)
    {
        var handler = Received;
        if (handler != null) handler((byte[])data.Clone());
    }
    public void Dispose() { IsOpen = false; Disposed = true; }
    internal void Fault() { var handler = Faulted; if (handler != null) handler("fixture disconnect"); }
}
