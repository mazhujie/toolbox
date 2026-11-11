using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal sealed class AdbText
{
    internal const int Limit = 262144;
    readonly object sync = new object();
    readonly StringBuilder text = new StringBuilder();
    bool trimmed, changed;
    internal void Append(string value)
    {
        lock (sync)
        {
            text.Append(value);
            if (text.Length > Limit) { text.Remove(0, text.Length - Limit); trimmed = true; }
            changed = true;
        }
    }
    internal void Clear() { lock (sync) { text.Clear(); trimmed = false; changed = true; } }
    internal bool Trimmed { get { lock (sync) return trimmed; } }
    internal string Read(bool onlyChanged)
    {
        lock (sync)
        {
            if (onlyChanged && !changed) return null;
            if (onlyChanged) changed = false;
            return (trimmed ? "[较早输出已裁剪]\r\n" : "") + text;
        }
    }
}

internal sealed class AdbDevice
{
    internal string Serial, State, Model, Transport;
    internal bool Ready { get { return State == "device"; } }
    internal bool Network { get { return Serial.Contains(":") || Serial.Contains("._tcp"); } }
    internal string[] Target { get { return String.IsNullOrEmpty(Transport) ? new[] { "-s", Serial } : new[] { "-t", Transport }; } }
    public override string ToString()
    {
        string state = Ready ? "在线" : State == "unauthorized" ? "未授权，请在设备上确认" : State == "offline" ? "离线" : State;
        return Serial + (String.IsNullOrEmpty(Model) ? "" : " · " + Model.Replace('_', ' ')) + "  [" + state + "]";
    }
}

internal sealed class AdbResult
{
    internal int ExitCode;
    internal string Output, Error;
    internal bool Truncated;
    internal void EnsureSuccess()
    {
        if (ExitCode != 0) throw new IOException("ADB 退出码 " + ExitCode + "。" + (String.IsNullOrWhiteSpace(Error) ? "请查看输出。" : Error.Trim()));
    }
}

