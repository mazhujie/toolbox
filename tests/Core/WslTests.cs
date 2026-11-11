using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal static class WslTests
{
    static void Assert(bool value, string message) { if (!value) throw new Exception("FAIL WSL: " + message); }
    static string Quote(string text) { return "'" + text.Replace("'", "'\"'\"'") + "'"; }
    internal static string Unit()
    {
        foreach (string host in new[] { "wsl.localhost", "wsl$" })
        {
            var project = new WorkProject { Directory = @"\\" + host + @"\Ubuntu-Test\home\user\a ' & 中文" };
            WslClient.Infer(project);
            Assert(project.WslDistribution == "Ubuntu-Test" && WslClient.DirectoryFor(project) == "/home/user/a ' & 中文", "UNC distro and path inference");
            project.WslDistribution = "Other"; project.WslDirectory = "";
            bool rejected = false; try { WslClient.DirectoryFor(project); } catch (IOException) { rejected = true; } Assert(rejected, "cross-distro path rejected");
        }
        foreach (string path in new[] { "/home/user", "~", @"D:\projects\demo" }) Assert(WslClient.DirectoryFor(new WorkProject { WslDirectory = path }) == path, "absolute path retained");
        foreach (string path in new[] { "relative", "~/ambiguous", "/tmp\ninjected" })
        { bool rejected = false; try { WslClient.DirectoryFor(new WorkProject { WslDirectory = path }); } catch (IOException) { rejected = true; } Assert(rejected, "invalid working directory rejected"); }
        return "PASS WSL UNC inference, explicit distribution boundaries and working directory validation\r\n";
    }
    internal static async Task<string> Live()
    {
        string[] names;
        using (var timeout = new CancellationTokenSource(10000)) names = await WslClient.Distributions(timeout.Token);
        string distro = Environment.GetEnvironmentVariable("TOOLBOX_TEST_WSL");
        if (String.IsNullOrWhiteSpace(distro)) { Assert(names.Length == 1, "set TOOLBOX_TEST_WSL when multiple distros exist"); distro = names[0]; }
        Assert(names.Contains(distro), "configured distro discovered");
        string root = "/tmp/toolbox-wsl-" + Guid.NewGuid().ToString("N"), directory = root + "/路径 ' & $ [x]";
        try
        {
            var result = await WslClient.Run(distro, "/tmp", "mkdir -p -- " + Quote(directory), CancellationToken.None, null);
            Assert(result.ExitCode == 0, "create isolated scratch directory");
            result = await WslClient.Run(distro, directory, "shopt -q login_shell || exit 19\r\nprintf '%s\\n' \"$PWD\" '中文输出' 'literal \" $HOME & | []'", CancellationToken.None, null);
            Assert(result.ExitCode == 0 && result.Output.Contains(directory) && result.Output.Contains("中文输出") && result.Output.Contains("literal \" $HOME & | []"), "Bash login, Unicode, CRLF, quotes and exact working directory");
            result = await WslClient.Run(distro, root + "/missing", "printf MUST_NOT_RUN", CancellationToken.None, null);
            Assert(result.ExitCode != 0 && !result.Output.Contains("MUST_NOT_RUN"), "missing directory never falls back to root");
            result = await WslClient.Run(distro, directory, "printf '中文错误\\n' >&2; exit 7", CancellationToken.None, null);
            Assert(result.ExitCode == 7 && result.Error.Contains("中文错误"), "stderr and nonzero exit preserved");
            result = await WslClient.Run("Toolbox-Missing-" + Guid.NewGuid().ToString("N"), "/tmp", "printf MUST_NOT_RUN", CancellationToken.None, null);
            Assert(result.ExitCode != 0 && !(result.Output + result.Error).Contains("\0"), "missing distro has readable output");

            using (var otherCancel = new CancellationTokenSource())
            using (var cancel = new CancellationTokenSource())
            {
                var otherReady = new TaskCompletionSource<int>(); var ready = new TaskCompletionSource<int>(); var child = new TaskCompletionSource<int>();
                var other = WslClient.Run(distro, "/tmp", "echo OTHER $$; sleep 60", otherCancel.Token, (text, error) => { var match = Regex.Match(text, @"OTHER (\d+)"); if (match.Success) otherReady.TrySetResult(Int32.Parse(match.Groups[1].Value)); });
                try
                {
                    Assert(await Task.WhenAny(otherReady.Task, Task.Delay(5000)) == otherReady.Task, "independent session started");
                    var pending = WslClient.Run(distro, directory, "echo READY $$; sleep 60 & echo CHILD $!; wait", cancel.Token, (text, error) => {
                        var match = Regex.Match(text, @"READY (\d+)"); if (match.Success) ready.TrySetResult(Int32.Parse(match.Groups[1].Value));
                        match = Regex.Match(text, @"CHILD (\d+)"); if (match.Success) child.TrySetResult(Int32.Parse(match.Groups[1].Value));
                    });
                    try
                    {
                        var bothReady = Task.WhenAll(ready.Task, child.Task);
                        Assert(await Task.WhenAny(bothReady, Task.Delay(5000)) == bothReady, "live output before command exits");
                        cancel.Cancel(); Assert(await Task.WhenAny(pending, Task.Delay(6000)) == pending, "WSL cancellation completes");
                        bool cancelled = false; try { await pending; } catch (OperationCanceledException) { cancelled = true; } Assert(cancelled, "cancellation status");
                        result = await WslClient.Run(distro, "/tmp", "test ! -d /proc/" + ready.Task.Result + " && test ! -d /proc/" + child.Task.Result + " && kill -0 " + otherReady.Task.Result, CancellationToken.None, null);
                        Assert(result.ExitCode == 0 && !other.IsCompleted, "only command and child stopped; independent session remains alive");
                    }
                    finally { cancel.Cancel(); try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
                }
                finally { otherCancel.Cancel(); try { other.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
            }
            return "PASS real WSL distro discovery, Bash login, exact cwd, quoting, Unicode, streaming, errors and missing-directory protection\r\nPASS real WSL cancellation stops command and child, preserving independent WSL sessions\r\n";
        }
        finally { WslClient.Run(distro, "/tmp", "rm -r -- " + Quote(root), CancellationToken.None, null).GetAwaiter().GetResult(); }
    }
}
