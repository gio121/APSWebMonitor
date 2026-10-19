using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using ApsMonitor.Services;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
string Value(string key, string type, string info = "", double scale = 1, double offset = 0) => $"""
    <value_element_t><keyname>{key}</keyname><description><localized_string_t><langid>es</langid><stringvalue>{key} ES</stringvalue></localized_string_t></description>
    <value><valtype>{type}</valtype><scale>{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}</scale><offset>{offset}</offset></value>
    <info><min>1</min><max>4294967294</max>{info}</info></value_element_t>
    """;
string Ref(string key, int pos) => $"<valuereference_element_t><keyname>{key}</keyname><position>{pos}</position></valuereference_element_t>";
string Label(int number, string text) => $"<info_value_element_t><value>{number}</value><value_description><localized_string_t><langid>es</langid><stringvalue>{text}</stringvalue></localized_string_t></value_description></info_value_element_t>";
var xml = $"""
    <root><node_t><pte_address_node_number>2</pte_address_node_number><sw><signature>TEST01</signature></sw><value_elements>
    {Value("failReg", "ULONG")}{Value("timedat.seconds", "UINT")}
    {Value("cfails_ac", "UINT", "<type>VALUE</type><info_elements>" + Label(7, "Fallo de prueba") + "</info_elements>")}
    {Value("status", "UINT", "<type>BITFIELD</type><info_elements>" + Label(0, "Marcha") + Label(15, "Fallo") + "</info_elements>")}
    {Value("signed", "INT", "<units>V</units>", 0.5, 1)}
    </value_elements><event_trace><event_download><downloadType>FLASH</downloadType><startAddress>0</startAddress><endAddress>142</endAddress><blockSize>64</blockSize></event_download>
    <event_start_address>6</event_start_address><num_event_registers>2</num_event_registers><event_size>68</event_size><eventdata>
    <data_block_t><traceNameDataSection>Failure Data</traceNameDataSection><data_type>DATA</data_type><start_position_on_event_register>0</start_position_on_event_register><trace_register_size>32</trace_register_size><num_trace_registers>1</num_trace_registers><tracevars>{Ref("failReg", 0)}{Ref("timedat.seconds", 4)}{Ref("cfails_ac", 16)}{Ref("status", 18)}{Ref("signed", 20)}</tracevars></data_block_t>
    <data_block_t><traceNameDataSection>Microcontroller trace</traceNameDataSection><data_type>TRACE</data_type><start_position_on_event_register>32</start_position_on_event_register><trace_register_size>4</trace_register_size><num_trace_registers>3</num_trace_registers><tracevars>{Ref("signed", 0)}{Ref("status", 2)}</tracevars></data_block_t>
    <data_block_t><traceNameDataSection>FPGA trace</traceNameDataSection><data_type>TRACE</data_type><start_position_on_event_register>44</start_position_on_event_register><trace_register_size>4</trace_register_size><num_trace_registers>6</num_trace_registers><tracevars /></data_block_t>
    </eventdata><FPGA_ANALOG><FPGA_element_t><keyname>signed</keyname><elementIndex>0</elementIndex><mask1>0</mask1><mask2>0</mask2></FPGA_element_t></FPGA_ANALOG>
    <FPGA_STATUS>{Ref("status", 0)}</FPGA_STATUS></event_trace></node_t></root>
    """;
var config = ControlEventsParser.LoadConfig(Encoding.UTF8.GetBytes(xml)).Single();
var mixed = XDocument.Parse(xml);
mixed.Root!.Add(new XElement("node_t", new XElement("event_trace",
    new XElement("num_event_registers", 495), new XElement("eventdata", new XElement("data_block_t",
        new XElement("start_position_on_event_register", 0), new XElement("tracevars", new XElement("valuereference_element_t", new XElement("keyname", "log_number"))))))));
