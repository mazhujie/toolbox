using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class ProjectData
{
    public int Version = 1;
    public List<WorkProject> Projects = new List<WorkProject>();
    public List<WorkCheckpoint> Checkpoints = new List<WorkCheckpoint>();
    public List<WorkRun> Runs = new List<WorkRun>();
    public List<WorkJournal> Journals = new List<WorkJournal>();
    public string SelectedProject = "", View = "projects", Page = "现场";
    public int Width = 1100, Height = 820;
}
internal sealed class WorkProject
{
    public string Id = Guid.NewGuid().ToString("N"), Name = "新项目", Directory = "", Editor = "", Urls = "";
    public string Done = "", Blocked = "", Next = "", Draft = "", Mode = "ADB Shell", JournalId = "";
    public string TerminalMode = "Windows PowerShell", WslDistribution = "", WslDirectory = "";
    public bool Terminal, Archived = false;
    public List<WorkCommand> Commands = new List<WorkCommand>();
    public override string ToString() { return Name; }
}
internal sealed class WorkCommand
{
    public string Name = "", Text = "", Mode = "ADB Shell";
    public override string ToString() { return Name + " · " + Mode; }
}
internal sealed class WorkCheckpoint
{
    public string Id = Guid.NewGuid().ToString("N"), ProjectId = "", Created = ProjectStore.Now(), Done = "", Blocked = "", Next = "";
    public override string ToString() { return Created + "  " + Next.Replace("\r", "").Replace("\n", " "); }
}
internal sealed class WorkRun
{
    public string Id = Guid.NewGuid().ToString("N"), ProjectId = "", Started = ProjectStore.Now(), Ended = "";
    public string Command = "", Target = "", Directory = "", Status = "运行中", Log = "";
    public int? ExitCode;
    public override string ToString() { return Started + "  [" + Status + "] " + Command.Replace("\r", "").Replace("\n", " "); }
}
internal sealed class WorkJournal
{
    public string Id = Guid.NewGuid().ToString("N"), ProjectId = "", Title = "新日志", Text = "";
    public string Created = ProjectStore.Now(), Updated = ProjectStore.Now(), RunId = "";
    public List<string> Images = new List<string>();
    public override string ToString() { return (String.IsNullOrWhiteSpace(Title) ? "未命名日志" : Title) + "  · " + Updated; }
}

// Metadata is replaced atomically; command output and PNG attachments live outside the JSON.
internal sealed class ProjectStore : IDisposable
{
    internal readonly string Root;
    internal readonly ProjectData Data;
    readonly FileStream lease;
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
    internal event Action Changed;
    internal static string Now() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }
    internal ProjectStore(string root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toolbox", "workspace");
        Directory.CreateDirectory(Root);
        lease = new FileStream(Path.Combine(Root, "workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            string path = Path.Combine(Root, "workspace.json");
            Data = File.Exists(path) ? json.Deserialize<ProjectData>(File.ReadAllText(path, Encoding.UTF8)) : new ProjectData();
            if (Data == null || Data.Version != 1 || Data.Projects == null || Data.Runs == null || Data.Journals == null || Data.Checkpoints == null)
                throw new IOException("项目数据格式无法识别，原文件已保留：" + path);
            if (Data.Projects.Any(p => p == null || p.Commands == null) || Data.Journals.Any(j => j == null || j.Images == null))
                throw new IOException("项目数据不完整，原文件已保留：" + path);
            foreach (var run in Data.Runs.Where(r => r.Status == "运行中")) { run.Status = "意外中断"; run.Ended = Now(); }
            if (!Data.Projects.Any(p => !p.Archived)) Data.Projects.Add(new WorkProject { Name = "默认项目" });
            if (!Data.Projects.Any(p => p.Id == Data.SelectedProject && !p.Archived)) Data.SelectedProject = Data.Projects.First(p => !p.Archived).Id;
        }
        catch { lease.Dispose(); throw; }
    }
    internal WorkProject Current { get { return Data.Projects.First(p => p.Id == Data.SelectedProject); } }
    internal void Save()
    {
        string path = Path.Combine(Root, "workspace.json"), temporary = path + ".tmp";
        byte[] bytes = new UTF8Encoding(false).GetBytes(json.Serialize(Data));
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
    }
    internal void Notify() { if (Changed != null) Changed(); }
    internal string FilePath(string relative)
    {
        if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new IOException("附件路径无效。");
        string path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("附件路径不能超出项目数据目录。");
        return path;
    }
    internal string AddImage(WorkJournal journal, Image image)
    {
        string relative = Path.Combine("images", journal.Id, Guid.NewGuid().ToString("N") + ".png");
        string path = FilePath(relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var copy = new Bitmap(image)) copy.Save(path, ImageFormat.Png);
        journal.Images.Add(relative); journal.Updated = Now(); Save();
        return relative;
    }
    internal WorkJournal NewJournal(string projectId)
    {
        var journal = new WorkJournal { ProjectId = projectId }; Data.Journals.Insert(0, journal); return journal;
    }
    internal RunRecording Begin(string projectId, string command, string target, string directory)
    {
        var run = new WorkRun { ProjectId = projectId, Command = command, Target = target, Directory = directory };
        run.Log = Path.Combine("runs", run.Id + ".log");
        Directory.CreateDirectory(Path.Combine(Root, "runs"));
        var recording = new RunRecording(this, run, FilePath(run.Log));
        Data.Runs.Insert(0, run);
        try { Save(); } catch { Data.Runs.Remove(run); recording.Dispose(); throw; }
        Notify(); return recording;
    }
    internal string ReadRun(WorkRun run)
    {
        string header = run.Started + " → " + run.Ended + "\r\n" + run.Target + " · " + run.Status +
            (run.ExitCode.HasValue ? " · 退出码 " + run.ExitCode : "") + "\r\n目录：" + run.Directory + "\r\n$ " + run.Command + "\r\n\r\n";
        if (String.IsNullOrEmpty(run.Log)) return header;
        using (var stream = new FileStream(FilePath(run.Log), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bool trimmed = stream.Length > 256 * 1024;
            if (trimmed) stream.Seek(-256 * 1024, SeekOrigin.End);
            using (var reader = new StreamReader(stream, Encoding.UTF8)) return header + (trimmed ? "[显示末尾内容；完整输出见日志文件]\r\n" : "") + reader.ReadToEnd();
        }
    }
    public void Dispose() { lease.Dispose(); }
}

internal sealed class RunRecording : IDisposable
{
    readonly ProjectStore store;
    readonly StreamWriter writer;
    readonly object sync = new object();
    internal readonly WorkRun Run;
    bool closed;
    string writeError;
    internal RunRecording(ProjectStore store, WorkRun run, string path)
    {
        this.store = store; Run = run;
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        writer.AutoFlush = true;
    }
    internal void Append(string text, bool error)
    {
        lock (sync)
        {
            if (closed || writeError != null) return;
            // Keep draining the child process even when the disk fills up.
            try { writer.Write((error ? "[stderr] " : "") + text); }
            catch (IOException ex) { writeError = ex.Message; }
        }
    }
    internal void Finish(string status, int? exit)
    {
        try { Dispose(); } catch (IOException ex) { writeError = ex.Message; }
        Run.Status = status + (writeError == null ? "" : "（日志不完整）"); Run.ExitCode = exit; Run.Ended = ProjectStore.Now(); store.Save(); store.Notify();
        if (writeError != null) throw new IOException("输出日志未完整保存：" + writeError);
    }
    public void Dispose() { lock (sync) { if (!closed) { closed = true; writer.Dispose(); } } }
}
