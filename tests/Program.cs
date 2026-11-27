// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VikingTool;

class Fake : IDeviceChannel
{
    public Queue<string> Scans = new Queue<string>();
    public int Writes, Mode;
    public bool CanWrite = true, WriteTimeout;
    public byte[] Payload;
    public Reply Send(int command, params byte[] data)
    {
        if (command == 1033) return new Reply(0x68, 0);
        if (command == 1035) return new Reply(0x68, CanWrite ? new byte[] { 0x0B, 0xBC, 0x0B, 0xBD } : new byte[] { 0x0B, 0xBC });
        if (command == 1017) return new Reply(0x68, Encoding.ASCII.GetBytes("test"));
        if (command == 1002) return new Reply(0x68, (byte)Mode);
        if (command == 1001) { Mode = data[0]; return new Reply(0x68); }
        if (command == 3005)
        {
            Writes++; Payload = data;
            if (WriteTimeout) throw new TimeoutException("simulated");
            return new Reply(0x40);
        }
        if (command == 3004)
        {
            string id = Scans.Count > 0 ? Scans.Dequeue() : null;
            if (id == null) return new Reply(0x41);
            byte[] value = new byte[id.Length / 2];
            for (int i = 0; i < value.Length; i++) value[i] = Convert.ToByte(id.Substring(i * 2, 2), 16);
            return new Reply(0x40, value);
        }
        throw new Exception("Unexpected command");
    }
    public void Dispose() { }
}

class Tests
{
    static int passed;
    static void Check(bool value, string description) { if (!value) throw new Exception(description); }
    static void Fails(Action action, string contains)
    {
        try { action(); } catch (Exception e) { Check(e.Message.Contains(contains), e.ToString()); return; }
        throw new Exception("Expected failure: " + contains);
    }
    static VikingService Service(Fake fake) { return new VikingService(() => fake, delegate { }); }
    static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    static int Main()
    {
        Test("stable read tolerates missed polls and restores mode", () => {
            var f = new Fake(); f.Scans = new Queue<string>(new[] { null, "12345678", null, "12345678" });
            Check(Service(f).Read().id == "12345678" && f.Mode == 0 && f.Writes == 0, "read result");
        });
        Test("mixed cards rejected", () => {
            var f = new Fake(); f.Scans = new Queue<string>(new[] { "12345678", "87654321" });
            Fails(() => Service(f).Read(), "不同编号"); Check(f.Mode == 0, "mode restoration");
        });
        Test("no-card read cannot return saved data", () => {
            var f = new Fake(); Fails(() => Service(f).Read(), "没有读到"); Check(f.Mode == 0, "restored");
        });
        Test("write acknowledgement alone is not success", () => {
            var f = new Fake(); Fails(() => Service(f).WriteBlank("12345678", true), "未得到完整验证");
            Check(f.Writes == 1 && f.Mode == 0, "must not auto retry");
        });
        Test("matching independent readback verifies write", () => {
            var f = new Fake(); f.Scans = new Queue<string>(new[] { null, null, "12345678", "12345678" });
            Service(f).WriteBlank("12345678", true);
            Check(f.Writes == 1 && f.Mode == 0 && f.Payload.Length == 16, "verified write");
        });
        Test("wrong readback rejects write", () => {
            var f = new Fake(); f.Scans = new Queue<string>(new[] { null, null, "87654321" });
            Fails(() => Service(f).WriteBlank("12345678", true), "不一致"); Check(f.Writes == 1, "one write");
        });
        Test("source-card presence prevents any write", () => {
            var f = new Fake(); f.Scans.Enqueue("12345678");
            Fails(() => Service(f).WriteBlank("12345678", true), "未写入"); Check(f.Writes == 0, "source protected");
        });
        Test("target confirmation required before opening channel", () => {
            var service = new VikingService(() => { throw new Exception("should not open"); }, null);
            Fails(() => service.WriteBlank("12345678", false), "空白卡");
        });
        Test("unsupported firmware cannot write", () => {
            var f = new Fake { CanWrite = false }; Fails(() => Service(f).WriteBlank("12345678", true), "缺少");
            Check(f.Writes == 0, "blocked");
        });
        Test("write timeout reported as uncertain and never retried", () => {
            var f = new Fake { WriteTimeout = true }; Fails(() => Service(f).WriteBlank("12345678", true), "可能已被修改");
            Check(f.Writes == 1 && f.Mode == 0, "single write + restore");
        });
        Test("verification is read-only", () => {
            var f = new Fake { Mode = 1 }; f.Scans = new Queue<string>(new[] { "12345678", "12345678" });
            Service(f).Verify("12345678"); Check(f.Writes == 0 && f.Mode == 1, "no writes, original mode preserved");
        });
        Test("frame rejects checksum corruption and mismatched command", () => {
            var bytes = Wire.Frame(3004, new byte[] { 1, 2, 3, 4 });
            Check(Wire.Decode(3004, bytes).Data.Length == 4, "valid frame");
            Fails(() => Wire.Decode(3005, bytes), "匹配"); bytes[10] ^= 1;
            Fails(() => Wire.Decode(3004, bytes), "匹配");
        });
        Test("card files round-trip and reject wrong protocol", () => {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try {
                CardRecord.Create("1234abcd").Save(path); Check(CardRecord.Load(path).id == "1234ABCD", "round trip");
                File.WriteAllText(path, "{\"version\":1,\"protocol\":\"MIFARE\",\"frequencyKhz\":125,\"id\":\"12345678\"}");
                Fails(() => CardRecord.Load(path), "请选择");
            } finally { File.Delete(path); }
            Fails(() => CardRecord.NormalizeId("12345678\n"), "8 位");
        });
        Test("legacy Framework card JSON remains compatible", () => {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try {
                File.WriteAllText(path, "{\"version\":1,\"protocol\":\"Viking\",\"frequencyKhz\":125,\"id\":\"1234abcd\",\"readAtUtc\":\"2026-01-01T00:00:00.0000000Z\"}");
                var card = CardRecord.Load(path);
                Check(card.id == "1234ABCD" && card.readAtUtc == "2026-01-01T00:00:00.0000000Z", "legacy input");
            } finally { File.Delete(path); }
        });
        Console.WriteLine("All " + passed + " checks passed.");
        return 0;
    }
}
