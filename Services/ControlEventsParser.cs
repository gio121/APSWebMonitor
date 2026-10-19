using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ApsMonitor.Models;

namespace ApsMonitor.Services;

public static class ControlEventsParser
{
    public const int MaxFileBytes = 50 * 1024 * 1024;
    private static readonly HashSet<string> TimeParts = ["timedat.minutes", "timedat.hours", "timedat.monthday",
        "timedat.month", "timedat.year", "rtim_minutes", "rtim_hours", "rtim_day", "rtim_month", "rtim_year"];

    public static List<ControlEventConfig> LoadConfig(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = MaxFileBytes });
        return LoadConfig(XDocument.Load(reader));
    }

    public static List<ControlEventConfig> LoadConfig(XDocument doc, bool allowEmpty = false)
    {
        var configs = new List<ControlEventConfig>();
        foreach (var node in doc.Descendants("node_t"))
        {
            var trace = node.Element("event_trace");
            if (trace is null || Int(trace, "num_event_registers", 0) == 0) continue;
            // Communications logs may also define event_trace, but do not use
            // the binary control failure register format.
            var header = trace.Element("eventdata")?.Elements("data_block_t")
                .FirstOrDefault(b => Text(b, "start_position_on_event_register") == "0");
            if (header?.Element("tracevars")?.Elements("valuereference_element_t").Any(r => Text(r, "keyname") == "failReg") != true) continue;
            var signature = Text(node.Element("sw"), "signature");
            if (signature.Length != 6 || signature.Any(c => c > 127))
                throw new InvalidDataException("La firma del nodo debe contener seis caracteres ASCII.");
            var values = new Dictionary<string, ControlEventValue>(StringComparer.Ordinal);
            foreach (var element in node.Element("value_elements")?.Elements("value_element_t") ?? [])
            {
                var key = Text(element, "keyname");
                if (key.Length == 0 || values.ContainsKey(key)) continue;
                var value = element.Element("value");
                var info = element.Element("info");
                var labels = new Dictionary<int, string>();
                foreach (var label in info?.Element("info_elements")?.Elements("info_value_element_t") ?? [])
                    labels.TryAdd(Int(label, "value"), Localized(label.Element("value_description")));
                values.Add(key, new(key, Localized(element.Element("description"), key), Text(value, "valtype").ToUpperInvariant(),
                    Number(value, "scale", 1), Number(value, "offset", 0), Text(info, "units"), Text(info, "type") == "BITFIELD",
                    Number(info, "min", 0), Number(info, "max", double.MaxValue), labels));
            }
            var blocks = new List<ControlEventBlock>();
            foreach (var block in trace.Element("eventdata")?.Elements("data_block_t") ?? [])
            {
                var refs = block.Element("tracevars")?.Elements("valuereference_element_t")
                    .Select(r => new ControlEventReference(Text(r, "keyname"), Int(r, "position")))
                    .Where(r => values.ContainsKey(r.Key)).ToList() ?? [];
                blocks.Add(new(Text(block, "traceNameDataSection"), Text(block, "data_type") == "TRACE",
                    Int(block, "start_position_on_event_register"), Int(block, "trace_register_size"), Int(block, "num_trace_registers"), refs));
            }
            var fpga = new List<ControlEventFpgaReference>();
            foreach (var kind in new[] { "ANALOG", "DIGITAL" })
                foreach (var element in trace.Element("FPGA_" + kind)?.Elements("FPGA_element_t") ?? [])
                    fpga.Add(new(Text(element, "keyname"), kind, Int(element, "elementIndex"), Int(element, "mask1", 0), Int(element, "mask2", 0)));
            foreach (var element in trace.Element("FPGA_STATUS")?.Elements("valuereference_element_t") ?? [])
                fpga.Add(new(Text(element, "keyname"), "STATUS", Int(element, "position"), 0, 0));
            var download = trace.Element("event_download");
            var config = new ControlEventConfig(Localized(node.Element("sw")?.Element("description"), signature), signature,
                checked((byte)Int(node, "pte_address_node_number")), Text(download, "downloadType"), Int(download, "startAddress", 0),
                Int(download, "endAddress", 0), Int(download, "blockSize", 0), Int(trace, "event_start_address"),
                Int(trace, "event_size"), Int(trace, "num_event_registers"), values, blocks, fpga);
            ValidateConfig(config);
            configs.Add(config);
        }
        if (configs.Count == 0 && !allowEmpty) throw new InvalidDataException("El CMFX no contiene nodos con eventos de control configurados.");
        if (configs.Select(c => c.Signature).Distinct().Count() != configs.Count)
            throw new InvalidDataException("Hay nodos de eventos con la misma firma de equipo.");
        return configs;
    }

    public static ControlEventConfig? BindSignals(ControlEventConfig layout, IEnumerable<Signal> signals)
    {
        var active = signals.Where(s => !s.IsDeleted && s.NodoNumero == layout.Node)
            .GroupBy(s => s.Tag.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, ControlEventValue>(StringComparer.Ordinal);
        foreach (var definition in layout.Values.Values)
        {
            if (!active.TryGetValue(definition.Key, out var signal)) continue;
            var labels = definition.IsBitfield
                ? signal.BitTextoActivo.Select((text, bit) => (text, bit)).Where(x => !string.IsNullOrWhiteSpace(x.text)).ToDictionary(x => x.bit, x => x.text!)
                : definition.Labels;
            values.Add(definition.Key, definition with
            {
                Name = string.IsNullOrWhiteSpace(signal.Nombre) ? signal.Tag : signal.Nombre,
                Type = SignalType(signal), Units = signal.Unidad ?? "", Scale = signal.Escala, Offset = signal.Offset,
                Labels = labels
            });
        }
        if (!values.ContainsKey("failReg")) return null;
        var config = layout with
        {
            Name = active.Values.FirstOrDefault()?.NodoDescripcion is { Length: > 0 } name ? name : layout.Name,
            Values = values,
            Blocks = layout.Blocks.Select(b => b with { References = b.References.Where(r => values.ContainsKey(r.Key)).ToList() }).ToList()
        };
        ValidateConfig(config);
        return config;
    }

    private static string SignalType(Signal signal) => signal.IsAscii ? "ASCII_BYTE" : signal.TipoVariable.ToUpperInvariant() switch
    {
        "UINT8" or "UNSIGNED_BYTE" => "BYTE", "INT8" or "SIGNED_BYTE" => "SBYTE",
        "UINT16" or "UNSIGNED_WORD" => "UINT", "INT16" or "SIGNED_WORD" => "INT",
        "UINT32" or "UNSIGNED_DWORD" => "ULONG", "INT32" or "SIGNED_DWORD" => "LONG",
        "FLOAT" => "FLOAT32", _ => signal.TipoVariable.ToUpperInvariant()
    };

    private static void ValidateConfig(ControlEventConfig c)
    {
        long end = (long)c.EventStart + (long)c.EventSize * c.EventCount;
        if (c.EventStart < 6 || c.EventSize < 1 || c.EventCount is < 1 or > 10000 || end > MaxFileBytes ||
            c.DownloadStart < 0 || c.DownloadEnd < c.DownloadStart || c.DownloadEnd - c.DownloadStart > MaxFileBytes)
            throw new InvalidDataException("Tamaños o direcciones de eventos no válidos en el CMFX.");
        if (!c.Blocks.Any(b => b.Offset == 0 && b.References.Any(r => r.Key == "failReg")))
            throw new InvalidDataException("Falta failReg en la cabecera de eventos del CMFX.");
        if (c.DownloadEnd > c.DownloadStart && end > c.DownloadEnd - c.DownloadStart)
            throw new InvalidDataException("Los registros exceden el tamaño de descarga del CMFX.");
        foreach (var b in c.Blocks)
        {
            if (b.Offset < 0 || b.RegisterSize < 1 || b.RegisterCount < 1 || (long)b.Offset + (long)b.RegisterSize * b.RegisterCount > c.EventSize)
                throw new InvalidDataException($"El bloque {b.Name} excede los límites del evento.");
            if (IsFpga(b) && b.RegisterSize < 4) throw new InvalidDataException("Los registros FPGA deben ocupar al menos cuatro bytes.");
            foreach (var r in b.References)
            {
                if (!c.Values.TryGetValue(r.Key, out var v)) throw new InvalidDataException($"Falta la definición de {r.Key} en value_elements.");
                if (r.Position < 0 || (long)r.Position + Size(v.Type) > b.RegisterSize)
                    throw new InvalidDataException($"La señal {r.Key} excede los límites de su bloque.");
            }
        }
    }

    public static ControlEventConfig MatchConfig(byte[] dat, IEnumerable<ControlEventConfig> configs)
    {
        if (dat.Length < 6) throw new InvalidDataException("El DAT está vacío o no contiene una cabecera completa.");
        var signature = Encoding.ASCII.GetString(dat, 0, 6);
        var config = configs.FirstOrDefault(c => c.Signature == signature)
            ?? throw new InvalidDataException($"Firma de equipo «{signature}» no encontrada en la configuración de las señales importadas.");
        var minimum = (long)config.EventStart + (long)config.EventSize * config.EventCount;
        int expected = config.DownloadEnd - config.DownloadStart;
        if (dat.Length > MaxFileBytes || dat.Length < minimum || (expected > 0 && dat.Length != expected))
            throw new InvalidDataException($"Tamaño de DAT incorrecto: {dat.Length} bytes; se esperaban {(expected > 0 ? expected : minimum)}.");
        return config;
    }

    public static List<ControlEventEntry> ReadEvents(byte[] dat, ControlEventConfig config)
    {
        _ = MatchConfig(dat, [config]);
        var header = config.Blocks.First(b => b.Offset == 0);
        var result = new List<ControlEventEntry>();
        for (int slot = 0; slot < config.EventCount; slot++)
        {
            int start = config.EventStart + slot * config.EventSize;
            var numberRef = header.References.First(r => r.Key == "failReg");
            var numberDef = config.Values[numberRef.Key];
            double number = Numeric(dat, start + numberRef.Position, numberDef);
            bool valid = number > numberDef.Min && number <= numberDef.Max;
            var timeRef = header.References.FirstOrDefault(r => IsTime(r.Key));
            DateTime? time = valid && timeRef is not null ? ReadTime(dat, start + timeRef.Position, start + header.RegisterSize) : null;
            var failures = valid ? string.Join(" + ", header.References.Where(r => IsFailure(r.Key))
                .Select(r => Decode(dat, start + r.Position, config.Values[r.Key])).Where(s => s.Length > 0)) : "—";
            result.Add(new(slot, valid ? (ulong)number : null, time, failures, valid));
        }
        return result.OrderByDescending(e => e.Number).ThenBy(e => e.Slot).ToList();
    }

    public static List<ControlEventDetail> ReadDetails(byte[] dat, ControlEventConfig c, ControlEventEntry entry, TimeZoneInfo zone)
    {
        var result = new List<ControlEventDetail>();
        foreach (var block in c.Blocks)
        foreach (var reference in block.References)
        {
            if (TimeParts.Contains(reference.Key) || reference.Key.StartsWith("spare", StringComparison.OrdinalIgnoreCase)) continue;
            var v = c.Values[reference.Key];
            int pos = c.EventStart + entry.Slot * c.EventSize + block.Offset + reference.Position;
            if (block.IsTrace) pos += (block.RegisterCount - 1) * block.RegisterSize;
            var bits = v.IsBitfield ? v.Labels.Where(l => l.Key >= 0 && l.Key < Size(v.Type) * 8)
                .Select(l => new ControlEventBit(l.Key, l.Value, (Unsigned(dat, pos, v.Type) & (1UL << l.Key)) != 0)).ToList() : [];
            string decoded = IsTime(v.Key) ? FormatTime(ReadTime(dat, pos, pos - reference.Position + block.RegisterSize), zone) : Decode(dat, pos, v);
            result.Add(new(v.Key, v.Name, block.Name, decoded.Length == 0 ? "—" : decoded, IsTime(v.Key) ? "" : v.Units, block.IsTrace, bits));
        }
        return result;
    }

    public static List<ControlEventSeries> ReadTraces(byte[] dat, ControlEventConfig c, ControlEventEntry entry, int blockIndex)
    {
        var block = c.Blocks[blockIndex];
        if (!block.IsTrace || !entry.IsValid) return [];
        int start = c.EventStart + entry.Slot * c.EventSize + block.Offset;
        if (IsFpga(block) && c.FpgaReferences.Count > 0) return ReadFpga(dat, start, block, c);
        var result = new List<ControlEventSeries>();
        foreach (var r in block.References)
        {
            var v = c.Values[r.Key];
            var x = Enumerable.Range(0, block.RegisterCount).Select(i => (double)i).ToArray();
            result.Add(new(v.Key, v.Name, v.Units, x,
                Enumerable.Range(0, block.RegisterCount).Select(i => Numeric(dat, start + i * block.RegisterSize + r.Position, v)).ToArray()));
            if (v.IsBitfield)
                foreach (var label in v.Labels.Where(l => l.Key >= 0 && l.Key < Size(v.Type) * 8 && l.Value.Length > 0))
                    result.Add(new($"{v.Key}[{label.Key}]", $"{v.Name} · {label.Value}", "", x,
                        Enumerable.Range(0, block.RegisterCount).Select(i => (Unsigned(dat, start + i * block.RegisterSize + r.Position, v.Type) & (1UL << label.Key)) != 0 ? 1.0 : 0.0).ToArray()));
        }
        return result;
    }

    private sealed record FpgaSample(string Kind, int Index, int Time, ushort Value, bool Valid);

    // frmGraphics: high word precedes low word; 8-bit clock wraps every 256 µs.
    private static List<ControlEventSeries> ReadFpga(byte[] dat, int start, ControlEventBlock block, ControlEventConfig c)
    {
        var samples = new List<FpgaSample>();
        int epoch = 0, prior = -1;
        for (int i = 0; i < block.RegisterCount; i++)
        {
            int pos = start + i * block.RegisterSize;
            uint raw = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(dat.AsSpan(pos)) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(dat.AsSpan(pos + 2));
            string kind = (raw & 0x80000000) != 0 ? "STATUS" : (raw & 0x10000000) == 0 ? "ANALOG" : "DIGITAL";
            int index = kind == "STATUS" ? (int)((raw >> 28) & 7) : kind == "DIGITAL" ? (int)((raw >> 29) & 3) : (int)(((raw >> 29) & 3) * 8 + ((raw >> 25) & 7));
            int time = (int)((raw >> 16) & 255);
            if (prior >= 0 && time - prior is not (0 or 1 or -255))
                throw new InvalidDataException("Marca de tiempo FPGA inválida: se esperaban incrementos de 0 o 1 µs.");
            if (prior > time) epoch += 256;
            prior = time;
            samples.Add(new(kind, index, time + epoch, (ushort)raw, (raw & 0x01000000) != 0));
        }
        var statuses = samples.Where(s => s.Kind == "STATUS" && s.Index == 0).GroupBy(s => s.Time).ToDictionary(g => g.Key, g => g.Last().Value);
        var points = new Dictionary<string, List<(double X, double Y)>>();
        foreach (var sample in samples.Where(s => s.Valid))
        {
            int mask = statuses.TryGetValue(sample.Time, out var status) ? sample.Kind == "ANALOG" ? (status & 0x2000) >> 13 : (status & 0xC000) >> 14 : 0;
            var reference = c.FpgaReferences.FirstOrDefault(r => r.Kind == sample.Kind && r.Index == sample.Index && (r.Mask1 & mask) == r.Mask1 && r.Mask2 == 0);
            if (reference is null || !c.Values.TryGetValue(reference.Key, out var v)) continue;
            // Preserve signed INT conversion before applying the CMFX scale.
            double raw = v.Type == "INT" ? (short)sample.Value : sample.Value;
            if (!points.TryGetValue(v.Key, out var list)) points.Add(v.Key, list = []);
            list.Add((sample.Time, raw));
        }
        var result = new List<ControlEventSeries>();
        foreach (var p in points)
        {
            var v = c.Values[p.Key];
            var x = p.Value.Select(point => point.X).ToArray();
            result.Add(new(p.Key, v.Name, v.Units, x, p.Value.Select(point => point.Y * v.Scale + v.Offset).ToArray()));
            if (v.IsBitfield)
                foreach (var label in v.Labels.Where(l => l.Key >= 0 && l.Key < 16 && l.Value.Length > 0))
                    result.Add(new($"{v.Key}[{label.Key}]", $"{v.Name} · {label.Value}", "", x,
                        p.Value.Select(point => ((ushort)(int)point.Y & (1 << label.Key)) != 0 ? 1.0 : 0.0).ToArray()));
        }
        return result;
    }

    public static bool IsFpga(ControlEventBlock b) => b.Name.Equals("FPGA trace", StringComparison.OrdinalIgnoreCase);
    public static string FormatTime(DateTime? utc, TimeZoneInfo zone) => utc.HasValue ? TimeZoneInfo.ConvertTimeFromUtc(utc.Value, zone).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) : "—";
    private static bool IsTime(string key) => key is "timedat.seconds" or "rtim_seconds";
    private static bool IsFailure(string key) => key.StartsWith("cfails", StringComparison.OrdinalIgnoreCase);
    private static DateTime? ReadTime(byte[] dat, int pos, int end)
    {
        if (pos < 0 || pos + 10 >= Math.Min(dat.Length, end)) return null;
        try { return new DateTime(2000 + Bcd(dat[pos + 10]), Bcd(dat[pos + 8]), Bcd(dat[pos + 6]), Bcd(dat[pos + 4]), Bcd(dat[pos + 2]), Bcd(dat[pos]), DateTimeKind.Utc); }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (InvalidDataException) { return null; }
    }
    private static int Bcd(byte b) => (b & 15) > 9 || (b >> 4) > 9 ? throw new InvalidDataException("Valor BCD inválido.") : (b >> 4) * 10 + (b & 15);
    private static string Decode(byte[] dat, int pos, ControlEventValue v)
    {
        if (v.IsBitfield)
        {
            ulong bits = Unsigned(dat, pos, v.Type);
            return string.Join(" + ", v.Labels.Where(l => l.Key >= 0 && l.Key < Size(v.Type) * 8 && (bits & (1UL << l.Key)) != 0).Select(l => l.Value).Where(s => s.Length > 0));
        }
        double value = Numeric(dat, pos, v);
        if (v.Labels.Count > 0) return v.Labels.GetValueOrDefault((int)Math.Round(value)) ?? value.ToString("0.##", CultureInfo.InvariantCulture);
        return v.Type == "ASCII_BYTE" ? ((char)dat[pos]).ToString().Replace('\0', ' ') : value.ToString("0.##", CultureInfo.InvariantCulture);
    }
    private static double Numeric(byte[] dat, int pos, ControlEventValue v)
    {
        var s = dat.AsSpan(pos, Size(v.Type));
        double raw = v.Type switch
        {
            "BCD_BYTE" => Bcd(s[0]), "SBYTE" => (sbyte)s[0], "INT" => BinaryPrimitives.ReadInt16LittleEndian(s),
            "LONG" => BinaryPrimitives.ReadInt32LittleEndian(s), "INT64" => BinaryPrimitives.ReadInt64LittleEndian(s),
            "FLOAT32" => BinaryPrimitives.ReadSingleLittleEndian(s), "FLOAT64" => BinaryPrimitives.ReadDoubleLittleEndian(s),
            _ => Unsigned(dat, pos, v.Type)
        };
        return raw * v.Scale + v.Offset;
    }
    private static ulong Unsigned(byte[] dat, int pos, string type)
    {
        var s = dat.AsSpan(pos, Size(type));
        return s.Length switch { 1 => s[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(s), 4 => BinaryPrimitives.ReadUInt32LittleEndian(s), _ => BinaryPrimitives.ReadUInt64LittleEndian(s) };
    }
    private static int Size(string type) => type switch
    {
        "ASCII_BYTE" or "BCD_BYTE" or "BYTE" or "SBYTE" => 1, "UINT" or "INT" => 2,
        "ULONG" or "LONG" or "FLOAT32" => 4, "UINT64" or "INT64" or "FLOAT64" => 8,
        _ => throw new InvalidDataException($"Tipo de señal no soportado: {type}.")
    };
    private static string Text(XElement? e, string name) => e?.Element(name)?.Value.Trim() ?? "";
    private static int Int(XElement? e, string name, int? fallback = null) => int.TryParse(Text(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback ?? throw new InvalidDataException($"Campo CMFX no válido: {name}.");
    private static double Number(XElement? e, string name, double fallback) => double.TryParse(Text(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : fallback;
    private static string Localized(XElement? e, string fallback = "")
    {
        var strings = e?.Elements("localized_string_t").ToList() ?? [];
        return (strings.FirstOrDefault(s => Text(s, "langid").StartsWith("es", StringComparison.OrdinalIgnoreCase))
            ?? strings.FirstOrDefault(s => Text(s, "langid").StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? strings.FirstOrDefault())?.Element("stringvalue")?.Value.Trim() ?? fallback;
    }
}
