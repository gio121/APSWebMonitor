using System.Buffers.Binary;
using System.Text;

namespace ApsMonitor.Services;

public static class FirmwareImage
{
    public const int MaximumBytes = 16 * 1024 * 1024;

    public static byte[] Decode(string name, byte[] content, uint flashOffset)
    {
        if (content.Length is 0 or > MaximumBytes) throw new InvalidDataException("Fichero vacío o superior a 16 MiB.");
        string extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension == ".bin") return content.ToArray();
        if (extension is not (".hex" or ".h86" or ".mcs"))
            throw new InvalidDataException("Seleccione un fichero BIN, HEX, H86 o MCS sin cifrar.");

        using var reader = new StringReader(Encoding.ASCII.GetString(content));
        var segments = new List<(int Offset, byte[] Data)>();
        ulong addressBase = 0;
        int length = 0, lineNumber = 0;
        bool eof = false;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (eof || !line.StartsWith(':')) throw Invalid(lineNumber);
            byte[] record;
            try { record = Convert.FromHexString(line[1..]); }
            catch (FormatException) { throw Invalid(lineNumber); }
            if (record.Length < 5 || record.Length != record[0] + 5 || (record.Sum(b => (int)b) & 255) != 0)
                throw Invalid(lineNumber);
            int count = record[0];
            ushort address = BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(1));
            switch (record[3])
            {
                case 0:
                    if (count == 0) break;
                    ulong absolute = addressBase + address;
                    if (absolute < flashOffset || absolute - flashOffset + (uint)count > MaximumBytes)
                        throw new InvalidDataException($"Dirección fuera del rango de memoria en línea {lineNumber}. Revise el desplazamiento de flash.");
                    int offset = (int)(absolute - flashOffset);
                    segments.Add((offset, record.AsSpan(4, count).ToArray()));
                    length = Math.Max(length, offset + count);
                    break;
                case 1 when count == 0 && address == 0: eof = true; break;
                case 2 when count == 2 && address == 0:
                    addressBase = (ulong)BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(4)) << 4; break;
                case 4 when count == 2 && address == 0:
                    addressBase = (ulong)BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(4)) << 16; break;
                case 3 or 5 when count == 4 && address == 0: break;
                default: throw Invalid(lineNumber);
            }
        }
        if (!eof || length == 0) throw new InvalidDataException("El fichero HEX no contiene datos o no tiene registro de fin.");
        var image = new byte[length];
        Array.Fill(image, (byte)0xFF);
        var written = new System.Collections.BitArray(length);
        foreach (var segment in segments)
            for (int i = 0; i < segment.Data.Length; i++)
            {
                int position = segment.Offset + i;
                if (written[position]) throw new InvalidDataException("El fichero HEX contiene direcciones solapadas.");
                written[position] = true;
                image[position] = segment.Data[i];
            }
        return image;
    }

    private static InvalidDataException Invalid(int line) => new($"Registro Intel HEX inválido en línea {line} (formato, tipo o checksum).");
}
