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
ReprogrammingService.ValidateCommunicationsFiles([new("edf01.gz", [1]), new("sdf01.gz", [2])]);
checks++;
ReprogrammingService.ValidateCommunicationsFiles([new("edf01.bin.gz", [1]), new("sdf01.bin.gz", [2])]);
checks++;
ReprogrammingService.ValidateCommunicationsFiles([new("archive.gz", [1, 2, 3])]);
checks++;
ReprogrammingService.ValidateCommunicationsFiles([new("test.gz", Text("abc")), new("test.gz.md5", Text("900150983cd24fb0d6963f7d28e17f72  test.gz"))]);
checks++;
Reject(() => ReprogrammingService.ValidateCommunicationsFiles([new("test.img", [1])]), "missing MD5");
Reject(() => ReprogrammingService.ValidateCommunicationsFiles([new("test.img", [1]), new("test.img.md5", Text("bad"))]), "wrong MD5");
Reject(() => ReprogrammingService.ValidateCommunicationsFiles([new("test.gz", [1]), new("test.gz.md5", Text("bad"))]), "wrong GZ MD5");
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

// Test status 1 (flash busy/erase) at start and between blocks
{
    using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var service = new ReprogrammingService(new MonitorStateService(), new FakeUploader());
    var upload = service.UploadControlAsync(new("127.0.0.1", ((IPEndPoint)device.Client.LocalEndPoint!).Port), false, "C135KI", [1, 2, 3, 4, 5], 4);
    var start = await device.ReceiveAsync(timeout.Token);
    Check(start.Buffer[5] == 0x4A, "UDP start type");

    // 1. Device reports flash erase busy (status 1) before sending block 0
    await device.SendAsync(Reply(2, 0x4C, [1, 0, 0, 0, 1]), start.RemoteEndPoint, timeout.Token);
    Check(!service.State.Success, "flash erase busy does not prematurely finish");

    // 2. Device requests block 0 (status 0)
    await device.SendAsync(Reply(2, 0x4C, [1, 0, 0, 0, 0]), start.RemoteEndPoint, timeout.Token);
    var data0 = await device.ReceiveAsync(timeout.Token);
    Check(data0.Buffer.SequenceEqual(ReprogrammingProtocol.Block(2, false, "C135KI", [1, 2, 3, 4, 5], 4, 0)), "block 0 sent after busy");

    // 3. Device reports flash write busy (status 2) between block 0 and block 1
    await device.SendAsync(Reply(2, 0x4C, [1, 0, 0, 0, 2]), start.RemoteEndPoint, timeout.Token);

    // 4. Device requests block 1 (status 4)
    await device.SendAsync(Reply(2, 0x4C, [1, 0, 1, 0, 4]), start.RemoteEndPoint, timeout.Token);
    var data1 = await device.ReceiveAsync(timeout.Token);
    Check(data1.Buffer.SequenceEqual(ReprogrammingProtocol.Block(2, false, "C135KI", [1, 2, 3, 4, 5], 4, 1)), "block 1 sent after busy status 2");

    // 5. Final FF block (block 2)
    await device.SendAsync(Reply(2, 0x4C, [1, 0, 2, 0, 0]), start.RemoteEndPoint, timeout.Token);
    var data2 = await device.ReceiveAsync(timeout.Token);
    Check(data2.Buffer.SequenceEqual(ReprogrammingProtocol.Block(2, false, "C135KI", [1, 2, 3, 4, 5], 4, 2)), "terminal block sent");

    // 6. Flash programming at end and success 0x80
    await device.SendAsync(Reply(2, 0x4C, [1, 0, 2, 0, 1]), start.RemoteEndPoint, timeout.Token);
    await device.SendAsync(Reply(2, 0x4C, [1, 0, 2, 0, 0x80]), start.RemoteEndPoint, timeout.Token);
    await upload.WaitAsync(timeout.Token);
    Check(service.State.Success && !service.State.Running, "success after flash busy transitions");
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

// Test direct SFTP upload without UDP handshake
{
    var uploader = new FakeUploader();
    var service = new ReprogrammingService(new MonitorStateService(), uploader);
    await service.UploadCommunicationsAsync(new("127.0.0.1", 50000), 22, "SHA256:test",
        [new("test.gz", [1, 2, 3])], sftpUser: "sepsa", sftpPassword: "thisisveryunsafepleasedeactivatemeaftersetup", remotePath: "/mnt/artifacts/update");
    Check(uploader.Called && uploader.User == "sepsa" && uploader.Password == "thisisveryunsafepleasedeactivatemeaftersetup" && uploader.RemotePath == "/mnt/artifacts/update",
        "direct SFTP upload without UDP handshake works and transmits credentials/remotePath");
    Check(service.State.Success, "direct SFTP upload marked success");
}

// Checks for 8A Reprogramming command
var req8A = ReprogrammingProtocol.FileCommand8ARequest();
Check(req8A.Length == 9, "8A request frame length");
Check(req8A[0] == 0xAA && req8A[5] == 0x8A && req8A[3] == 0x03 && req8A[4] == 0x01, "8A request header bytes");
Check(req8A[^1] == 0x41, "8A request checksum");

// Test TryRead8AResponse
byte[] msgBytes = Encoding.ASCII.GetBytes("Comenzamos la reprogramacion");
byte[] respOkPayload = [0x00, .. msgBytes]; // status 0 = starting
byte[] respOkFrame = Reply(3, 0x8A, respOkPayload);
Check(ReprogrammingProtocol.TryRead8AResponse(respOkFrame, out bool startingOk, out string msgOk) &&
      startingOk && msgOk == "Comenzamos la reprogramacion", "8A response OK parsing");

byte[] errMsgBytes = Encoding.ASCII.GetBytes("No hay archivos que reprogramar en /mnt/artifacts/update");
byte[] respErrPayload = [0x01, .. errMsgBytes]; // status 1 = error
byte[] respErrFrame = Reply(3, 0x8A, respErrPayload);
Check(ReprogrammingProtocol.TryRead8AResponse(respErrFrame, out bool startingErr, out string msgErr) &&
      !startingErr && msgErr == "No hay archivos que reprogramar en /mnt/artifacts/update", "8A response Error parsing");

// Bad checksum rejected
byte[] corruptedFrame = (byte[])respOkFrame.Clone();
corruptedFrame[^1]++;
Check(!ReprogrammingProtocol.TryRead8AResponse(corruptedFrame, out _, out _), "8A response bad checksum rejected");

// Truncated frame rejected
Check(!ReprogrammingProtocol.TryRead8AResponse([0xAA, 0x05, 0x00], out _, out _), "8A response truncated rejected");

// Test SendReprogramming8ACommandAsync via UDP loopback
{
    using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    int devicePort = ((IPEndPoint)device.Client.LocalEndPoint!).Port;
    var service = new ReprogrammingService(new MonitorStateService(), new FakeUploader());
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    var sendTask = service.SendReprogramming8ACommandAsync("127.0.0.1", devicePort, cts.Token);
    var received = await device.ReceiveAsync(cts.Token);
    Check(received.Buffer[0] == 0xAA && received.Buffer[5] == 0x8A, "8A command received on device loopback");

    await device.SendAsync(respOkFrame, received.RemoteEndPoint, cts.Token);
    var (success, resultMsg) = await sendTask;
    Check(success && resultMsg == "Comenzamos la reprogramacion", "SendReprogramming8ACommandAsync confirms success message");
}

Console.WriteLine($"PASS: {checks} reprogramming checks (UDP loopback and simulated SFTP; no real device contacted).");

sealed class FakeUploader : ISftpFirmwareUploader
{
    public bool Fail { get; init; }
    public bool Called { get; private set; }
    public string? User { get; private set; }
    public string? Password { get; private set; }
    public string? RemotePath { get; private set; }
    public Task UploadAsync(string address, int port, string user, string password, string fingerprint,
        IReadOnlyList<CommunicationsFile> files, Action<string, double> progress, CancellationToken token,
        string remotePath = "/mnt/artifacts/update")
    {
        Called = true; User = user; Password = password; RemotePath = remotePath;
        if (Fail) throw new IOException("Simulated SFTP failure");
        return Task.CompletedTask;
    }
}
