using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal sealed class TestServer : IDisposable
{
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    public int ProxyRequests, StallRequests, LimitedRequests;
    public string Url;
    public TestServer()
    {
        listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/data";
        Task.Run(async () => {
            while (true) {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch { break; }
                Task.Run(() => Serve(client));
            }
        });
    }
    void Serve(TcpClient client)
    {
        using (client)
        try {
            client.ReceiveTimeout = 3000; client.SendTimeout = 3000;
            var stream = client.GetStream(); var header = new StringBuilder(); int ch;
            while ((ch = stream.ReadByte()) >= 0 && header.Length < 16000) { header.Append((char)ch); if (header.ToString().EndsWith("\r\n\r\n")) break; }
            string request = header.ToString();
            if (request.StartsWith("GET http://")) Interlocked.Increment(ref ProxyRequests);
            if (request.Contains("/scam"))
            {
                var match = Regex.Match(request, "/scam(?:-[a-z]+)?/([^ ?\\r\\n]+)");
                string ip = Uri.UnescapeDataString(match.Groups[1].Value);
                bool limited = request.Contains("/scam-error/");
                string pageBody = request.Contains("/scam-bad/") ? IpTests.Page("192.0.2.1") : IpTests.Page(ip);
                byte[] body = Encoding.UTF8.GetBytes(pageBody);
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + (limited ? "429 Too Many Requests" : "200 OK") + "\r\nContent-Type: text/html\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length); return;
            }
            if (request.Contains("/ip-"))
            {
                string ip = request.StartsWith("GET http://") ? "203.0.113.9" : "203.0.113.8";
                bool limited = request.Contains("/ip-error"), stall = request.Contains("/ip-stall");
                string json = limited ? "{\"error\":\"rate limited\"}" : "{\"ip\":\"" + ip + "\"" + (request.Contains("/ip-good") ? ",\"risk\":{\"risk_score\":55,\"is_proxy\":true}" : "") + "}";
                byte[] body = Encoding.UTF8.GetBytes(json);
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + (limited ? "429 Too Many Requests" : "200 OK") + "\r\nContent-Type: application/json\r\nContent-Length: " + (stall ? 512 : body.Length) + "\r\nConnection: close\r\n\r\n");
                stream.Write(head, 0, head.Length);
                if (stall) { Thread.Sleep(4000); return; }
                stream.Write(body, 0, body.Length); return;
            }
            if (request.Contains("/rate-limit"))
            {
                int count = Interlocked.Increment(ref LimitedRequests);
                // Three latency probes and one full download succeed before the delayed limit.
                if (!request.Contains("/rate-limit-after-data") || count > 4)
                {
                    byte[] limited = Encoding.ASCII.GetBytes("HTTP/1.1 429 Too Many Requests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    stream.Write(limited, 0, limited.Length); return;
                }
            }
            bool error = request.Contains("/error"), html = request.Contains("/html");
            byte[] h = Encoding.ASCII.GetBytes("HTTP/1.1 " + (error ? "503 Unavailable" : "200 OK") + "\r\nContent-Type: " + (html ? "text/html" : "application/octet-stream") + "\r\nContent-Length: 1048576\r\nConnection: close\r\n\r\n");
            stream.Write(h, 0, h.Length);
            if (request.Contains("/stall") && Interlocked.Increment(ref StallRequests) > 3) { Thread.Sleep(4000); return; }
            byte[] b = new byte[16384];
            for (int i = 0; i < 64; i++) { stream.Write(b, 0, b.Length); Thread.Sleep(2); }
        } catch (IOException) { } catch (SocketException) { }
    }
    public void Dispose() { listener.Stop(); }
}
