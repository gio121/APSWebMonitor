using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ApsMonitor.Data;
using ApsMonitor.Models;
using ApsMonitor.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); } catch (ArgumentException) { checks++; return; }
    throw new Exception(message);
}
var signal = new Signal { Id = 42, NodoNumero = 2, Tag = "TEST", TipoVariable = "UINT16" };
var modify = new ScadaCommand { FrameType = 0x6A, Subcommand = 0x1234, SignalId = 42, VariableValue = 513 };
var internalCommand = new ScadaCommand { Nombre = "Test", FrameType = 0x7A, Subcommand = 0x1234 };
byte[] frame6 = ControlCommandProtocol.Build(modify, signal);
Check(Convert.ToHexString(frame6) == "AA0B0002016A341201026B", "6A wire format, length, value, checksum");
byte[] frame7 = ControlCommandProtocol.Build(internalCommand, null);
Check(Convert.ToHexString(frame7) == "AA0F0002017A34120000000000007C", "7A wire format and zero padding");
Check(Convert.ToHexString(ControlCommandProtocol.EncodeValue("INT16", -2)) == "FEFF", "Signed value encoding");
Check(Convert.ToHexString(ControlCommandProtocol.EncodeValue("FLOAT32", 1.5)) == "0000C03F", "Float little endian");
Check(ControlCommandProtocol.EncodeValue("BCD_BYTE", 42)[0] == 0x42, "BCD value");
Reject(() => ControlCommandProtocol.EncodeValue("UINT8", 256), "Reject overflow");
Reject(() => ControlCommandProtocol.EncodeValue("UINT16", -1), "Reject negative unsigned value");
Reject(() => ControlCommandProtocol.EncodeValue("INT16", 1.5), "Reject integer truncation");
Reject(() => ControlCommandProtocol.EncodeValue("FLOAT32", double.NaN), "Reject non-finite");
Reject(() => ControlCommandProtocol.Build(modify, null), "6A requires associated signal");
signal.IsDeleted = true;
Reject(() => ControlCommandProtocol.Build(modify, signal), "Reject deleted signal");
signal.IsDeleted = false; signal.NodoNumero = 3;
Reject(() => ControlCommandProtocol.Build(modify, signal), "Reject communications signal");
signal.NodoNumero = 2;
Reject(() => ControlCommandProtocol.Build(new ScadaCommand(), null), "Legacy commands cannot be sent unconfigured");

string path = Path.Combine(Path.GetTempPath(), $"aps-commands-{Guid.NewGuid():N}.db");
try
{
    var services = new ServiceCollection();
    services.AddDbContextFactory<ApsDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False"));
    services.AddScoped<ApsDataService>();
    services.AddHttpClient();
    services.Configure<InfluxOptions>(o => o.Enabled = false);
    services.AddSingleton<InfluxWriterService>();
    services.AddSingleton<MonitorStateService>();
    using var provider = services.BuildServiceProvider();
    using (var db = provider.GetRequiredService<IDbContextFactory<ApsDbContext>>().CreateDbContext())
    {
        db.Database.EnsureCreated();
        db.Commands.Add(new ScadaCommand { Nombre = "Legacy", CommandValue = "CMD_OLD" });
        db.SaveChanges();
        db.Database.ExecuteSqlRaw("ALTER TABLE Commands DROP COLUMN FrameType");
        db.Database.ExecuteSqlRaw("ALTER TABLE Commands DROP COLUMN Subcommand");
        db.Database.ExecuteSqlRaw("ALTER TABLE Commands DROP COLUMN SignalId");
        db.Database.ExecuteSqlRaw("ALTER TABLE Commands DROP COLUMN VariableValue");
        CommandSchema.EnsureControlCommands(db);
        CommandSchema.EnsureControlCommands(db);
    }
    using var scope = provider.CreateScope();
    var data = scope.ServiceProvider.GetRequiredService<ApsDataService>();
    signal.Id = 0;
    await data.AddSignalAsync(signal);
    modify.SignalId = signal.Id;
    await data.AddCommandAsync(modify);
    var storedCommands = await data.GetCommandsAsync();
    Check(storedCommands.Single(c => c.CommandValue == "CMD_OLD").FrameType is null, "Legacy commands preserved without assigning an executable frame");
    var stored = storedCommands.Single(c => c.FrameType == 0x6A);
    Check(stored.FrameType == 0x6A && stored.Subcommand == 0x1234 && stored.VariableValue == 513 && stored.SignalId == signal.Id, "Command schema upgrade and persistence");

    using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var received = new ConcurrentQueue<byte[]>();
    int polls = 0;
    var firstPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var simulation = Task.Run(async () =>
    {
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var packet = await device.ReceiveAsync(timeout.Token);
                if (packet.Buffer[5] is 0x6A or 0x7A) received.Enqueue(packet.Buffer);
                else
                {
                    Interlocked.Increment(ref polls);
                    var reply = ReprogrammingProtocol.Frame(1, 0x12, [0, 0]);
                    reply[4] = 3; reply[^1] += 2;
                    await device.SendAsync(reply, packet.RemoteEndPoint, timeout.Token);
                    firstPoll.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) { }
    });
    var monitor = provider.GetRequiredService<MonitorStateService>();
    monitor.IpAddress = "127.0.0.1";
    monitor.Port = ((IPEndPoint)device.Client.LocalEndPoint!).Port.ToString();
    Check(!await monitor.SendControlCommandAsync(internalCommand), "Disconnected send blocked");
    await monitor.ConnectAsync();
    Check(!await monitor.SendControlCommandAsync(internalCommand), "Connected without monitoring blocked");
    monitor.StartMonitoring();
    await firstPoll.Task.WaitAsync(timeout.Token);
    Check(await monitor.SendControlCommandAsync(internalCommand), "7A UDP send while monitoring");
    Check(await monitor.SendControlCommandAsync(modify), "6A UDP send while monitoring");
    int pollCount = polls;
    while (received.Count < 2 || polls <= pollCount) await Task.Delay(20, timeout.Token);
    Check(monitor.IsMonitoring, "Monitoring remains active after sending");
    monitor.StopMonitoring();
    await monitor.WaitForMonitoringStoppedAsync();
    Check(!await monitor.SendControlCommandAsync(internalCommand), "Stopped monitoring blocks command");
    Check(received.Count == 2 && received.Any(f => f.SequenceEqual(frame6)) && received.Any(f => f.SequenceEqual(frame7)), "Exactly one datagram per configured command");
    Check(monitor.TryBeginReprogramming(), "Acquire reprogramming state");
    Check(!await monitor.SendControlCommandAsync(modify), "Reprogramming blocks command");
    monitor.EndReprogramming();
    monitor.Disconnect();
    timeout.Cancel();
    await simulation;
    Console.WriteLine($"PASS: {checks} control command checks (local UDP only).");
}
finally { if (File.Exists(path)) File.Delete(path); }