Check(ControlEventsParser.LoadConfig(mixed).Count == 1, "Import communications logs alongside control without treating them as binary control registers.");
var dat = Enumerable.Repeat((byte)0xFF, 142).ToArray();
Encoding.ASCII.GetBytes("TEST01").CopyTo(dat, 0);
dat.AsSpan(6, 68).Clear();
BinaryPrimitives.WriteUInt32LittleEndian(dat.AsSpan(6), 42);
byte[] date = [0x56, 0x34, 0x12, 0x19, 0x07, 0x26];
for (int i = 0; i < date.Length; i++) dat[10 + i * 2] = date[i];
BinaryPrimitives.WriteUInt16LittleEndian(dat.AsSpan(22), 7);
BinaryPrimitives.WriteUInt16LittleEndian(dat.AsSpan(24), 0x8001);
BinaryPrimitives.WriteInt16LittleEndian(dat.AsSpan(26), -12);
for (int i = 0; i < 3; i++) { BinaryPrimitives.WriteInt16LittleEndian(dat.AsSpan(38 + i * 4), (short)(i * 2 - 4)); dat[40 + i * 4] = (byte)i; }
void Fpga(int index, int time, byte tag, ushort value)
{
    int pos = 50 + index * 4;
    BinaryPrimitives.WriteUInt16LittleEndian(dat.AsSpan(pos), (ushort)((tag << 8) | time));
    BinaryPrimitives.WriteUInt16LittleEndian(dat.AsSpan(pos + 2), value);
}
Fpga(0, 254, 0x81, 1); Fpga(1, 254, 0x01, 0xFFFC);
Fpga(2, 255, 0x81, 1); Fpga(3, 255, 0x01, 2);
Fpga(4, 0, 0x81, 1); Fpga(5, 0, 0x01, 4);
Check(ControlEventsParser.MatchConfig(dat, [config]) == config, "Match six-byte signature.");
var events = ControlEventsParser.ReadEvents(dat, config);
Check(events.Count == 2 && events[0].Number == 42 && !events[1].IsValid, "Preserve ring slots and reject erased records.");
Check(events[0].UtcTime == new DateTime(2026, 7, 19, 12, 34, 56, DateTimeKind.Utc), "Decode RTC BCD in low bytes of words.");
var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
Check(ControlEventsParser.FormatTime(events[0].UtcTime, madrid) == "19/07/2026 14:34:56", "Apply daylight saving to UTC RTC date.");
Check(events[0].Failures == "Fallo de prueba", "Decode localized failure enumeration.");
var details = ControlEventsParser.ReadDetails(dat, config, events[0], madrid);
Check(details.Single(d => d.Key == "signed" && !d.IsTrace).Value == "-5", "Signed little-endian scaling and offset.");
Check(details.Single(d => d.Key == "signed" && d.IsTrace).Value == "1", "Detail uses last trace sample.");
Check(details.Single(d => d.Key == "status" && !d.IsTrace).Bits.Count(b => b.Active) == 2, "Expand low and high digital bits.");
var trace = ControlEventsParser.ReadTraces(dat, config, events[0], 1).Single(s => s.Key == "signed");
Check(trace.Y.SequenceEqual([-1.0, 0.0, 1.0]) && trace.X.SequenceEqual([0.0, 1.0, 2.0]), "Trace samples keep order and physical scale.");
Check(ControlEventsParser.ReadTraces(dat, config, events[0], 1).Single(s => s.Key == "status[0]").Y.SequenceEqual([0.0, 1.0, 0.0]), "Graph individual microcontroller bits.");
var fpga = ControlEventsParser.ReadTraces(dat, config, events[0], 2).Single(s => s.Key == "signed");
Check(fpga.Y.SequenceEqual([-1.0, 2.0, 3.0]) && fpga.X.SequenceEqual([254.0, 255.0, 256.0]), "FPGA word order, signed conversion and clock rollover.");
Check(ControlEventsParser.ReadTraces(dat, config, events[0], 2).Single(s => s.Key == "status[0]").Y.All(v => v == 1), "Graph FPGA status bits.");
var badTime = dat.ToArray(); badTime[10] = 0xFA;
Check(ControlEventsParser.ReadEvents(badTime, config)[0].UtcTime is null, "Invalid BCD dates remain unknown.");
foreach (int length in new[] { 0, 5, 141, 143 }) Reject(() => ControlEventsParser.MatchConfig(new byte[length], [config]), "Reject empty, truncated and oversized DAT.");
var truncated = dat[..^1]; Reject(() => ControlEventsParser.MatchConfig(truncated, [config]), "Reject a matching but truncated DAT.");
var mismatch = dat.ToArray(); mismatch[0] = (byte)'X'; Reject(() => ControlEventsParser.MatchConfig(mismatch, [config]), "Reject wrong equipment signature.");
Reject(() => ControlEventsParser.LoadConfig(Encoding.UTF8.GetBytes(xml.Replace("<position>20</position>", "<position>32</position>"))), "Reject overflowing signal reference.");
Reject(() => ControlEventsParser.LoadConfig(Encoding.UTF8.GetBytes(xml.Replace("<event_size>68", "<event_size>60"))), "Reject overflowing trace block.");

