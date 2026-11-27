// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.Json;

namespace VikingTool
{
    internal sealed class UsageError : Exception
    { public UsageError(string message) : base(message) { } }

    internal sealed class PortInfo
    {
        public string port { get; set; }
        public string name { get; set; }
        public bool chameleon { get; set; }
    }

    internal static class CliProgram
    {
        private static readonly Dictionary<string, string> options = new Dictionary<string, string>();
        private static string Get(string key) { return options.ContainsKey(key) ? options[key] : null; }
        private static bool Flag(string key) { return options.ContainsKey(key); }

        private static void Parse(string command, string[] args)
        {
            if (!new[] { "help", "ports", "info", "read", "write", "verify" }.Contains(command))
                throw new UsageError("未知命令：" + command + "。运行 viking help 查看用法。");
            for (int i = 1; i < args.Length; i++)
            {
                string key = args[i];
                if (options.ContainsKey(key)) throw new UsageError("参数重复：" + key);
                if (key == "--json" || key == "--blank-t5577") { options.Add(key, "true"); continue; }
                if (!new[] { "--port", "--out", "--from", "--id" }.Contains(key)) throw new UsageError("未知参数：" + key);
                if (++i == args.Length || args[i].StartsWith("--")) throw new UsageError(key + " 后面需要一个值。");
                options.Add(key, args[i]);
            }
            foreach (string key in options.Keys)
            {
                bool allowed = key == "--json"
                    || key == "--port" && new[] { "info", "read", "write", "verify" }.Contains(command)
                    || key == "--out" && command == "read"
                    || key == "--blank-t5577" && command == "write"
                    || (key == "--from" || key == "--id") && (command == "write" || command == "verify");
                if (!allowed) throw new UsageError(command + " 不接受 " + key + "。");
            }
            if (command == "write" || command == "verify")
            {
                if ((Get("--from") == null) == (Get("--id") == null))
                    throw new UsageError("请选择 --from 卡号文件 或 --id 8位编号，二者不能同时使用。");
                if (command == "write" && !Flag("--blank-t5577"))
                    throw new UsageError("先移开源卡并放好 T5577 空白卡，再加上 --blank-t5577；此参数表示允许覆盖该空白卡。");
            }
        }

