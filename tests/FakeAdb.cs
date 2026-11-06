using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

// Test fixture only. It never contacts an ADB server, Android device or command shell.
internal static class FakeAdb
{
    static readonly string root = AppDomain.CurrentDomain.BaseDirectory;
    static string DevicePath(string path)
    {
        if (!path.StartsWith("/") || path.Split('/').Any(p => p == "..")) throw new IOException("invalid fixture path");
        return Path.Combine(root, "device", path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }
    static void Copy(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (string directory in Directory.GetDirectories(source)) Copy(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }
    }
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.Length == 0) return 2;
            if (args[0] == "fixture-args")
            {
                foreach (string value in args.Skip(1)) Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
                return 0;
            }
            if (args[0] == "devices")
            {
                Console.WriteLine("List of devices attached");
                string mode = File.Exists(Path.Combine(root, "mode.txt")) ? File.ReadAllText(Path.Combine(root, "mode.txt")) : "";
                if (mode == "invalid") { Console.Error.WriteLine("fixture list failure"); return 1; }
                if (mode != "removed") Console.WriteLine("DEMO-USB\tdevice product:fixture model:Demo_Phone transport_id:1");
                Console.WriteLine("DEMO-LOCKED\tunauthorized transport_id:2");
                Console.WriteLine("DEMO-OFFLINE\toffline transport_id:3");
                if (mode == "removed") Console.WriteLine("OTHER-DEVICE\tdevice model:Other_Phone transport_id:4");
                if (File.Exists(Path.Combine(root, "connected.txt"))) Console.WriteLine(File.ReadAllText(Path.Combine(root, "connected.txt")) + "\tdevice model:Network_Demo transport_id:5");
                return 0;
            }
            if (args[0] == "connect" || args[0] == "disconnect")
            {
                if (args.Length != 2 || String.IsNullOrEmpty(args[1])) return 3;
                if (args[1] == "fail.invalid:5555") { Console.WriteLine("failed to connect (simulated)"); return 0; }
                if (args[0] == "connect") File.WriteAllText(Path.Combine(root, "connected.txt"), args[1]);
                else File.Delete(Path.Combine(root, "connected.txt"));
                Console.WriteLine(args[0] + " " + args[1] + " (simulated)"); return 0;
            }
            if (args.Length < 3 || (args[0] != "-t" && args[0] != "-s")) { Console.Error.WriteLine("explicit device required"); return 3; }
            if (args[1] != "1" && args[1] != "5" && args[1] != "DEMO-USB") { Console.Error.WriteLine("device offline or unauthorized"); return 4; }
            File.WriteAllText(Path.Combine(root, "last-args.txt"), String.Join("\n", args.Select(v => Convert.ToBase64String(Encoding.UTF8.GetBytes(v)))));
            if (args[2] == "shell")
            {
                string command = args.Last();
                if (command == "fixture-hang")
                {
                    File.WriteAllText(Path.Combine(root, "pid.txt"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                    Console.Write("READY\n"); Console.Out.Flush(); Thread.Sleep(60000); return 0;
                }
                if (command == "fixture-error") { Console.Error.WriteLine("模拟：Permission denied"); return 7; }
                if (command == "fixture-flood")
                {
                    for (int i = 0; i < 200; i++) { Console.Out.Write(new string('o', 4096)); Console.Error.Write(new string('e', 4096)); }
                    return 0;
                }
                Console.Write("模拟设备输出（未连接真实设备）\n"); Console.Out.Flush();
                Thread.Sleep(250);
                Console.WriteLine("/sdcard\nDownload\nDocuments\nAndroid");
                return 0;
            }
            if (args[2] == "push" || args[2] == "pull")
            {
                bool push = args[2] == "push";
                string remote = push ? args[4] : args[3];
                if (remote.Contains("permission-denied")) { Console.Error.WriteLine("模拟：Permission denied"); return 1; }
                if (remote.Contains("slow-transfer"))
                {
                    File.WriteAllText(Path.Combine(root, "pid.txt"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                    Console.Write("[ 1%] 模拟传输等待\r"); Console.Out.Flush(); Thread.Sleep(60000); return 0;
                }
                string source = push ? args[3] : DevicePath(remote), target = push ? DevicePath(remote) : args[4];
                if (Directory.Exists(target) || (push && remote.EndsWith("/"))) target = Path.Combine(target, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
                Copy(source, target);
                Console.Write("[ 50%] 模拟传输\r"); Console.Out.Flush(); Thread.Sleep(100);
                Console.WriteLine("[100%] 完成\n1 item " + (push ? "pushed" : "pulled") + " (simulated)"); return 0;
            }
            Console.Error.WriteLine("unknown fixture command"); return 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
