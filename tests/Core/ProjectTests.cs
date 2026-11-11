using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class ProjectTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception("FAIL projects: " + message); }
    internal static async Task<string> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "toolbox-project-" + Guid.NewGuid().ToString("N"));
        string picture, id;
        try
        {
            using (var store = new ProjectStore(root))
            {
                id = store.Current.Id; store.Current.Name = "板卡 · 中文"; store.Current.Next = "检查电源\r\n继续调试";
                store.Current.Draft = "printf '你好'"; store.Current.WslDistribution = "Ubuntu-Test"; store.Current.WslDirectory = "/home/user/demo"; store.Current.TerminalMode = "WSL"; store.Save();
                bool locked = false; try { using (var other = new ProjectStore(root)) { } } catch (IOException) { locked = true; }
                Assert(locked, "a second writer cannot overwrite the workspace");
                var journal = store.NewJournal(id); journal.Title = "黑屏排查"; journal.Text = "复现 → 测量 → 结论";
                using (var image = new Bitmap(24, 16)) { image.SetPixel(7, 5, Color.Coral); picture = store.AddImage(journal, image); image.SetPixel(7, 5, Color.Blue); }
                using (var saved = new Bitmap(store.FilePath(picture))) Assert(saved.GetPixel(7, 5).ToArgb() == Color.Coral.ToArgb(), "image cloned and persisted as PNG");
                store.Data.Checkpoints.Add(new WorkCheckpoint { ProjectId = id, Next = store.Current.Next }); store.Current.Next = "新的下一步";
                var run = store.Begin(id, "cat /proc/version", "ADB · TEST", "/demo");
                string payload = new string('x', 400000); run.Append(payload, false); run.Append("错误信息", true); run.Finish("失败", 7);
                Assert(new FileInfo(store.FilePath(run.Run.Log)).Length > 400000 && store.ReadRun(run.Run).Length < 270000, "full log on disk, bounded preview");
                store.Begin(id, "interrupted", "TEST", "").Dispose();
                foreach (string path in new[] { "../outside.png", @"C:\outside.png", "" })
                { bool rejected = false; try { store.FilePath(path); } catch (IOException) { rejected = true; } Assert(rejected, "path traversal rejected"); }
            }
            using (var store = new ProjectStore(root))
            {
                Assert(store.Current.Id == id && store.Current.Name == "板卡 · 中文" && store.Current.Draft == "printf '你好'", "project and draft survive restart");
                Assert(store.Current.WslDistribution == "Ubuntu-Test" && store.Current.WslDirectory == "/home/user/demo" && store.Current.TerminalMode == "WSL", "WSL configuration survives restart");
                Assert(store.Data.Journals.Single().Text == "复现 → 测量 → 结论" && File.Exists(store.FilePath(store.Data.Journals.Single().Images.Single())), "journal and attachment survive restart");
                Assert(store.Data.Checkpoints.Single().Next == "检查电源\r\n继续调试", "checkpoint is an independent snapshot");
                Assert(store.Data.Runs[0].Status == "意外中断" && store.Data.Runs[1].ExitCode == 7, "interrupted and failed runs retain correct status");
            }
            File.WriteAllText(Path.Combine(root, "workspace.json"), "{broken");
            bool corrupt = false; try { using (var store = new ProjectStore(root)) { } } catch (ArgumentException) { corrupt = true; }
            Assert(corrupt && File.ReadAllText(Path.Combine(root, "workspace.json")) == "{broken" && File.Exists(Path.Combine(root, "workspace.json.bak")), "corrupt data is never silently reset; previous save retained");

            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            string directory = Path.Combine(root, "路径 ' & $ 中文"); Directory.CreateDirectory(directory);
            Func<string, string[]> args = script => new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-Command", ProjectWorkspace.LocalScript(directory, script) };
            var result = await AdbClient.Run(powershell, args("[Console]::WriteLine((Get-Location).Path); [Console]::WriteLine('调试正常')"), CancellationToken.None, null, true);
            Assert(result.ExitCode == 0 && result.Output.Contains(directory) && result.Output.Contains("调试正常"), "PowerShell Unicode and metacharacter directory");
            Assert(!result.Error.Contains("CLIXML"), "startup progress does not leak PowerShell protocol into output");
            result = await AdbClient.Run(powershell, args("Write-Error '中文错误'"), CancellationToken.None, null, true);
            Assert(result.ExitCode != 0 && result.Error.Contains("中文错误") && !result.Error.Contains("CLIXML"), "PowerShell error is readable UTF-8 text");
            result = await AdbClient.Run(powershell, args("exit 9"), CancellationToken.None, null, true);
            Assert(result.ExitCode == 9, "PowerShell exit code retained");
            using (var cancel = new CancellationTokenSource())
            {
                var ready = new TaskCompletionSource<bool>();
                var task = AdbClient.Run(powershell, args("[Console]::WriteLine('READY'); Start-Sleep -Seconds 60"), cancel.Token, (s, error) => { if (s.Contains("READY")) ready.TrySetResult(true); }, true);
                Assert(await Task.WhenAny(ready.Task, Task.Delay(5000)) == ready.Task, "PowerShell streams before exit"); cancel.Cancel();
                Assert(await Task.WhenAny(task, Task.Delay(5000)) == task, "PowerShell cancellation completes");
                bool cancelled = false; try { await task; } catch (OperationCanceledException) { cancelled = true; } Assert(cancelled, "cancel status");
            }
            return "PASS project atomic persistence, exclusive writer, corrupt-data protection, Unicode, PNG attachments, checkpoints and complete debug logs\r\nPASS Windows PowerShell directory quoting, UTF-8, streaming, exit status and cancellation\r\n";
        }
        finally { Directory.Delete(root, true); }
    }
}
