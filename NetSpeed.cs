using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

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

internal sealed class SpeedForm : Form
{
    readonly ComboBox source = new ComboBox(), mode = new ComboBox();
    readonly TextBox url = new TextBox(), proxy = new TextBox(), log = new TextBox();
    readonly NumericUpDown seconds = new NumericUpDown(), connections = new NumericUpDown();
    readonly Label speed = new Label(), detail = new Label(), status = new Label();
    readonly Button start = new Button(), stop = new Button(), export = new Button();
    readonly ProgressBar bar = new ProgressBar();
    readonly IpPage ipPage;
    readonly LanPage lanPage;
    readonly ToolShell shell;
    readonly SerialWorkspace serialWorkspace;
    readonly SocketPage socketPage;
    CancellationTokenSource running;
    readonly List<string[]> history = new List<string[]>();
    long previousBytes; double previousTime;
    public SpeedForm() : this(null) { }
    internal SpeedForm(SerialSession serialConnection)
    {
        Text = "工具箱 3.0 · 网络服务 / 串口助手 / 网络调试"; ClientSize = new Size(1034, 760);
        MinimumSize = new Size(1050, 759); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.FromArgb(245, 247, 251);
        var title = new Label { Text = "网络测速", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), AutoSize = true, Location = new Point(24, 18) };
        Controls.Add(title);
        AddLabel("测速地址", 24, 82); source.SetBounds(120, 78, 650, 30); source.DropDownStyle = ComboBoxStyle.DropDownList;
        source.Items.AddRange(new object[] { "国内 · 阿里云开源镜像", "国内 · 华为云开源镜像", "国内 · 中科大测速网", "国内 · 南京大学测速网", "海外 · Cloudflare", "海外 · OVH 备用", "自定义下载地址" });
        Controls.Add(source); url.SetBounds(120, 117, 650, 30); Controls.Add(url);
        source.SelectedIndexChanged += delegate {
            string[] urls = { "https://mirrors.aliyun.com/ubuntu/ls-lR.gz", "https://mirrors.huaweicloud.com/ubuntu/ls-lR.gz", "http://test.ustc.edu.cn/backend/garbage.php?ckSize=16", "https://test.nju.edu.cn/backend/garbage.php?ckSize=16", "https://speed.cloudflare.com/__down?bytes=10000000", "https://proof.ovh.net/files/100Mb.dat" };
            if (source.SelectedIndex >= 0 && source.SelectedIndex < urls.Length) url.Text = urls[source.SelectedIndex];
        };
        source.SelectedIndex = 0;
        AddLabel("连接方式", 24, 166); mode.SetBounds(120, 162, 225, 30); mode.DropDownStyle = ComboBoxStyle.DropDownList;
        mode.Items.AddRange(new object[] { "直连（不使用 HTTP 代理）", "跟随 Windows 系统代理", "手动 HTTP / 混合端口" });
        Controls.Add(mode); proxy.SetBounds(360, 162, 410, 30); proxy.Text = "http://127.0.0.1:7890"; Controls.Add(proxy);
        mode.SelectedIndexChanged += delegate { proxy.Enabled = mode.SelectedIndex == 2; }; mode.SelectedIndex = 0;
        AddLabel("下载时长", 24, 212); seconds.SetBounds(120, 207, 90, 30); seconds.Minimum = 3; seconds.Maximum = 60; seconds.Value = 10; Controls.Add(seconds);
        AddLabel("秒", 218, 212); AddLabel("并发连接", 310, 212); connections.SetBounds(405, 207, 85, 30); connections.Minimum = 1; connections.Maximum = 8; connections.Value = 4; Controls.Add(connections);
        var note = new Label { Text = "手动模式使用 HTTP 或混合端口，不支持纯 SOCKS5。TUN/VPN 可能接管直连流量。\n测速会消耗流量；结果代表当前地址的下载能力，不包含上传测速。", AutoSize = false, ForeColor = Color.DimGray };
        note.SetBounds(24, 253, 752, 48); Controls.Add(note);
        speed.Text = "— Mbps"; speed.Font = new Font(Font.FontFamily, 36, FontStyle.Bold); speed.ForeColor = Color.FromArgb(35, 96, 207); speed.SetBounds(24, 308, 740, 75); Controls.Add(speed);
        detail.Text = "实时下载速度 · 1 MB/s = 8 Mbps"; detail.SetBounds(28, 389, 742, 27); Controls.Add(detail);
        bar.SetBounds(28, 425, 742, 8); bar.Maximum = 1000; Controls.Add(bar);
        start.Text = "开始测速"; start.SetBounds(28, 453, 150, 40); start.BackColor = Color.FromArgb(35, 96, 207); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat;
        stop.Text = "停止"; stop.SetBounds(192, 453, 100, 40); stop.Enabled = false;
        export.Text = "导出 CSV"; export.SetBounds(650, 453, 120, 40);
        Controls.AddRange(new Control[] { start, stop, export });
        status.Text = "就绪。可用同一地址分别测直连和代理，方便比较。"; status.SetBounds(28, 453, 742, 30); Controls.Add(status);
        log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.SetBounds(28, 492, 742, 164); Controls.Add(log);
        start.Click += async delegate { await StartTest(); }; stop.Click += delegate { if (running != null) running.Cancel(); };
        export.Click += delegate { Export(); };
        var speedPage = new Panel { BackColor = BackColor, AutoScroll = true };
        var oldControls = Controls.Cast<Control>().ToArray();
        foreach (var c in oldControls) speedPage.Controls.Add(c);
        shell = new ToolShell();
        ipPage = new IpPage(Busy);
        lanPage = new LanPage(Busy);
        serialWorkspace = new SerialWorkspace(serialConnection);
        socketPage = new SocketPage();
        shell.Add("speed", "network", "下载测速", speedPage, start, stop, export);
        shell.Add("ip", "network", "出口 IP / 风险", ipPage, ipPage.Actions);
        shell.Add("lan", "network", "局域网扫描", lanPage, lanPage.Actions);
        shell.Add("serial", "serial", "串口收发", serialWorkspace.Terminal, serialWorkspace.TerminalActions);
        shell.Add("socket", "debug", "TCP / UDP 收发", socketPage, socketPage.Actions);
        Controls.Add(shell); shell.ShowView("speed");
        bool closing = false;
        FormClosing += async delegate(object sender, FormClosingEventArgs e) {
            if (closing) return; closing = true; e.Cancel = true;
            if (running != null) running.Cancel(); ipPage.Cancel(); lanPage.Cancel();
            socketPage.Shutdown();
            await serialWorkspace.Shutdown(); serialWorkspace.Dispose();
            if (!IsDisposed) BeginInvoke(new Action(Close));
        };
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (serialWorkspace != null) serialWorkspace.Dispose();
            if (socketPage != null) socketPage.Shutdown();
        }
        base.Dispose(disposing);
    }
    void AddLabel(string text, int x, int y) { var l = new Label { Text = text, AutoSize = true, Location = new Point(x, y) }; Controls.Add(l); }
    void Ui(Action action) { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
    void Busy(bool yes)
    {
        foreach (Control c in new Control[] { source, mode, url, seconds, connections, start }) c.Enabled = !yes;
        proxy.Enabled = !yes && mode.SelectedIndex == 2; stop.Enabled = running != null;
        if (ipPage != null) ipPage.SetSpeedBusy(yes);
        if (lanPage != null) lanPage.SetExternalBusy(yes);
    }
    async Task StartTest()
    {
        Uri address;
        if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out address) || (address.Scheme != "http" && address.Scheme != "https") || address.UserInfo.Length > 0)
        { MessageBox.Show("请输入有效的 HTTP/HTTPS 文件地址，不要包含账号密码。"); return; }
        Uri p;
        if (mode.SelectedIndex == 2 && (!Uri.TryCreate(proxy.Text.Trim(), UriKind.Absolute, out p) || p.Scheme != "http" || p.UserInfo.Length > 0 || p.AbsolutePath != "/" || p.Query.Length > 0 || p.Fragment.Length > 0))
        { MessageBox.Show("代理格式：http://127.0.0.1:7890；请填写 HTTP 或混合端口。此版本不支持代理认证。"); return; }
        var o = new Options { Url = address.AbsoluteUri, Mode = mode.SelectedIndex, Proxy = proxy.Text.Trim(), Seconds = (int)seconds.Value, Connections = (int)connections.Value };
        var route = mode.Text; var chosenSource = source.Text;
        running = new CancellationTokenSource(); Busy(true); bar.Value = 0; previousBytes = 0; previousTime = 0; speed.Text = "准备中…";
        try
        {
            var r = await Engine.Run(o, running.Token, (bytes, elapsed) => Ui(() => {
                double instant = (bytes - previousBytes) * 8.0 / Math.Max(.001, elapsed - previousTime) / 1000000;
                previousBytes = bytes; previousTime = elapsed;
                speed.Text = instant.ToString("F2") + " Mbps";
                detail.Text = String.Format("{0:F2} MB/s  ·  已下载 {1:F1} MB  ·  {2:F1} / {3} 秒", instant / 8, bytes / 1000000.0, elapsed, o.Seconds);
                bar.Value = Math.Min(1000, (int)(elapsed / o.Seconds * 1000));
            }), text => Ui(() => status.Text = text));
            if (IsDisposed) return;
            speed.Text = r.Mbps.ToString("F2") + " Mbps";
            detail.Text = String.Format("平均 {0:F2} MB/s  ·  首字节 {1:F0} ms  ·  已下载 {2:F1} MB  ·  {3:F1} 秒", r.Mbps / 8, r.Latency, r.Bytes / 1000000.0, r.Seconds);
            status.Text = r.Stopped ? "已停止，显示停止前的平均值。" : "测速完成（平均值含连接开销）。";
            if (!r.Stopped) bar.Value = 1000;
            var row = new[] { DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), chosenSource, o.Url, route, o.Mode == 2 ? o.Proxy : "", o.Connections.ToString(), r.Seconds.ToString("F2", CultureInfo.InvariantCulture), r.Mbps.ToString("F3", CultureInfo.InvariantCulture), r.Latency.ToString("F1", CultureInfo.InvariantCulture), r.Bytes.ToString(), r.Stopped ? "停止" : "完成" };
            history.Add(row); log.AppendText(row[0] + "  " + route + "  " + r.Mbps.ToString("F2") + " Mbps  " + row[10] + Environment.NewLine);
        }
        catch (OperationCanceledException) { if (!IsDisposed) { status.Text = "已取消。"; speed.Text = "— Mbps"; } }
        catch (Exception ex) { if (!IsDisposed) { speed.Text = "测速失败"; status.Text = "请检查地址或代理，可切换测速源重试。"; log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + ex.GetBaseException().Message + Environment.NewLine); } }
        finally { var old = running; running = null; old.Dispose(); if (!IsDisposed) Busy(false); }
    }
    void Export()
    {
        if (history.Count == 0) { MessageBox.Show("请先完成一次测速。"); return; }
        using (var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "测速结果-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" })
        if (dialog.ShowDialog() == DialogResult.OK)
        try {
            var lines = new List<string> { "时间,测速源,地址,连接方式,代理,并发数,下载秒数,平均Mbps,首字节延迟ms,下载字节,状态" };
            lines.AddRange(history.Select(row => String.Join(",", row.Select(Csv))));
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
            status.Text = "测速记录已导出。";
        } catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message); }
    }
    static string Csv(string s) { if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
}