byte[] Reply(int address, int size, byte[] source)
{
    var payload = new byte[14 + size];
    Encoding.ASCII.GetBytes(config.Signature).CopyTo(payload, 0); payload[7] = 2;
    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), address);
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), (ushort)size);
    source.AsSpan(address, size).CopyTo(payload.AsSpan(14));
    var frame = ReprogrammingProtocol.Frame(1, 0x54, payload); frame[4] = config.Node;
    frame[^1] = unchecked((byte)frame.AsSpan(0, frame.Length - 1).ToArray().Sum(b => b));
    return frame;
}
var request = ApsControlEventsClient.Request(config, 0, 64);
Check(Convert.ToHexString(request) == "AA0F0002015C00020000000040005A", "Golden FLASH request matches legacy SEPSA layout and checksum.");
Check(ApsControlEventsClient.Request(config with { DownloadType = "RAM" }, 0, 64)[7] == 1, "RAM download subcommand.");
var reply = Reply(0, 64, dat);
Check(ApsControlEventsClient.TryReadBlock(reply, config, 0, 64, out var received) && received.SequenceEqual(dat[..64]), "Read reply memory payload.");
foreach (int pos in new[] { 0, 1, 3, 4, 5, 12, 13, 14, 18, reply.Length - 1 })
{
    var invalid = reply.ToArray(); invalid[pos] ^= 1;
    Check(!ApsControlEventsClient.TryReadBlock(invalid, config, 0, 64, out _), $"Reject corrupt reply at {pos}.");
}
Check(!ApsControlEventsClient.TryReadBlock(reply, config, 64, 64, out _), "Ignore stale address.");
using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
int requests = 0;
var peer = Task.Run(async () =>
{
    int total = 0;
    while (total < dat.Length)
    {
        var rx = await server.ReceiveAsync(deadline.Token); requests++;
        if (requests == 1) continue; // Exercise an actual retry.
        int address = BinaryPrimitives.ReadInt32LittleEndian(rx.Buffer.AsSpan(8));
        int size = BinaryPrimitives.ReadUInt16LittleEndian(rx.Buffer.AsSpan(12));
        if (address > 0) await server.SendAsync(Reply(0, 64, dat), rx.RemoteEndPoint, deadline.Token);
        var corrupt = Reply(address, size, dat); corrupt[^1] ^= 1;
        await server.SendAsync(corrupt, rx.RemoteEndPoint, deadline.Token);
        await server.SendAsync(Reply(address, size, dat), rx.RemoteEndPoint, deadline.Token);
        total += size;
    }
});
var progress = new List<double>();
var downloaded = await ApsControlEventsClient.DownloadAsync("127.0.0.1", ((IPEndPoint)server.Client.LocalEndPoint!).Port, config, progress.Add, deadline.Token);
await peer;
Check(downloaded.SequenceEqual(dat) && requests == 4 && progress.Last() == 100, "Download all bytes with retry, stale/corrupt packets and short final block.");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await ApsControlEventsClient.DownloadAsync("127.0.0.1", 12345, config, cancellationToken: cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { checks++; }
if (args.Length > 0)
{
    var actual = ControlEventsParser.LoadConfig(File.ReadAllBytes(args[0]));
    var control = actual.Single(c => c.Node == 2);
    Check(control.Signature == "N200KI" && control.EventCount == 50 && control.EventSize == 24576 && control.EventStart == 16384, "Read original QT8802_MONMDx CMFX.");
    Check(control.Blocks.Count == 3 && control.Blocks[1].RegisterCount == 200 && control.Blocks[2].RegisterCount == 3072, "Original microcontroller and FPGA geometry.");
    var sample = new byte[control.DownloadEnd]; Encoding.ASCII.GetBytes(control.Signature).CopyTo(sample, 0);
    Check(ControlEventsParser.ReadEvents(sample, control).All(e => !e.IsValid), "Original empty image has no invented events.");
    BinaryPrimitives.WriteUInt32LittleEndian(sample.AsSpan(control.EventStart), 2);
    var realEntry = ControlEventsParser.ReadEvents(sample, control).First(e => e.IsValid);
    Check(ControlEventsParser.ReadDetails(sample, control, realEntry, madrid).Count > 60, "Read real CMFX event detail.");
    Check(ControlEventsParser.ReadTraces(sample, control, realEntry, 1).All(s => s.Y.Length == 200), "Read original 200-point microcontroller traces.");
    Console.WriteLine($"Reference: {control.Signature}, {control.EventCount} slots, {control.Values.Count} values, {control.FpgaReferences.Count} FPGA references.");
}
await IntegrationChecks.RunAsync(config, dat);
Console.WriteLine($"PASS: {checks} control event parser and UDP checks.");
