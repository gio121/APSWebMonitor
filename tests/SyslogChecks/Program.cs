using ApsMonitor.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var entries = SyslogParser.Parse("Dec  7 10:37:22 aps.local (dhcpcd)[290]: Failed: missing executable\r\nDec 17 10:37:22 aps.local kernel: usb error -71\n  continuation\n\n");
Check(entries.Count == 4, "Keep every line, including continuations and blank lines.");
Check(entries[0].Timestamp == "Dec  7 10:37:22", "Do not invent a year.");
Check(entries[0].Host == "aps.local" && entries[0].Process == "(dhcpcd)" && entries[0].Pid == "290", "Parse host, process and PID.");
Check(entries[0].Message == "Failed: missing executable", "Keep colons in the message.");
Check(entries[1].Process == "kernel" && entries[1].Pid == "", "PID is optional.");
Check(entries[2].Message == "  continuation" && entries[3].RawLine == "", "Preserve unstructured lines.");
Check(SyslogParser.Parse("").Count == 0, "Empty file.");
if (args.Length > 0)
{
    var lines = File.ReadAllLines(args[0]);
    var actual = SyslogParser.Parse(File.ReadAllText(args[0]));
    Check(actual.Count == lines.Length, "No lost lines in sample.");
    Check(actual.Select(e => e.RawLine).SequenceEqual(lines), "Preserve source order and content.");
    Check(actual[0].Process == "systemd-resolved" && actual[0].Pid == "206", "First sample record.");
    Check(actual.Any(e => e.Process == "ECNManager"), "Read application logs.");
    Console.WriteLine($"Sample: {actual.Count} lines; {actual.Count(e => e.Process.Length == 0)} unstructured lines retained.");
}
var iso = SyslogParser.Parse("<14>2026-06-30T23:59:59.123+02:00 aps.local ControlManager[308]: Ready, waiting")[0];
Check(iso.Date == new DateTime(2026, 6, 30) && iso.Process == "ControlManager" && iso.Pid == "308", "ISO priority and source date.");
Check(iso.Timestamp.EndsWith("+02:00") && iso.Message == "Ready, waiting", "Keep timezone and message.");
var csv = SyslogParser.Parse("2026-06-30,10:20:30,42,\"Message, with \"\"quotes\"\"\"")[0];
Check(SyslogParser.Parse("Date,Time,Code,Description\n2026-06-30,10:20:30,42,Ready").Count == 1, "Skip legacy CSV header only.");
var exported = SyslogParser.Parse("Fecha y hora,Equipo,Proceso,PID,Mensaje\n\"Dec 17 10:37:22\",\"aps.local\",\"kernel\",\"\",\"Message, details\"")[0];
Check(exported.Host == "aps.local" && exported.Process == "kernel" && exported.Message == "Message, details", "Reopen exported CSV.");
Check(csv.Process == "42" && csv.Message == "Message, with \"quotes\"", "Legacy CSV quoting.");
Check(SyslogParser.Parse("2026-06-30 10:20:30 42 Message with spaces")[0].Message == "Message with spaces", "Legacy whitespace format.");
Check(SyslogParser.MatchesDateRange(iso, iso.Date, iso.Date), "Inclusive date boundaries.");
Check(!SyslogParser.MatchesDateRange(iso, new DateTime(2026, 7, 1), null), "Exclude out-of-range dates.");
Check(SyslogParser.MatchesDateRange(entries[0], new DateTime(2026, 7, 1), null), "Keep entries without full dates like frmLog.");

using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, ApsLogClient.Port);
listener.Start();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var payload = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Dec 17 10:37:22 aps.local kernel: message\n", 1000)));
var sender = Task.Run(async () =>
{
    using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
    await peer.GetStream().WriteAsync(payload.AsMemory(0, 17), deadline.Token);
    await peer.GetStream().WriteAsync(payload.AsMemory(17), deadline.Token);
});
var received = await ApsLogClient.DownloadAsync("127.0.0.1", deadline.Token);
await sender;
Check(received.SequenceEqual(payload), "Receive fragmented TCP stream until EOF with no request frame.");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { await ApsLogClient.DownloadAsync("127.0.0.1", cancelled.Token); throw new Exception("Cancellation was ignored."); }
catch (OperationCanceledException) { }
Console.WriteLine("All parser, filter and TCP checks passed.");
