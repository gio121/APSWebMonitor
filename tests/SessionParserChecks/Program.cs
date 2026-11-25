using System.Text;
using ApsMonitor.Models;
using ApsMonitor.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var signals = Enumerable.Range(0, 10).Select(i => new Signal
{
    Id = i + 1, NodoNumero = 3, BytePosicion = i, TipoVariable = "UINT8", Escala = 1
}).ToList();
signals.Add(new Signal { Id = 20, NodoNumero = 2, BytePosicion = 19, TipoVariable = "UINT8", Escala = 1 });
byte[] Frame(byte type, int length, string name = "")
{
    var frame = new byte[length + 7];
    frame[0] = 0xAA;
    frame[1] = (byte)frame.Length;
    frame[3] = type is 0x1A or 0x1D or 0x2A ? (byte)3 : (byte)1;
    frame[4] = type is 0x1A or 0x1D or 0x2A ? (byte)1 : (byte)3;
    frame[5] = type;
    Encoding.ASCII.GetBytes(name).CopyTo(frame, 6);
    frame[^1] = (byte)frame.Take(frame.Length - 1).Sum(b => b);
    return frame;
}
var frames = new[] { Frame(0x1A, 8), Frame(0x12, 10, "B160KX"), Frame(0x1D, 8), Frame(0x15, 20), Frame(0x1A, 8), Frame(0x2A, 8) };
var text = string.Join("\n", frames.Select((f, i) => $"2026-10-29 09:50:36.{i * 100:000};{Convert.ToHexString(f)}"));
var parser = new SessionParserService();
var dataset = parser.Parse(Encoding.ASCII.GetBytes(text), signals)!;
Check(dataset.FrameCount == 2, "Polling requests must not become telemetry snapshots.");
Check(dataset.DeviceConnections[0] == (true, false), "Communications response only activates communications.");
Check(dataset.DeviceConnections[1] == (true, true), "Copy response activates control independently of its gateway source.");
var controlOnly = parser.Parse(Encoding.ASCII.GetBytes($"2026-10-29 09:50:36.000;{Convert.ToHexString(frames[3])}"), signals)!;
Check(controlOnly.DeviceConnections[0] == (false, true), "Control-only session must not invent communications responses.");
Check(dataset.DeviceConnections[0] == (true, false), "Seeking backwards restores the state before control responded.");
for (var row = 0; row < dataset.FrameCount; row++)
    Check(new string(Enumerable.Range(0, 6).Select(i => (char)dataset.RawValues[row, i]).ToArray()) == "B160KX",
        "Communications software must survive intervening requests/control responses.");
Check(parser.Parse(Encoding.ASCII.GetBytes($"2026-10-29 09:50:36.000;{Convert.ToHexString(frames[0])}"), signals) is null,
    "Request-only recordings must not fabricate telemetry.");

// Optional read-only regression against the reported recording.
if (args.Length > 0)
{
    var bytes = File.ReadAllBytes(args[0]);
    var lines = Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var recorded = lines.Select(line => line.Split(';')).Where(parts => parts.Length == 2)
        .Select(parts => Convert.FromHexString(parts[1].Replace(" ", "").Trim())).ToArray();
    var sizes = recorded.Where(f => f.Length >= 7 && f[5] is 0x12 or 0x15)
        .GroupBy(f => f[5]).ToDictionary(g => g.Key, g => g.Max(f => f.Length - 7));
    signals.RemoveAll(s => s.Id == 20);
    signals.Add(new Signal { Id = 20, NodoNumero = 2, BytePosicion = sizes[0x15] - 1, TipoVariable = "UINT8", Escala = 1 });
    signals.Add(new Signal { Id = 21, NodoNumero = 3, BytePosicion = sizes[0x12] - 1, TipoVariable = "UINT8", Escala = 1 });
    var actual = parser.Parse(bytes, signals)!;
    var names = Enumerable.Range(0, actual.FrameCount).Select(row => new string(Enumerable.Range(0, 6)
        .Select(i => (char)actual.RawValues[row, i]).ToArray())).ToArray();
    var first = Array.IndexOf(names, "B160KX");
    Check(first >= 0, "Expected software is present in the reported recording.");
    Check(names.Skip(first).All(name => name == "B160KX"), "Software must remain stable throughout the reported recording.");
    Check(Enumerable.Range(first, actual.FrameCount - first).All(row =>
        actual.RawValues[row, 8] == '0' && actual.RawValues[row, 9] == 'A'),
        "Communications version 0A must also remain stable throughout the recording.");
    Console.WriteLine($"Recording: {recorded.Length} frames, {actual.FrameCount} telemetry snapshots; B160KX / 0A remains stable.");
}
Console.WriteLine("PASS: session polling regression checks.");
