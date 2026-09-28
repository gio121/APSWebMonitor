using System.Net;
using System.Net.Sockets;
using System.Text;
using ApsMonitor.Services;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { checks++; return; }
    throw new Exception("FAIL: expected rejection: " + name);
}
byte[] Text(string value) => Encoding.ASCII.GetBytes(value);
byte[] Reply(byte node, byte type, byte[] payload)
{
    var frame = ReprogrammingProtocol.Frame(1, type, payload);
    frame[4] = node;
    frame[^1] = (byte)(frame[^1] + node - 1);
    return frame;
}

Check(ReprogrammingProtocol.Crc16(Text("123456789")) == 0x31C3, "CRC16 XMODEM standard vector");
Check(ReprogrammingProtocol.Crc32(Text("123456789")) == 0xCBF43926, "CRC32 standard vector");
Check(Convert.ToHexString(ReprogrammingProtocol.Start(2, false, 2)) == "AA0F0002014A000002000000000008", "MetroMadrid start frame");
Check(ReprogrammingProtocol.Start(2, true, 1)[5] == 0x4B, "FPGA start code");
var block = ReprogrammingProtocol.Block(2, false, "C135KI", [1, 2, 3], 4, 0);
Check(block.AsSpan(14, 4).SequenceEqual(new byte[] { 1, 2, 3, 255 }), "last block FF padding");
Check(ReprogrammingProtocol.Block(2, true, "F22044", [1], 4, 1).AsSpan(14, 4).ToArray().All(b => b == 255), "terminal FF block");
var status = Reply(2, 0x4C, [1, 0, 1, 0, 0x80]);
Check(ReprogrammingProtocol.TryReadStatus(status, 2, out var index, out var code) && index == 1 && code == 0x80, "status decoding");
status[^1]++;
Check(!ReprogrammingProtocol.TryReadStatus(status, 2, out _, out _), "bad checksum rejected");
Check(!ReprogrammingProtocol.TryReadStatus([0xAA], 2, out _, out _), "truncated response rejected");
Reject(() => ReprogrammingProtocol.Start(2, false, 32768), "signed block limit");
Reject(() => ReprogrammingProtocol.Block(2, false, "bad", [1], 4, 0), "signature validation");
string hex = ":0200000400C03A\n:0400000001020304F2\n:00000001FF\n";
Check(FirmwareImage.Decode("control.h86", Text(hex), 0xC00000).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "H86 offset");
Reject(() => FirmwareImage.Decode("bad.hex", Text(hex.Replace("F2", "F3")), 0xC00000), "HEX checksum");
Reject(() => FirmwareImage.Decode("bad.hex", Text(hex.Replace(":00000001FF", "")), 0xC00000), "missing EOF");
Reject(() => FirmwareImage.Decode("bad.hex", Text(hex), 0xD00000), "address below flash");
Reject(() => FirmwareImage.Decode("bad.hex", Text(hex.Replace(":00000001FF", ":0400000001020304F2\n:00000001FF")), 0xC00000), "overlapping addresses");
Check(FirmwareImage.Decode("fpga.mcs", Text(":01000200AA53\n:00000001FF"), 0).SequenceEqual(new byte[] { 255, 255, 170 }), "sparse HEX filled FF");
Reject(() => FirmwareImage.Decode("secret.enc", [1], 0), "unsupported encrypted file");
ReprogrammingService.ValidateCommunicationsFiles([new("test.img", Text("abc")), new("test.img.md5", Text("900150983cd24fb0d6963f7d28e17f72  test.img"))]);
checks++;
ReprogrammingService.ValidateCommunicationsFiles([new("edf01.bin", [1]), new("sdf01.bin", [2])]);
checks++;
Reject(() => ReprogrammingService.ValidateCommunicationsFiles([new("test.img", [1])]), "missing MD5");
Reject(() => ReprogrammingService.ValidateCommunicationsFiles([new("test.img", [1]), new("test.img.md5", Text("bad"))]), "wrong MD5");
Reject(() => ReprogrammingService.ValidateCommunicationsFiles([new("../edf.bin", [1]), new("sdf.bin", [1])]), "path traversal");
Check(ReprogrammingProtocol.TryReadFileResponse(Reply(3, 0x83, [4, 0, 100, 1]), out var p) && p[2] == 100, "communications final status");

