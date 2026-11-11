using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal static class WslClient
{
    internal const string Mode = "WSL";
    internal static string Executable
    {
        get
        {
            string system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), system, "wsl.exe");
            if (!File.Exists(path)) throw new IOException("未找到 WSL，请先安装 Windows Subsystem for Linux 和 Linux 发行版。");
            return path;
        }
    }
    internal static async Task<string[]> Distributions(CancellationToken token)
    {
        var args = new[] { "--list", "--quiet" };
        var result = await AdbClient.Run(Executable, args, token, null, configure: info => Configure(info, args));
        if (result.ExitCode != 0) throw new IOException("读取 WSL 发行版失败：" + result.Error.Trim() + result.Output.Trim());
        return result.Output.Trim('\uFEFF').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToArray();
    }
    internal static string Arguments(string[] args)
    {
        AdbClient.Arguments(args);
        // wsl.exe treats quoted option names as shell commands rather than CLI switches.
        return String.Join(" ", args.Select(value => Regex.IsMatch(value, @"^[A-Za-z0-9_./~:-]+$") ? value : AdbClient.Quote(value)));
    }
    static void Configure(ProcessStartInfo info, string[] args) { info.EnvironmentVariables["WSL_UTF8"] = "1"; info.Arguments = Arguments(args); }
    internal static bool TryUnc(string path, out string distribution, out string directory)
    {
        var match = Regex.Match(path ?? "", @"^\\\\(?:wsl\$|wsl\.localhost)\\([^\\]+)(?:\\(.*))?$", RegexOptions.IgnoreCase);
        distribution = match.Success ? match.Groups[1].Value : "";
        directory = match.Success ? "/" + match.Groups[2].Value.Replace('\\', '/') : "";
        return match.Success;
    }
    internal static void Infer(WorkProject project)
    {
        string distribution, directory;
        if (!TryUnc(project.Directory, out distribution, out directory)) return;
        if (String.IsNullOrWhiteSpace(project.WslDistribution)) project.WslDistribution = distribution;
        if (String.IsNullOrWhiteSpace(project.WslDirectory) && String.Equals(project.WslDistribution, distribution, StringComparison.OrdinalIgnoreCase)) project.WslDirectory = directory;
    }
    internal static string DirectoryFor(WorkProject project)
    {
        string path = String.IsNullOrWhiteSpace(project.WslDirectory) ? project.Directory : project.WslDirectory;
        string distribution, directory;
        if (TryUnc(path, out distribution, out directory))
        {
            if (!String.Equals(project.WslDistribution, distribution, StringComparison.OrdinalIgnoreCase)) throw new IOException("项目目录属于另一套 WSL 发行版，请在项目设置中确认发行版和 Linux 工作目录。");
            path = directory;
        }
        if (String.IsNullOrWhiteSpace(path)) path = "~";
        if (path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0 || !(path == "~" || path.StartsWith("/") || Regex.IsMatch(path, @"^[A-Za-z]:[\\/]")))
            throw new IOException("WSL 工作目录请填写 / 开头的 Linux 路径、~ 或绝对 Windows 路径。");
        return path;
    }
    internal static string[] TerminalArguments(string distribution, string directory)
    {
        if (String.IsNullOrWhiteSpace(distribution) || distribution.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
            throw new IOException("请在项目设置中读取并选择 WSL 发行版。");
        return new[] { "--distribution", distribution, "--cd", directory };
    }
    // Keep stdin open as a lifetime signal. EOF kills only this invocation's setsid group.
    // No distro termination: other terminals, builds and services are left running.
    const string Supervisor = @"directory=$2
case ""$directory"" in
    '~') directory=$HOME ;;
    [A-Za-z]:*) directory=$(wslpath -u ""$directory"") || exit 125 ;;
esac
cd -- ""$directory"" || exit 125
exec 3<&0
setsid /bin/bash -lc 'cd -- ""$1"" || exit; eval ""$2""' toolbox ""$PWD"" ""$1"" </dev/null 3<&- &
job=$!
watcher=
cleanup() { /bin/kill -KILL -- ""-$job"" 2>/dev/null || :; if [ -n ""$watcher"" ]; then kill ""$watcher"" 2>/dev/null || :; fi; }
trap cleanup EXIT
( IFS= read -r ignored <&3; /bin/kill -TERM -- ""-$job"" 2>/dev/null || : ) &
watcher=$!
wait ""$job""
result=$?
exit ""$result""
";
    internal static Task<AdbResult> Run(string distribution, string directory, string command, CancellationToken token, Action<string, bool> output)
    {
        var args = TerminalArguments(distribution, "~").Concat(new[] { "--exec", "/bin/sh", "-c", Supervisor.Replace("\r\n", "\n"), "toolbox", command.Replace("\r\n", "\n"), directory }).ToArray();
        return AdbClient.Run(Executable, args, token, output, configure: info => Configure(info, args), keepInputOpen: true);
    }
}
