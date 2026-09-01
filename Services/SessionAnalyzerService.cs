using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace ApsMonitor.Services;

public sealed class SessionAnalyzerService
{
    public List<AnalyzerValueElement> LoadConfig(byte[] content)
    {
        using var stream = new MemoryStream(content);
        var doc = XDocument.Load(stream);
        var result = new List<AnalyzerValueElement>();
        var sizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ASCII_BYTE"] = 1, ["BYTE"] = 1, ["BCD_BYTE"] = 1, ["SBYTE"] = 1,
            ["UINT"] = 2, ["INT"] = 2, ["ULONG"] = 4
        };

        foreach (var node in doc.Descendants("node_t"))
        {
            _ = int.TryParse(node.Element("pte_address_node_number")?.Value, out var nodeAddress);
            var signature = node.Element("sw")?.Element("signature")?.Value?.Trim() ?? "";
            var definitions = node.Element("value_elements")?.Elements("value_element_t")
                .Where(x => !string.IsNullOrWhiteSpace(x.Element("keyname")?.Value))
                .GroupBy(x => x.Element("keyname")!.Value.Trim()).ToDictionary(x => x.Key, x => x.First());
            var frames = node.Descendants("frame_t").ToList();
            if (definitions is not null && frames.Count > 0)
            {
                foreach (var frame in frames)
                foreach (var reference in frame.Descendants("valuereference_element_t"))
                {
                    var key = reference.Element("keyname")?.Value?.Trim() ?? "";
                    if (definitions.TryGetValue(key, out var value) && int.TryParse(reference.Element("position")?.Value, out var position))
                        result.Add(CreateElement(value, nodeAddress, position, signature));
                }
                continue;
            }

            var configuredOffset = 0;
            var realOffset = 0;
            foreach (var value in node.Element("value_elements")?.Elements("value_element_t") ?? Enumerable.Empty<XElement>())
            {
                var type = value.Element("value")?.Element("valtype")?.Value?.Trim() ?? "BYTE";
                var extra = nodeAddress == 2
                    ? (configuredOffset > 98 ? 2 : 0) + (configuredOffset > 140 ? 2 : 0) + (configuredOffset > 154 ? 1 : 0) + (configuredOffset > 183 ? 1 : 0)
                    : 0;
                result.Add(CreateElement(value, nodeAddress, realOffset + extra, signature));
                var size = sizes.TryGetValue(type, out var found) ? found : 1;
                var realSize = configuredOffset < 28 ? 1 : size;
                configuredOffset += size;
                realOffset += realSize;
            }
        }
        return result.GroupBy(x => (x.NodeAddress, x.KeyName)).Select(x => x.First()).ToList();
    }

    public (DateTime Min, DateTime Max) GetDateRange(byte[] dat, int offsetHours = -1)
    {
        var min = DateTime.MaxValue; var max = DateTime.MinValue;
        foreach (var line in ReadLines(dat))
        {
            var parts = line.Split(';');
            if (parts.Length < 2 || !DateTime.TryParse(parts[0].Trim(), out var date)) continue;
            date = date.AddHours(offsetHours);
            if (date < min) min = date;
            if (date > max) max = date;
        }
        return min == DateTime.MaxValue ? (DateTime.Now, DateTime.Now) : (min, max);
    }

    public AnalyzerResult Analyze(IEnumerable<AnalyzerSession> sessions, List<AnalyzerValueElement> elements)
    {
        var result = new AnalyzerResult();
        foreach (var session in sessions.Where(x => x.SelectedKeys.Count > 0))
        {
            var chosen = elements.Where(x => session.SelectedKeys.Contains(x.KeyName)).ToList();
            var rows = ParseDat(session.Content, chosen, session.RangeFrom, session.RangeTo, session.TimeOffsetHours);
            var keys = new List<string>();
            foreach (var key in session.SelectedKeys)
            {
                if (session.DigitalBits.TryGetValue(key, out var bits) && bits.Count > 0)
                {
                    var names = chosen.FirstOrDefault(x => x.KeyName == key)?.BitNames;
                    foreach (var bit in bits.OrderBy(x => x)) keys.Add($"{key} [{(names?.TryGetValue(bit, out var name) == true ? name : $"Bit {bit}")}]");
                }
                else keys.Add(key);
            }
            foreach (var row in rows)
            foreach (var item in session.DigitalBits)
            if (row.Values.TryGetValue(item.Key, out var raw))
            {
                var names = chosen.FirstOrDefault(x => x.KeyName == item.Key)?.BitNames;
                foreach (var bit in item.Value)
                    row.Values[$"{item.Key} [{(names?.TryGetValue(bit, out var name) == true ? name : $"Bit {bit}")}]"] = ((int)raw >> bit) & 1;
                row.Values.Remove(item.Key);
            }
            result.Sessions.Add(new AnalyzerResultSession { Name = session.Name, Keys = keys, Rows = rows });
        }
        return result;
    }

    private static List<AnalyzerRow> ParseDat(byte[] content, List<AnalyzerValueElement> elements, DateTime from, DateTime to, int offsetHours)
    {
        var byNode = elements.GroupBy(x => x.NodeAddress).ToDictionary(x => x.Key, x => x.ToList());
        var last = new Dictionary<string, double>(); var keys = elements.Select(x => x.KeyName).Distinct().ToList(); var rows = new List<AnalyzerRow>();
        foreach (var line in ReadLines(content))
        {
            var parts = line.Split(';');
            if (parts.Length < 2 || !DateTime.TryParse(parts[0].Trim(), out var time)) continue;
            time = time.AddHours(offsetHours); if (time < from || time > to) continue;
            byte[] data; try { data = Convert.FromHexString(parts[1].Trim().Replace(" ", "")); } catch { continue; }
            if (data.Length < 6) continue;
            var node = data.Length > 2 && data[2] != 0 ? data[2] : data.Length > 4 && data[4] != 0 ? data[4] : -1;
            var payload = data[6..];
            var applicable = byNode.TryGetValue(node, out var nodeElements) ? nodeElements : node == -1 ? elements.Where(x => Matches(payload, x.Signature)).ToList() : [];
            var values = new Dictionary<string, double>();
            foreach (var value in applicable)
            {
                if (value.ByteOffset + TypeSize(value.ValType) > payload.Length) continue;
                var raw = ReadValue(payload, value.ByteOffset, value.ValType);
                if ((value.ValType.Equals("UINT", StringComparison.OrdinalIgnoreCase) && raw == ushort.MaxValue) || (value.ValType.Equals("ULONG", StringComparison.OrdinalIgnoreCase) && raw == uint.MaxValue)) continue;
                values[value.KeyName] = raw * value.Scale + value.Offset; last[value.KeyName] = values[value.KeyName];
            }
            foreach (var key in keys) if (!values.ContainsKey(key) && last.TryGetValue(key, out var prior)) values[key] = prior;
            if (values.Count > 0) rows.Add(new AnalyzerRow { Timestamp = time, Values = values });
        }
        return rows;
    }

    private static IEnumerable<string> ReadLines(byte[] content) => Encoding.ASCII.GetString(content).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    private static bool Matches(byte[] payload, string signature) => string.IsNullOrWhiteSpace(signature) || (payload.Length >= signature.Length && Encoding.ASCII.GetString(payload, 0, signature.Length) == signature);
    private static int TypeSize(string type) => type.ToUpperInvariant() switch { "UINT" or "INT" => 2, "ULONG" => 4, _ => 1 };
    private static double ReadValue(byte[] data, int i, string type) => type.ToUpperInvariant() switch { "SBYTE" => (sbyte)data[i], "BCD_BYTE" => (data[i] >> 4) * 10 + (data[i] & 15), "UINT" => BitConverter.ToUInt16(data, i), "INT" => BitConverter.ToInt16(data, i), "ULONG" => BitConverter.ToUInt32(data, i), _ => data[i] };
    private static AnalyzerValueElement CreateElement(XElement value, int node, int position, string signature)
    {
        var result = new AnalyzerValueElement { KeyName = value.Element("keyname")?.Value?.Trim() ?? "", ValType = value.Element("value")?.Element("valtype")?.Value?.Trim() ?? "BYTE", Scale = Number(value.Element("value")?.Element("scale")?.Value, 1), Offset = Number(value.Element("value")?.Element("offset")?.Value, 0), NodeAddress = node, ByteOffset = position, Signature = signature };
        foreach (var info in value.Element("info")?.Element("info_elements")?.Elements("info_value_element_t") ?? Enumerable.Empty<XElement>()) if (int.TryParse(info.Element("value")?.Value, out var bit)) { var label = info.Descendants("localized_string_t").FirstOrDefault(x => x.Element("langid")?.Value is "es" or "en")?.Element("stringvalue")?.Value; if (!string.IsNullOrWhiteSpace(label)) result.BitNames[bit] = label.Trim(); }
        return result;
    }
    private static double Number(string? value, double fallback) => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}

public sealed class AnalyzerValueElement { public string KeyName { get; init; } = ""; public string ValType { get; init; } = ""; public double Scale { get; init; } public double Offset { get; init; } public int NodeAddress { get; init; } public int ByteOffset { get; init; } public string Signature { get; init; } = ""; public Dictionary<int, string> BitNames { get; } = []; }
public sealed class AnalyzerSession { public string Name { get; init; } = ""; public byte[] Content { get; init; } = []; public List<string> SelectedKeys { get; set; } = []; public Dictionary<string, List<int>> DigitalBits { get; set; } = []; public DateTime RangeFrom { get; set; } public DateTime RangeTo { get; set; } public int TimeOffsetHours { get; set; } = -1; }
public sealed class AnalyzerRow { public DateTime Timestamp { get; init; } public Dictionary<string, double> Values { get; init; } = []; }
public sealed class AnalyzerResult { public List<AnalyzerResultSession> Sessions { get; } = []; public int RowCount => Sessions.Sum(x => x.Rows.Count); }
public sealed class AnalyzerResultSession { public string Name { get; init; } = ""; public List<string> Keys { get; init; } = []; public List<AnalyzerRow> Rows { get; init; } = []; }
