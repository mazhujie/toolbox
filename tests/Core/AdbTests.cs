using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class AdbTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception("FAIL ADB: " + message); }
    internal static async Task<string> Run(string executable)
    {
        var report = new StringBuilder();
        string[] args = { "", "中文 文件.txt", "a\"b", @"C:\a b\", "x&echo injected|%PATH%", "line1\nline2", @"a\\\" + '"' + "z" };
        var result = await AdbClient.Run(executable, new[] { "fixture-args" }.Concat(args).ToArray(), CancellationToken.None, null);
        result.EnsureSuccess();
        string[] actual = result.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        Assert(args.Select(s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))).SequenceEqual(actual.Take(args.Length)), "Windows argv preserves Unicode, quotes, trailing slashes and shell metacharacters");
        var devices = AdbClient.ParseDevices("* daemon started *\nList of devices attached\nUSB123 device product:demo model:Test_Model transport_id:17\nLOCKED unauthorized\nOFF offline\n");
        Assert(devices.Length == 3 && devices[0].Ready && devices[0].Target.SequenceEqual(new[] { "-t", "17" }) && !devices[1].Ready && !devices[2].Ready, "device states and explicit transport target");
        Assert(devices[1].Target.SequenceEqual(new[] { "-s", "LOCKED" }), "serial fallback when transport ID absent");
        Assert(AdbClient.NetworkAddress("192.168.1.20:5555") == "192.168.1.20:5555", "network address");
        foreach (string value in new[] { "", "host", "http://host:5555", "host:5555/path", "-bad:0" })
        { bool rejected = false; try { AdbClient.NetworkAddress(value); } catch (ArgumentException) { rejected = true; } Assert(rejected, "reject invalid endpoint " + value); }
        foreach (string value in new[] { "", "relative", "-rf", "/sdcard/a\nb" })
        { bool rejected = false; try { AdbClient.RemotePath(value); } catch (ArgumentException) { rejected = true; } Assert(rejected, "reject invalid remote path"); }
        report.AppendLine("PASS ADB real subprocess argv quoting, device parsing, transport selection and path validation");

        var device = new AdbDevice { Serial = "DEMO-USB", State = "device", Transport = "1" };
        string root = Path.GetDirectoryName(executable);
        string source = Path.Combine(root, "local & 中文"), destination = Path.Combine(root, "pull result");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        string file = Path.Combine(source, "a & 中文.bin"); byte[] bytes = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray(); File.WriteAllBytes(file, bytes);
        result = await AdbClient.Run(executable, AdbClient.Push(device, file, "/sdcard/Download/"), CancellationToken.None, null); result.EnsureSuccess();
        result = await AdbClient.Run(executable, AdbClient.Pull(device, "/sdcard/Download/" + Path.GetFileName(file), destination), CancellationToken.None, null); result.EnsureSuccess();
        Assert(File.ReadAllBytes(Path.Combine(destination, Path.GetFileName(file))).SequenceEqual(bytes), "binary push/pull with spaces and Chinese paths");
        Directory.CreateDirectory(Path.Combine(source, "sub")); File.WriteAllText(Path.Combine(source, "sub", "nested.txt"), "nested");
        result = await AdbClient.Run(executable, AdbClient.Push(device, source, "/sdcard/tree"), CancellationToken.None, null); result.EnsureSuccess();
        result = await AdbClient.Run(executable, AdbClient.Pull(device, "/sdcard/tree", destination), CancellationToken.None, null); result.EnsureSuccess();
        Assert(File.ReadAllText(Path.Combine(destination, "tree", "sub", "nested.txt")) == "nested", "recursive folder round trip");
        bool empty = false; try { AdbClient.Push(device, "", "/sdcard/"); } catch (ArgumentException) { empty = true; }
        Assert(empty, "blank local path cannot upload current working directory");
        report.AppendLine("PASS ADB simulated-device binary/file/directory push-pull and invalid local source rejection");

        int characters = 0;
        result = await AdbClient.Run(executable, device.Target.Concat(new[] { "shell", "-T", "-n", "fixture-flood" }).ToArray(), CancellationToken.None, (text, error) => Interlocked.Add(ref characters, text.Length));
        Assert(result.ExitCode == 0 && result.Truncated && characters == 4096 * 200 * 2 && result.Output.Length < AdbText.Limit + 100, "stdout/stderr drained concurrently with bounded capture");
        result = await AdbClient.Run(executable, device.Target.Concat(new[] { "shell", "-T", "-n", "fixture-error" }).ToArray(), CancellationToken.None, null);
        Assert(result.ExitCode == 7 && result.Error.Contains("Permission denied"), "nonzero exit and stderr preserved");
        report.AppendLine("PASS ADB concurrent stdout/stderr, bounded logs and nonzero exit reporting");

        using (var cancel = new CancellationTokenSource())
        {
            var ready = new TaskCompletionSource<bool>();
            var task = AdbClient.Run(executable, device.Target.Concat(new[] { "shell", "-T", "-n", "fixture-hang" }).ToArray(), cancel.Token, (text, error) => { if (text.Contains("READY")) ready.TrySetResult(true); });
            Assert(await Task.WhenAny(ready.Task, Task.Delay(4000)) == ready.Task, "output arrives before command exits");
            int pid = Int32.Parse(File.ReadAllText(Path.Combine(root, "pid.txt")));
            cancel.Cancel();
            Assert(await Task.WhenAny(task, Task.Delay(4000)) == task, "cancel completes promptly");
            bool cancelled = false; try { await task; } catch (OperationCanceledException) { cancelled = true; }
            bool exited; try { using (var process = Process.GetProcessById(pid)) exited = process.HasExited; } catch (ArgumentException) { exited = true; }
            Assert(cancelled && exited, "cancel kills only the started client process");
        }
        report.AppendLine("PASS ADB live output, cancellation and client-process cleanup (fixture executable)");
        var listing = await AdbFiles.Read(executable, device, "/sdcard/Download/quote' $name.txt", CancellationToken.None);
        Assert(listing.Path == "/sdcard/Download" && listing.Entries.First().Directory && listing.Entries.Any(e => e.Path == "/sdcard/Download/quote' $name.txt" && !e.Directory), "directory and file types, UTF-8 and quoted paths");
        Assert(listing.Entries.Single(e => e.Name == "broken-link").Selectable == false && listing.Entries.Single(e => e.Name == "socket").Selectable == false, "invalid links and special files cannot be selected");
        Assert(AdbFiles.Parent("/") == "/" && AdbFiles.Parent("/sdcard/Download/") == "/sdcard", "root and parent path handling");
        Assert((await AdbFiles.Read(executable, device, "/empty", CancellationToken.None)).Entries.Length == 0, "empty directory remains selectable");
        bool denied = false; try { await AdbFiles.Read(executable, device, "/denied", CancellationToken.None); } catch (IOException ex) { denied = ex.Message.Contains("Permission denied"); }
        Assert(denied, "permission failure is not an empty listing");
        foreach (string bad in new[] { "", "TB_ADB_DIR_V1\0/\0D\0x\0", "TB_ADB_DIR_V1\0/\0D\0../escape\0E\x0000" + "0\0", "TB_ADB_DIR_V1\0/\0D\0same\0F\0same\0E\x0000" + "0\0" })
        { bool rejected = false; try { AdbFiles.Parse(bad); } catch (IOException) { rejected = true; } Assert(rejected, "reject malformed, incomplete or ambiguous directory listings"); }
        var unusual = AdbFiles.Parse("TB_ADB_DIR_V1\0/\0f\0link.txt\0d\0linked-directory\0F\0line\nbreak\0E\x0000" + "1\0");
        Assert(unusual.Limited && unusual.Skipped == 1 && unusual.Entries.Length == 2 && unusual.Entries.All(e => e.Selectable), "limits, symlink types and unsupported names surfaced");
        string unixRoot = "/tmp/toolbox-browser-" + Path.GetFileName(root);
        File.WriteAllText(Path.Combine(root, "browser-script.sh"), AdbFiles.Command(unixRoot), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "browser-quoted.sh"), AdbFiles.Command(unixRoot + "/quo'te $value ; $(touch TOOLBOX_SENTINEL) [x]"), new UTF8Encoding(false));
        report.AppendLine("PASS ADB directory protocol, file/folder/link distinction, Unicode paths, permissions and incomplete-output rejection");
        return report.ToString();
    }
}