internal sealed class TestServer : IDisposable
{
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    public int ProxyRequests, StallRequests;
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
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.DefaultConnectionLimit = 32;
        if (args.Length > 0 && (args[0] == "--self-test" || args[0] == "--check-universities" || args[0] == "--check-ip" || args[0] == "--check-lan"))
        {
            bool universities = args[0] == "--check-universities";
            bool ip = args[0] == "--check-ip";
            bool lan = args[0] == "--check-lan";
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, universities ? "university-test.txt" : ip ? "ip-test.txt" : lan ? "lan-test.txt" : "self-test.txt");
            try {
                string report = (universities ? CheckUniversities() : ip ? IpTests.Live() : lan ? LanTests.Live() : SelfTest()).GetAwaiter().GetResult();
                File.WriteAllText(output, report, Encoding.UTF8);
                return report.Contains("FAIL ") ? 1 : 0;
            }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString(), Encoding.UTF8); return 1; }
        }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new SpeedForm())
        {
            if (args.Length > 0 && args[0] == "--smoke-test")
            {
                var timer = new System.Windows.Forms.Timer { Interval = 800 };
                timer.Tick += delegate { timer.Stop(); using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png")); } form.Close(); timer.Dispose(); };
                timer.Start();
            }
            Application.Run(form);
        }
        return 0;
    }
    static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
    static async Task<string> CheckUniversities()
    {
        var report = new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
        foreach (string host in new[] { "test.ustc.edu.cn", "test.nju.edu.cn" })
        {
            using (var limit = new CancellationTokenSource(60000))
            {
                var o = new Options { Url = (host == "test.ustc.edu.cn" ? "http://" : "https://") + host + "/backend/garbage.php?ckSize=16", Mode = 0, Seconds = 2, Connections = 1 };
                try {
                    var r = await Engine.Run(o, limit.Token, (b, t) => { }, s => { });
                    Assert(r.Bytes > 0 && !r.Stopped, host + " live download");
                    report.AppendLine("PASS " + host + " native verification + latency + 2s download; bytes=" + r.Bytes);
                } catch (Exception ex) { report.AppendLine("FAIL " + host + ": " + ex.Message); }
            }
        }
        return report.ToString();
    }
    static async Task<string> SelfTest()
    {
        var report = new StringBuilder();
        using (var server = new TestServer())
        {
            var o = new Options { Url = server.Url, Mode = 0, Seconds = 1, Connections = 3 };
            var r = await Engine.Run(o, CancellationToken.None, (b, t) => { }, s => { });
            Assert(r.Bytes > 0 && r.Seconds >= .8 && r.Seconds < 5 && r.Latency > 0 && !r.Stopped && r.Mbps > 0, "direct streaming / deadline / metrics");
            report.AppendLine("PASS direct streaming, parallel download, deadline, latency, Mbps");
            o.Mode = 2; o.Proxy = server.Url.Replace("/data", ""); o.Url = "http://proxy-test.invalid/data";
            r = await Engine.Run(o, CancellationToken.None, (b, t) => { }, s => { });
            Assert(server.ProxyRequests >= 6 && r.Bytes > 0, "explicit proxy routing"); report.AppendLine("PASS explicit HTTP proxy routing (unresolvable origin)");
            o.Mode = 0; o.Url = server.Url;
            using (var cancel = new CancellationTokenSource()) {
                cancel.CancelAfter(500); o.Seconds = 10;
                r = await Engine.Run(o, cancel.Token, (b, t) => { }, s => { });
                Assert(r.Stopped && r.Seconds < 3, "user cancellation");
            }
            report.AppendLine("PASS cancellation during streaming");
            o.Url = server.Url.Replace("/data", "/stall");
            using (var cancel = new CancellationTokenSource()) {
                cancel.CancelAfter(500);
                r = await Engine.Run(o, cancel.Token, (b, t) => { }, s => { });
                Assert(r.Stopped && r.Seconds < 2, "cancellation on stalled response body");
            }
            report.AppendLine("PASS cancellation on stalled response body");
            using (var cancel = new CancellationTokenSource()) {
                cancel.Cancel(); bool stopped = false;
                try { await Engine.Run(o, cancel.Token, (b, t) => { }, s => { }); } catch (OperationCanceledException) { stopped = true; }
                Assert(stopped, "cancel during probe");
            }
            report.AppendLine("PASS cancellation during latency probe");
            foreach (var path in new[] { "/error", "/html" }) {
                o.Url = server.Url.Replace("/data", path); bool failed = false;
                try { await Engine.Run(o, CancellationToken.None, (b, t) => { }, s => { }); } catch (Exception) { failed = true; }
                Assert(failed, path); report.AppendLine("PASS reject " + path);
            }
        }
        report.Append(await IpTests.Run());
        report.Append(await LanTests.Run());
        report.Append(await SerialTests.Run());
        report.Append(await SocketTests.Run());
        return report.ToString();
    }
}
