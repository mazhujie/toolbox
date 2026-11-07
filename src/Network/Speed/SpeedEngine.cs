using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class Options
{
    public string Url, Proxy;
    public int Mode, Seconds, Connections;
}
internal sealed class Result
{
    public long Bytes;
    public double Seconds, Latency;
    public bool Stopped;
    public double Mbps { get { return Bytes * 8.0 / Math.Max(Seconds, .001) / 1000000; } }
}
internal static class Engine
{
    public static HttpClient Client(Options o, CookieContainer cookies = null, bool redirects = true)
    {
        var h = new HttpClientHandler { UseProxy = o.Mode != 0, AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = redirects, CookieContainer = cookies ?? new CookieContainer() };
        if (o.Mode == 2) h.Proxy = new WebProxy(o.Proxy, false);
        var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NetSpeed/1.3");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
        return c;
    }
    static HttpRequestMessage Request(string url)
    {
        var uri = new Uri(url);
        if ((uri.Host == "test.ustc.edu.cn" || uri.Host == "test.nju.edu.cn") && uri.AbsolutePath == "/backend/garbage.php")
            url += (uri.Query.Length > 0 ? "&" : "?") + "r=" + Guid.NewGuid().ToString("N");
        return new HttpRequestMessage(HttpMethod.Get, url);
    }
    static async Task<string> SmallBody(HttpResponseMessage response, CancellationToken token)
    {
        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (token.Register(() => stream.Dispose()))
        using (var body = new MemoryStream())
        {
            var buffer = new byte[4096]; int n;
            while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + n > 131072) throw new IOException("测速站验证响应过大，请稍后重试。");
                body.Write(buffer, 0, n);
            }
            return Encoding.UTF8.GetString(body.ToArray());
        }
    }
    static async Task PrepareUniversity(Options o, HttpClient client, CookieContainer cookies, CancellationToken user, Action<string> phase)
    {
        var target = new Uri(o.Url);
        if (target.Host != "test.ustc.edu.cn" && target.Host != "test.nju.edu.cn") return;
        var origin = new Uri(target.GetLeftPart(UriPartial.Authority) + "/");
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(user))
        {
            limit.CancelAfter(45000);
            try
            {
                if (target.Host == "test.ustc.edu.cn")
                {
                    phase("正在准备中科大测速：建立会话并完成站点验证…");
                    // The homepage supplies the station's session cookie; PoW alone is insufficient.
                    using (var home = await client.GetAsync(origin, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                    { home.EnsureSuccessStatusCode(); await SmallBody(home, limit.Token).ConfigureAwait(false); }
                    var json = new JavaScriptSerializer(); Dictionary<string, object> puzzle;
                    using (var response = await client.GetAsync(new Uri(origin, "backend/pow.php?r=" + Guid.NewGuid().ToString("N")), HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                    { response.EnsureSuccessStatusCode(); puzzle = json.Deserialize<Dictionary<string, object>>(await SmallBody(response, limit.Token).ConfigureAwait(false)); }
                    string challenge = Convert.ToString(puzzle["challenge"]), ticket = Convert.ToString(puzzle["token"]);
                    int difficulty = Convert.ToInt32(puzzle["difficulty"]);
                    if (difficulty < 1 || difficulty > 24 || challenge.Length > 256 || ticket.Length > 4096)
                        throw new IOException("中科大测速验证方式发生变化，请稍后重试。");
                    string nonce = await Task.Run(() => SolvePow(challenge, difficulty, limit.Token), limit.Token).ConfigureAwait(false);
                    using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "backend/pow_verify.php")))
                    {
                        request.Headers.Referrer = origin;
                        request.Content = new StringContent(json.Serialize(new { token = ticket, nonce = nonce }), Encoding.UTF8, "application/json");
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            var verification = json.Deserialize<Dictionary<string, object>>(await SmallBody(response, limit.Token).ConfigureAwait(false));
                            if (!verification.ContainsKey("success") || !Object.Equals(verification["success"], true))
                                throw new IOException("中科大测速验证未通过，请稍后重试。");
                        }
                    }
                }
                else
                {
                    phase("正在准备南京大学测速：等待站点验证跳转…");
                    // Follow server-issued redirects with its cookies, Referer and requested delay.
                    using (var session = Client(o, cookies, false))
                    {
                        Uri next = origin, previous = null; bool ready = false;
                        for (int attempt = 0; attempt < 10; attempt++)
                        using (var request = new HttpRequestMessage(HttpMethod.Get, next))
                        {
                            if (previous != null) request.Headers.Referrer = previous;
                            using (var response = await session.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                            {
                                Uri destination = response.Headers.Location;
                                if ((int)response.StatusCode < 300 || (int)response.StatusCode >= 400) destination = null;
                                if (destination == null)
                                {
                                    response.EnsureSuccessStatusCode();
                                    string body = await SmallBody(response, limit.Token).ConfigureAwait(false);
                                    IEnumerable<string> values; string refresh = null;
                                    if (response.Headers.TryGetValues("Refresh", out values)) refresh = values.FirstOrDefault();
                                    if (refresh == null)
                                    {
                                        var meta = Regex.Match(body, "<meta\\s+http-equiv=[\"']refresh[\"']\\s+content=[\"']([^\"']+)", RegexOptions.IgnoreCase);
                                        if (meta.Success) refresh = WebUtility.HtmlDecode(meta.Groups[1].Value);
                                    }
                                    if (refresh == null)
                                    {
                                        if (body.IndexOf("anubis_challenge", StringComparison.OrdinalIgnoreCase) >= 0)
                                            throw new IOException("南京大学当前要求浏览器验证，暂时无法在程序内测速。请稍后重试或选择其他源。");
                                        ready = true; break;
                                    }
                                    var match = Regex.Match(refresh, "^\\s*(\\d+(?:\\.\\d+)?)\\s*;\\s*url=(.+)$", RegexOptions.IgnoreCase);
                                    double delay;
                                    if (!match.Success || !Double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out delay) || delay > 10)
                                        throw new IOException("南京大学测速验证跳转格式已变化。");
                                    await Task.Delay(TimeSpan.FromSeconds(delay), limit.Token).ConfigureAwait(false);
                                    destination = new Uri(match.Groups[2].Value.Trim().Trim('"', '\''), UriKind.RelativeOrAbsolute);
                                }
                                destination = new Uri(next, destination);
                                if (destination.Host != origin.Host || destination.Scheme != origin.Scheme || destination.Port != origin.Port || destination.UserInfo.Length > 0)
                                    throw new IOException("南京大学测速验证返回了不同站点的跳转。");
                                previous = next; next = destination;
                            }
                        }
                        if (!ready) throw new IOException("南京大学测速验证未完成，请稍后重试。");
                    }
                }
            }
            catch (Exception)
            {
                if (user.IsCancellationRequested) throw new OperationCanceledException(user);
                if (limit.IsCancellationRequested) throw new IOException("测速站验证超时，请稍后重试或换一个测速源。");
                throw;
            }
        }
    }
    static string SolvePow(string challenge, int difficulty, CancellationToken token)
    {
        int zeroNibbles = (difficulty + 3) / 4;
        using (var sha = SHA256.Create())
        for (int nonce = 0; nonce < 100000000; nonce++)
        {
            if ((nonce & 1023) == 0) token.ThrowIfCancellationRequested();
            string text = nonce.ToString(CultureInfo.InvariantCulture);
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(challenge + text));
            bool valid = true;
            for (int i = 0; i < zeroNibbles; i++)
                if (((i % 2 == 0 ? hash[i / 2] >> 4 : hash[i / 2]) & 15) != 0) { valid = false; break; }
            if (valid) return text;
        }
        throw new IOException("中科大测速计算验证未完成，请稍后重试。");
    }
    static void Check(HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        var t = r.Content.Headers.ContentType;
        if (t != null && (t.MediaType == "text/html" || t.MediaType == "application/json"))
            throw new IOException("测速地址返回网页或 JSON，请换成直接下载文件的 URL。");
    }
    public static async Task<Result> Run(Options o, CancellationToken user, Action<long, double> progress, Action<string> phase)
    {
        var result = new Result();
        var cookies = new CookieContainer();
        using (var client = Client(o, cookies))
        {
            await PrepareUniversity(o, client, cookies, user, phase).ConfigureAwait(false);
            phase("正在测量首字节延迟（3 次）…");
            var times = new List<double>();
            for (int i = 0; i < 3; i++)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(user))
            using (var request = Request(o.Url))
            {
                timeout.CancelAfter(10000);
                var sw = Stopwatch.StartNew();
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        Check(response);
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (timeout.Token.Register(() => stream.Dispose()))
                        {
                            if (await stream.ReadAsync(new byte[1], 0, 1, timeout.Token).ConfigureAwait(false) == 0)
                                throw new IOException("测速文件为空。");
                            times.Add(sw.Elapsed.TotalMilliseconds);
                        }
                    }
                }
                catch (Exception) {
                    if (user.IsCancellationRequested) throw new OperationCanceledException(user);
                    if (timeout.IsCancellationRequested) throw new IOException("连接超时：请检查地址、网络或代理端口。");
                    throw;
                }
            }
            result.Latency = times.Average();
            phase("正在下载测速… 首字节延迟 " + result.Latency.ToString("F0") + " ms");
            long bytes = 0;
            Exception failure = null;
            var clock = Stopwatch.StartNew();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(user))
            {
                deadline.CancelAfter(o.Seconds * 1000);
                var jobs = Enumerable.Range(0, o.Connections).Select(async index =>
                {
                    var buffer = new byte[65536];
                    try
                    {
                        while (!deadline.IsCancellationRequested)
                        using (var request = Request(o.Url))
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                        {
                            Check(response);
                            long received = 0;
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (deadline.Token.Register(() => stream.Dispose()))
                            {
                                int n;
                                while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) > 0)
                                { Interlocked.Add(ref bytes, n); received += n; }
                            }
                            if (received == 0) throw new IOException("测速文件为空。");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!deadline.IsCancellationRequested)
                        { Interlocked.CompareExchange(ref failure, ex, null); deadline.Cancel(); }
                    }
                }).ToArray();
                var sample = Task.Run(async () =>
                {
                    while (!deadline.IsCancellationRequested)
                    {
                        progress(Interlocked.Read(ref bytes), clock.Elapsed.TotalSeconds);
                        try { await Task.Delay(250, deadline.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    }
                });
                await Task.WhenAll(jobs).ConfigureAwait(false);
                deadline.Cancel();
                await sample.ConfigureAwait(false);
            }
            clock.Stop();
            if (failure != null) throw new IOException("下载失败：" + failure.GetBaseException().Message, failure);
            result.Bytes = Interlocked.Read(ref bytes);
            result.Seconds = clock.Elapsed.TotalSeconds;
            result.Stopped = user.IsCancellationRequested;
            if (result.Bytes == 0 && !result.Stopped) throw new IOException("没有收到测速数据。");
            return result;
        }
    }
}
