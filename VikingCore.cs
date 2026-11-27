// SPDX-License-Identifier: GPL-3.0-only
// ChameleonUltra protocol and command implementation references: see NOTICE.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace VikingTool
{
    public sealed class Reply
    {
        public int Status;
        public byte[] Data;
        public Reply(int status, params byte[] data) { Status = status; Data = data; }
    }

    public interface IDeviceChannel : IDisposable
    {
        Reply Send(int command, params byte[] data);
    }

    public static class Wire
    {
        public static byte Lrc(byte[] bytes, int count)
        {
            int sum = 0;
            for (int i = 0; i < count; i++) sum += bytes[i];
            return (byte)((256 - (sum & 255)) & 255);
        }

        public static byte[] Frame(int command, byte[] data)
        {
            var frame = new byte[data.Length + 10];
            frame[0] = 0x11; frame[1] = 0xEF;
            frame[2] = (byte)(command >> 8); frame[3] = (byte)command;
            frame[6] = (byte)(data.Length >> 8); frame[7] = (byte)data.Length;
            frame[8] = Lrc(frame, 8);
            Array.Copy(data, 0, frame, 9, data.Length);
            frame[frame.Length - 1] = Lrc(frame, frame.Length - 1);
            return frame;
        }

        public static Reply Decode(int command, byte[] frame)
        {
            if (frame.Length < 10 || frame[0] != 0x11 || frame[1] != 0xEF || Lrc(frame, 9) != 0)
                throw new IOException("设备回复的校验不正确，请重新连接 USB。");
            int actual = (frame[2] << 8) | frame[3];
            int count = (frame[6] << 8) | frame[7];
            if (actual != command || count + 10 != frame.Length || Lrc(frame, frame.Length) != 0)
                throw new IOException("设备回复不完整或与当前操作不匹配，请重新连接。");
            var data = new byte[count];
            Array.Copy(frame, 9, data, 0, count);
            return new Reply((frame[4] << 8) | frame[5], data);
        }
    }

    public sealed class SerialChannel : IDeviceChannel
    {
        private readonly SerialPort port;
        private static readonly HashSet<int> Allowed = new HashSet<int> { 1000, 1001, 1002, 1017, 1033, 1035, 3004, 3005 };

        public SerialChannel(string name)
        {
            port = new SerialPort(name, 115200, Parity.None, 8, StopBits.One);
            port.DtrEnable = true; port.RtsEnable = true;
            port.ReadTimeout = 6000; port.WriteTimeout = 3000;
            try { port.Open(); port.DiscardInBuffer(); }
            catch { port.Dispose(); throw; }
        }

        public Reply Send(int command, params byte[] data)
        {
            if (!Allowed.Contains(command)) throw new InvalidOperationException("本工具不支持此设备命令。");
            var frame = Wire.Frame(command, data);
            port.Write(frame, 0, frame.Length);
            var timer = Stopwatch.StartNew();
            var header = ReadExact(9, timer);
            if (header[0] != 0x11 || header[1] != 0xEF || Wire.Lrc(header, 9) != 0)
                throw new IOException("设备回复头部校验失败，请重新连接。");
            int length = (header[6] << 8) | header[7];
            if (length > 4096) throw new IOException("设备回复长度异常。");
            var tail = ReadExact(length + 1, timer);
            var reply = new byte[header.Length + tail.Length];
            Array.Copy(header, reply, header.Length);
            Array.Copy(tail, 0, reply, header.Length, tail.Length);
            return Wire.Decode(command, reply);
        }

        private byte[] ReadExact(int count, Stopwatch timer)
        {
            var result = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int remaining = 6500 - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException("设备没有及时回复。");
                port.ReadTimeout = remaining;
                int read = port.Read(result, offset, count - offset);
                if (read == 0) throw new IOException("设备连接已中断。");
                offset += read;
            }
            return result;
        }

        public void Dispose() { port.Dispose(); }
    }

    public sealed class CardRecord
    {
        public int version { get; set; }
        public string protocol { get; set; }
        public int frequencyKhz { get; set; }
        public string id { get; set; }
        public string readAtUtc { get; set; }

        public static CardRecord Create(string cardId)
        {
            return new CardRecord { version = 1, protocol = "Viking", frequencyKhz = 125,
                id = NormalizeId(cardId), readAtUtc = DateTime.UtcNow.ToString("o") };
        }

        public static string NormalizeId(string value)
        {
            if (value == null || !Regex.IsMatch(value, "\\A[0-9a-fA-F]{8}\\z"))
                throw new InvalidDataException("Viking 编号必须是 8 位十六进制字符。");
            return value.ToUpperInvariant();
        }

        public static CardRecord Load(string path)
        {
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("卡片文件过大。");
            var card = JsonSerializer.Deserialize<CardRecord>(File.ReadAllText(path));
            if (card == null || card.version != 1 || card.protocol != "Viking" || card.frequencyKhz != 125)
                throw new InvalidDataException("请选择本工具保存的 Viking 125 kHz 卡片文件。");
            card.id = NormalizeId(card.id);
            return card;
        }

        public void Save(string path)
        {
            NormalizeId(id);
            File.WriteAllText(path, JsonSerializer.Serialize(this), new UTF8Encoding(false));
        }
    }

    public sealed class DeviceInfo
    {
        public string Firmware;
        public bool CanRead;
        public bool CanWrite;
    }

    public sealed class VikingService
    {
        private readonly Func<IDeviceChannel> open;
        private readonly Action<string> log;
        public VikingService(Func<IDeviceChannel> factory, Action<string> report)
        { open = factory; log = report ?? delegate { }; }

        private static Reply Expect(Reply reply, int status, string action)
        {
            if (reply.Status != status)
            {
                string why = reply.Status == 0x66 ? "设备未进入读卡模式" :
                    reply.Status == 0x67 || reply.Status == 0x69 ? "此固件不支持该功能" :
                    reply.Status == 0x60 ? "设备拒绝了参数" : "设备状态 0x" + reply.Status.ToString("X2");
                throw new IOException(action + "失败：" + why + "。");
            }
            return reply;
        }

        private static DeviceInfo Inspect(IDeviceChannel channel)
        {
            var model = Expect(channel.Send(1033), 0x68, "识别设备");
            if (model.Data.Length != 1 || model.Data[0] != 0)
                throw new IOException("需要支持读写的 Chameleon Ultra；Lite 不支持此操作。");
            var caps = Expect(channel.Send(1035), 0x68, "检查功能");
            if (caps.Data.Length == 0 || caps.Data.Length % 2 != 0) throw new IOException("固件功能列表不完整。");
            var commands = new HashSet<int>();
            for (int i = 0; i < caps.Data.Length; i += 2) commands.Add((caps.Data[i] << 8) | caps.Data[i + 1]);
            var version = channel.Send(1017);
            return new DeviceInfo {
                Firmware = version.Status == 0x68 ? Encoding.UTF8.GetString(version.Data).TrimEnd('\0') : "未知版本",
                CanRead = commands.Contains(3004), CanWrite = commands.Contains(3005)
            };
        }

        public DeviceInfo Probe()
        {
            using (var channel = open()) return Inspect(channel);
        }

        private T InReader<T>(bool needsWrite, Func<IDeviceChannel, T> work)
        {
            using (var channel = open())
            {
                var device = Inspect(channel);
                if (!device.CanRead || (needsWrite && !device.CanWrite))
                    throw new IOException(needsWrite ? "当前固件缺少 Viking 读取或写入功能。" : "当前固件缺少 Viking 读取功能。");
                var mode = Expect(channel.Send(1002), 0x68, "读取工作模式");
                if (mode.Data.Length != 1 || mode.Data[0] > 1) throw new IOException("工作模式回复异常。");
                bool restore = mode.Data[0] != 1;
                try
                {
                    if (restore) Expect(channel.Send(1001, 1), 0x68, "切换读卡模式");
                    return work(channel);
                }
                finally
                {
                    if (restore)
                    {
                        try { Expect(channel.Send(1001, mode.Data[0]), 0x68, "恢复设备模式"); }
                        catch (Exception e) { log("提示：" + e.Message + " 可拔插 USB 恢复连接。"); }
                    }
                }
            }
        }

        private static string Scan(IDeviceChannel channel)
        {
            var reply = channel.Send(3004);
            if (reply.Status == 0x41) return null;
            Expect(reply, 0x40, "读取 Viking");
            if (reply.Data.Length != 4) throw new IOException("Viking 卡号长度异常，未采用此次结果。");
            return BitConverter.ToString(reply.Data).Replace("-", "");
        }

        private string ReadStable(IDeviceChannel channel, string expected, int attempts)
        {
            string previous = null;
            int matches = 0;
            for (int i = 0; i < attempts; i++)
            {
                string id = Scan(channel);
                if (id == null) continue;
                if (expected != null && id != expected)
                    throw new IOException("回读编号与源卡不一致，未验证成功。请只保留一张目标卡。");
                if (previous != null && id != previous)
                    throw new IOException("读到了不同编号，请移开其他卡片后重新读取。");
                previous = id;
                if (++matches >= 2) return id;
            }
            throw new IOException(previous == null ? "没有读到 Viking 卡。请单独贴近卡片，拨开金属钥匙并调整位置。" : "只读到一次，尚未得到稳定结果。请调整卡片位置后重试。");
        }

        public CardRecord Read()
        { return InReader(false, channel => CardRecord.Create(ReadStable(channel, null, 10))); }

        public void Verify(string expected)
        {
            expected = CardRecord.NormalizeId(expected);
            InReader(false, channel => ReadStable(channel, expected, 8));
        }

        public void WriteBlank(string expected, bool blankConfirmed)
        {
            expected = CardRecord.NormalizeId(expected);
            if (!blankConfirmed) throw new InvalidOperationException("请先确认已放好要写入的 T5577 空白卡，并移开源卡。");
            InReader(true, channel =>
            {
                // This is a source-card guard, not a chip-type detector. The user identifies the blank T5577.
                for (int i = 0; i < 2; i++)
                {
                    if (Scan(channel) != null)
                        throw new IOException("检测到已有 Viking 数据的卡。为避免覆盖源卡，本次未写入；请换上空白 T5577。已有卡可用 verify 命令核对。");
                }
                byte[] id = Enumerable.Range(0, 4).Select(i => Convert.ToByte(expected.Substring(i * 2, 2), 16)).ToArray();
                // Upstream VIKING_WRITE_TO_T55XX contract: ID | new password | known default passwords.
                // The firmware enables rewritable password protection, not irreversible lock bits.
                byte[] defaults = { 0x20, 0x20, 0x66, 0x66, 0x51, 0x24, 0x36, 0x48, 0x19, 0x92, 0x04, 0x27 };
                var payload = id.Concat(defaults).ToArray();
                log("正在写入 T5577，请保持卡片不动……");
                try
                {
                    Expect(channel.Send(3005, payload), 0x40, "写入命令");
                    log("写入命令已返回；正在独立回读两次验证……");
                    ReadStable(channel, expected, 10);
                }
                catch (Exception e)
                {
                    // Acknowledgement is not proof of a programmed tag. Never automatically retry a write.
                    throw new IOException("写入未得到完整验证，目标卡可能已被修改。请保持源卡移开，使用 verify 命令检查。\r\n" + e.Message, e);
                }
                return true;
            });
        }
    }
}
