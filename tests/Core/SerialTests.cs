using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

internal static class SerialTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception("FAIL serial: " + message); }
    internal static async Task<string> Run()
    {
        Assert(DataCodec.Encode("0xAA BB,0D0A", true, "UTF-8", "CRLF").SequenceEqual(new byte[] { 0xAA, 0xBB, 13, 10 }), "HEX exact bytes; no implicit suffix");
        Assert(Encoding.UTF8.GetString(DataCodec.Encode("中文", false, "UTF-8", "CRLF")) == "中文\r\n", "UTF-8 plus newline");
        foreach (string text in new[] { "A", "0xZZ", "0x", "" })
        { bool failed = false; try { DataCodec.Encode(text, true, "UTF-8", "无"); } catch (FormatException) { failed = true; } Assert(failed, "invalid HEX rejection"); }
        bool asciiFailed = false; try { DataCodec.Encode("中文", false, "ASCII", "无"); } catch (EncoderFallbackException) { asciiFailed = true; }
        Assert(asciiFailed, "unsupported ASCII characters are not silently replaced");
        byte[] chinese = Encoding.UTF8.GetBytes("中文");
        var records = new[] { new DataRecord { Kind = "RX", Bytes = chinese.Take(2).ToArray() }, new DataRecord { Kind = "TX", Bytes = Encoding.UTF8.GetBytes("ok") }, new DataRecord { Kind = "RX", Bytes = chinese.Skip(2).ToArray() } };
        string rendered = DataCodec.Render(records, false, "UTF-8", false, true);
        Assert(rendered.Contains("中文") && !rendered.Contains("�"), "UTF-8 decoder preserves fragmented receive bytes");
        Assert(DataCodec.Render(records, true, "UTF-8", false, true).Contains("E4 B8"), "HEX receive formatting");
        var history = new DataHistory(); for (int i = 0; i < 80; i++) history.Add(new DataRecord { Kind = "RX", Bytes = new byte[4096] });
        Assert(history.Trimmed && history.ByteCount <= DataHistory.Limit, "bounded log retention");
        var fake = new FakeSerialTransport(); var session = new SerialSession(s => fake); var echoed = new ConcurrentQueue<byte[]>();
        session.Received += bytes => echoed.Enqueue(bytes);
        await session.Open(new SerialSettings { PortName = "COM99" });
        await Task.WhenAll(session.Send(new byte[] { 0, 255 }), session.Send(new byte[] { 13, 10 }));
        Assert(fake.Writes == 2 && fake.PeakWrites == 1 && echoed.Count == 2, "serialized sends and receive echo");
        Assert(echoed.Any(bytes => bytes.SequenceEqual(new byte[] { 0, 255 })), "binary data preserved");
        fake.FailWrite = true; bool writeFailed = false;
        try { await session.Send(new byte[] { 1 }); } catch (IOException) { writeFailed = true; }
        Assert(writeFailed && !session.IsOpen && fake.Disposed, "write failure closes transport");
        var bad = new FakeSerialTransport { FailOpen = true }; var badSession = new SerialSession(s => bad); bool openFailed = false;
        try { await badSession.Open(new SerialSettings { PortName = "COM98" }); } catch (IOException) { openFailed = true; }
        Assert(openFailed && !badSession.IsOpen && bad.Disposed, "open failure releases transport");
        await session.Close(); await badSession.Close();
        return "PASS serial HEX/text encoding, line endings and invalid input\r\nPASS fragmented UTF-8 receive and bounded logs\r\nPASS simulated serial open/send/receive/close, serialized writes and failures\r\nNOTE physical serial hardware has not been exercised.\r\n";
    }
}