foreach (bool fpga in new[] { false, true })
{
    using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var service = new ReprogrammingService(new MonitorStateService(), new FakeUploader());
    var upload = service.UploadControlAsync(new("127.0.0.1", ((IPEndPoint)device.Client.LocalEndPoint!).Port), fpga, "C135KI", [1, 2, 3, 4, 5], 4);
    var start = await device.ReceiveAsync(timeout.Token);
    Check(start.Buffer[5] == (fpga ? 0x4B : 0x4A), "UDP start type");
    foreach (ushort requested in new ushort[] { 0, 0, 1, 2 })
    {
        await device.SendAsync(Reply(2, 0x4C, [1, 0, (byte)requested, 0, 0]), start.RemoteEndPoint, timeout.Token);
        var data = await device.ReceiveAsync(timeout.Token);
        Check(data.Buffer.SequenceEqual(ReprogrammingProtocol.Block(2, fpga, "C135KI", [1, 2, 3, 4, 5], 4, requested)), "requested/repeated/terminal block");
    }
    Check(!service.State.Success, "transfer is not yet success");
    await device.SendAsync(Reply(2, 0x4C, [1, 0, 2, 0, 1]), start.RemoteEndPoint, timeout.Token);
    await device.SendAsync(Reply(2, 0x4C, [1, 0, 2, 0, 0x80]), start.RemoteEndPoint, timeout.Token);
    await upload.WaitAsync(timeout.Token);
    Check(service.State.Success && !service.State.Running, "success only after equipment confirmation");
}
foreach (byte failure in new byte[] { 8, 16, 32, 64, 0x80 })
{
    using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var service = new ReprogrammingService(new MonitorStateService(), new FakeUploader());
    var upload = service.UploadControlAsync(new("127.0.0.1", ((IPEndPoint)device.Client.LocalEndPoint!).Port), false, "C135KI", [1], 4);
    var start = await device.ReceiveAsync(timeout.Token);
    await device.SendAsync(Reply(2, 0x4C, [1, 0, 0, 0, failure]), start.RemoteEndPoint, timeout.Token);
    await upload.WaitAsync(timeout.Token);
    Check(!service.State.Success && !service.State.Running, "device error or premature success rejected");
}
foreach (bool failUpload in new[] { false, true })
{
    using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var uploader = new FakeUploader { Fail = failUpload };
    var service = new ReprogrammingService(new MonitorStateService(), uploader);
    var upload = service.UploadCommunicationsAsync(new("127.0.0.1", ((IPEndPoint)device.Client.LocalEndPoint!).Port),
        22, "SHA256:test", [new("edf01.bin", [1]), new("sdf01.bin", [2])]);
    var start = await device.ReceiveAsync(timeout.Token);
    Check(start.Buffer[5] == 0x8B && start.Buffer[6] == 1, "SFTP handshake requested");
    static string Encode(string value) => new(value.Select(c => (char)(c ^ 1)).ToArray());
    byte[] login = [1, 0, .. Encoding.Latin1.GetBytes(Encode("password") + "\r\n" + Encode("user") + "\r\n"), 2];
    await device.SendAsync(Reply(3, 0x83, login), start.RemoteEndPoint, timeout.Token);
    if (!failUpload)
    {
        var end = await device.ReceiveAsync(timeout.Token);
        Check(uploader.Called && end.Buffer[6] == 4, "transfer-end only after successful SFTP upload");
        Check(!service.State.Success, "SFTP upload alone is not success");
        await device.SendAsync(Reply(3, 0x83, [4, 0, 100, 1]), end.RemoteEndPoint, timeout.Token);
    }
    await upload.WaitAsync(timeout.Token);
    Check(uploader.User == "user" && uploader.Password == "password", "device credentials decoded correctly");
    Check(service.State.Success == !failUpload, "SFTP failure/result propagation");
    if (failUpload) Check(device.Available == 0, "no commit sent after SFTP failure");
}
Console.WriteLine($"PASS: {checks} reprogramming checks (UDP loopback and simulated SFTP; no real device contacted).");

sealed class FakeUploader : ISftpFirmwareUploader
{
    public bool Fail { get; init; }
    public bool Called { get; private set; }
    public string? User { get; private set; }
    public string? Password { get; private set; }
    public Task UploadAsync(string address, int port, string user, string password, string fingerprint,
        IReadOnlyList<CommunicationsFile> files, Action<string, double> progress, CancellationToken token)
    {
        Called = true; User = user; Password = password;
        if (Fail) throw new IOException("Simulated SFTP failure");
        return Task.CompletedTask;
    }
}