        private static List<PortInfo> ListPorts()
        {
            var ports = SerialPort.GetPortNames().OrderBy(x => x).Select(x => new PortInfo { port = x, name = "Serial port" }).ToList();
            // WMI is Windows-only. Other platforms can select a port returned by `ports`.
            if (!OperatingSystem.IsWindows()) return ports;
            try
            {
                using (var query = new ManagementObjectSearcher("SELECT DeviceID, Name, PNPDeviceID FROM Win32_SerialPort"))
                using (var items = query.Get())
                {
                    foreach (ManagementObject item in items)
                    {
                        string deviceId = Convert.ToString(item["DeviceID"]);
                        var match = ports.FirstOrDefault(x => x.port.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
                        if (match == null) continue;
                        match.name = Convert.ToString(item["Name"]);
                        match.chameleon = Convert.ToString(item["PNPDeviceID"]).IndexOf("VID_6868&PID_8686", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            return ports;
        }

        private static string ResolvePort()
        {
            var ports = ListPorts();
            string requested = Get("--port");
            if (requested != null)
            {
                var selected = ports.FirstOrDefault(x => x.port.Equals(requested, StringComparison.OrdinalIgnoreCase));
                if (selected == null) throw new UsageError("找不到端口 " + requested + "。运行 viking ports 查看当前端口。");
                return selected.port;
            }
            var candidates = ports.Where(x => x.chameleon).ToList();
            if (candidates.Count != 1) throw new UsageError(candidates.Count == 0
                ? "没有找到 Chameleon Ultra。请连接 USB，或用 --port COM3 指定端口。"
                : "发现多个 Chameleon 设备，请用 --port 指定端口。");
            return candidates[0].port;
        }

        private static void Emit(object value, string text)
        { Console.WriteLine(Flag("--json") ? JsonSerializer.Serialize(value) : text); }

        private static void Help()
        {
            Console.WriteLine(@"Viking CLI — .NET 10 / Chameleon Ultra / USB / 125 kHz

  viking ports [--json]
  viking info [--port COM3] [--json]
  viking read [--out card.viking.json] [--port COM3] [--json]
  viking write --from card.viking.json --blank-t5577 [--port COM3] [--json]
  viking verify --from card.viking.json [--port COM3] [--json]

write / verify 可用 --id 8位十六进制编号代替 --from。
Windows 未指定端口时自动选择唯一的 Chameleon Ultra。
Linux/macOS 请用 --port 指定端口，例如 /dev/ttyACM0 或 /dev/cu.usbmodem...。
read 默认保存到程序旁的 last-read.viking.json。

write 只写 T5577/T55xx 空白低频卡。每次只放一张卡，并移开源卡。
--blank-t5577 表示确认目标卡类型、已换卡并允许覆盖其内容。
写入后回读两次，编号一致才返回成功；不会自动重发写入命令。
已有 Viking 数据会阻止写入，可用 verify 核对。

--json：一条结果写入 stdout；进度、警告写入 stderr。
退出码：0 成功；1 设备/读写/文件错误；2 参数或端口选择错误。
写入非零退出时，卡可能已被修改，请先 verify，勿直接重复写入。");
        }

        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            bool json = args.Contains("--json");
            string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
            if (command == "--help" || command == "-h") command = "help";
            try
            {
                Parse(command, args);
                if (command == "help") { Help(); return 0; }
                if (command == "ports")
                {
                    var ports = ListPorts();
                    Emit(new { ok = true, command = command, ports = ports }, ports.Count == 0 ? "没有找到串口。" :
                        string.Join(Environment.NewLine, ports.Select(x => x.port.PadRight(9) + (x.chameleon ? "Chameleon Ultra  " : "") + x.name)));
                    return 0;
                }
                // Validate inputs before opening any device.
                string expected = null;
                if (command == "write" || command == "verify")
                    expected = Get("--id") != null ? CardRecord.NormalizeId(Get("--id")) : CardRecord.Load(Path.GetFullPath(Get("--from"))).id;
                string output = command == "read" ? Path.GetFullPath(Get("--out") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-read.viking.json")) : null;
                if (output != null && !Directory.Exists(Path.GetDirectoryName(output))) throw new UsageError("输出目录不存在：" + Path.GetDirectoryName(output));
                string port = ResolvePort();
                var service = new VikingService(() => new SerialChannel(port), message => Console.Error.WriteLine(message));
                if (command == "info")
                {
                    var info = service.Probe();
                    Emit(new { ok = true, command = command, port = port, firmware = info.Firmware, vikingRead = info.CanRead, vikingWrite = info.CanWrite },
                        "Chameleon Ultra · " + port + " · " + info.Firmware + Environment.NewLine +
                        "Viking 读取：" + (info.CanRead ? "支持" : "不支持") + "；写入：" + (info.CanWrite ? "支持" : "不支持"));
                }
                else if (command == "read")
                {
                    Console.Error.WriteLine("正在读取 Viking，请保持卡片不动……");
                    var card = service.Read();
                    card.Save(output);
                    Emit(new { ok = true, command = command, port = port, card = card, saved = output, matchingReads = 2 },
                        "Viking · 125 kHz · " + card.id + Environment.NewLine + "两次读取一致，已保存：" + output);
                }
                else if (command == "verify")
                {
                    Console.Error.WriteLine("正在核对卡号；仅执行读取……");
                    service.Verify(expected);
                    Emit(new { ok = true, command = command, port = port, id = expected, matchingReads = 2, wroteCard = false },
                        "核对通过：" + expected + "。两次回读一致，此次没有写卡。");
                }
                else if (command == "write")
                {
                    Console.Error.WriteLine("请保持 T5577 空白卡不动。目标编号：" + expected);
                    service.WriteBlank(expected, Flag("--blank-t5577"));
                    Emit(new { ok = true, command = command, port = port, id = expected, verified = true, matchingReads = 2 },
                        "写入成功：" + expected + "。已回读两次，均与源卡一致。请到门禁读头实际试刷。");
                }
                return 0;
            }
            catch (Exception e)
            {
                int code = e is UsageError ? 2 : 1;
                string message = e is UnauthorizedAccessException ? "无法访问设备或文件。请关闭占用端口的读卡程序，并检查文件权限。" :
                    e is TimeoutException ? "设备没有及时回复，请检查 USB 连接再重试读取或核对。" : e.Message;
                if (json) Console.WriteLine(JsonSerializer.Serialize(new { ok = false, command = command, exitCode = code, error = message }));
                else Console.Error.WriteLine("错误：" + message);
                return code;
            }
        }
    }
}