internal static class AdbClient
{
    internal static string Quote(string value)
    {
        if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("参数不能包含空字符。");
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static string Arguments(IEnumerable<string> args)
    {
        string result = String.Join(" ", args.Select(Quote));
        if (result.Length > 28000) throw new ArgumentException("命令过长，请缩短后再执行。");
        return result;
    }

    // Invoke the executable directly: local paths and remote shell commands must never pass through cmd.exe.
    internal static Task<AdbResult> Run(string executable, string[] args, CancellationToken token, Action<string, bool> output, bool killTree = false, Action<ProcessStartInfo> configure = null, bool keepInputOpen = false)
    {
        string arguments = Arguments(args);
        return Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            var stdout = new AdbText(); var stderr = new AdbText();
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(executable, arguments) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                    WorkingDirectory = Path.GetDirectoryName(executable)
                };
                if (configure != null) configure(process.StartInfo);
                process.Start();
                using (token.Register(() => { if (keepInputOpen) CloseInput(process); else Kill(process, killTree); }))
                {
                    if (!keepInputOpen) process.StandardInput.Close();
                    var readers = Task.WhenAll(Read(process.StandardOutput, stdout, false, output), Read(process.StandardError, stderr, true, output));
                    process.WaitForExit();
                    await readers.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return new AdbResult { ExitCode = process.ExitCode, Output = stdout.Read(false), Error = stderr.Read(false), Truncated = stdout.Trimmed || stderr.Trimmed };
                }
            }
        });
    }

    static void CloseInput(Process process)
    {
        try { process.StandardInput.Close(); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        // WSL's supervisor receives EOF and stops only this command's Linux process group.
        Task.Run(() => {
            try { if (!process.WaitForExit(5000)) Kill(process, false); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
    }

    static async Task Read(StreamReader reader, AdbText capture, bool error, Action<string, bool> output)
    {
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            string text = new string(buffer, 0, count);
            capture.Append(text);
            if (output != null) output(text, error);
        }
    }

    static void Kill(Process process, bool tree)
    {
        try
        {
            if (process.HasExited) return;
            if (tree)
            {
                using (var killer = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"), "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true })) killer.WaitForExit();
            }
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal static AdbDevice[] ParseDevices(string output)
    {
        var result = new List<AdbDevice>();
        bool header = false;
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("List of devices attached")) { header = true; continue; }
            if (!header || line.StartsWith("*")) continue;
            string[] parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length < 2) continue;
            result.Add(new AdbDevice { Serial = parts[0], State = parts[1],
                Model = parts.FirstOrDefault(p => p.StartsWith("model:")) == null ? "" : parts.First(p => p.StartsWith("model:")).Substring(6),
                Transport = parts.Where(p => p.StartsWith("transport_id:")).Select(p => p.Substring(13)).FirstOrDefault(p => Regex.IsMatch(p, @"^[0-9]+$")) });
        }
        if (!header) throw new IOException("ADB 未返回有效的设备列表，请检查所选程序。" );
        return result.ToArray();
    }

    internal static string RemotePath(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || !value.StartsWith("/") || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
            throw new ArgumentException("设备路径需为以 / 开头的完整路径，不支持换行。" );
        return value;
    }

    internal static string[] Push(AdbDevice device, string local, string remote)
    {
        local = LocalPath(local);
        if (!File.Exists(local) && !Directory.Exists(local)) throw new IOException("本地文件或目录不存在。");
        return device.Target.Concat(new[] { "push", local, RemotePath(remote) }).ToArray();
    }

    internal static string[] Pull(AdbDevice device, string remote, string local)
    {
        local = LocalPath(local);
        if (!Directory.Exists(local)) throw new IOException("请选择已存在的本地保存目录。");
        return device.Target.Concat(new[] { "pull", RemotePath(remote), local }).ToArray();
    }

    static string LocalPath(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value)) throw new ArgumentException("请选择本地文件或目录的完整路径。");
        return Path.GetFullPath(value);
    }

    internal static string NetworkAddress(string value)
    {
        value = value.Trim();
        Uri uri;
        if (!Uri.TryCreate("tcp://" + value, UriKind.Absolute, out uri) || uri.Port < 1 || uri.Port > 65535 ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            value.IndexOfAny(new[] { '/', '\\', ' ', '\t', '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("网络地址格式：主机或 IP:端口，例如 192.168.1.20:5555。");
        return value;
    }
}

internal static class AdbLocator
{
    static string Settings { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toolbox", "adb-path.txt"); } }
    internal static string Find()
    {
        var candidates = new List<string>();
        try { if (File.Exists(Settings)) candidates.Add(File.ReadAllText(Settings).Trim()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        string root = AppDomain.CurrentDomain.BaseDirectory;
        candidates.Add(Path.Combine(root, "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(root, "adb.exe"));
        foreach (string variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            string sdk = Environment.GetEnvironmentVariable(variable);
            if (!String.IsNullOrEmpty(sdk)) candidates.Add(Path.Combine(sdk, "platform-tools", "adb.exe"));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"));
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { if (!String.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory.Trim().Trim('"'), "adb.exe")); }
            catch (ArgumentException) { }
        }
        foreach (string candidate in candidates)
        {
            try { if (File.Exists(candidate)) return Validate(candidate); }
            catch (Exception ex) { if (!(ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)) throw; }
        }
        return "";
    }

    internal static string Validate(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) throw new IOException("尚未配置 ADB。请下载并解压 Platform Tools，选择其中的 adb.exe。");
        path = Path.GetFullPath(path.Trim().Trim('"'));
        if (!File.Exists(path) || !String.Equals(Path.GetFileName(path), "adb.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择 Android Platform Tools 中的 adb.exe，并保留其同目录 DLL。");
        // Scoop shims spawn another process. Resolve their target so cancellation stops the actual ADB client.
        string shim = Path.ChangeExtension(path, ".shim");
        if (File.Exists(shim))
        {
            var match = Regex.Match(File.ReadAllText(shim), "(?m)^path = \"([^\"]+)\"\\s*$");
            if (match.Success && File.Exists(match.Groups[1].Value) && String.Equals(Path.GetFileName(match.Groups[1].Value), "adb.exe", StringComparison.OrdinalIgnoreCase))
                path = Path.GetFullPath(match.Groups[1].Value);
        }
        return path;
    }

    internal static void Remember(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings));
        File.WriteAllText(Settings, path, new UTF8Encoding(false));
    }
}
