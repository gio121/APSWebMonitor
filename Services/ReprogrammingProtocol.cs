using System.Buffers.Binary;
using System.Text;

namespace ApsMonitor.Services;

// SEPSA legacy upload: reference SwUploadProcess and SepsaProtocol in MetroMadrid.
public static class ReprogrammingProtocol
{
    public static byte[] Frame(byte node, byte type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 7];
        frame[0] = 0xAA;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), checked((ushort)frame.Length));
        frame[3] = node;
        frame[4] = 1;
        frame[5] = type;
        payload.CopyTo(frame.AsSpan(6));
        frame[^1] = Checksum(frame.AsSpan(0, frame.Length - 1));
        return frame;
    }

    public static byte[] Start(byte node, bool fpga, int blocks)
    {
        if (blocks is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(blocks));
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), (ushort)blocks);
        return Frame(node, fpga ? (byte)0x4B : (byte)0x4A, payload);
    }

    public static byte[] Block(byte node, bool fpga, string signature, byte[] image, int blockSize, ushort index)
    {
        if (signature.Length != 6 || signature.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("La firma debe contener seis caracteres alfanuméricos ASCII.");
        if (blockSize is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(blockSize));
        var payload = new byte[blockSize + 10];
        Encoding.ASCII.GetBytes(signature).CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), index);
        var data = payload.AsSpan(8, blockSize);
        data.Fill(0xFF);
        int offset = index * blockSize;
        if (offset < image.Length) image.AsSpan(offset, Math.Min(blockSize, image.Length - offset)).CopyTo(data);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(payload.Length - 2), Crc16(data));
        return Frame(node, fpga ? (byte)0x45 : (byte)0x44, payload);
    }

    public static bool TryReadStatus(byte[] frame, byte node, out ushort block, out byte status)
    {
        block = 0; status = 0;
        if (frame.Length < 12 || frame[0] != 0xAA ||
            BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) != frame.Length ||
            frame[3] != 1 || frame[4] != node || frame[5] is not (0x4C or 0x4D) ||
            frame[6] != 1 || frame[7] != 0 || Checksum(frame.AsSpan(0, frame.Length - 1)) != frame[^1]) return false;
        block = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8));
        status = frame[10];
        return true;
    }

    private static byte Checksum(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (byte b in data) sum += b;
        return (byte)sum;
    }

    public static bool TryReadFileResponse(byte[] frame, out byte[] payload)
    {
        payload = [];
        if (frame.Length < 9 || frame[0] != 0xAA ||
            BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) != frame.Length ||
            frame[3] != 1 || frame[4] != 3 || frame[5] is not (0x82 or 0x83) || frame[7] != 0 ||
            Checksum(frame.AsSpan(0, frame.Length - 1)) != frame[^1]) return false;
        payload = frame[6..^1];
        return true;
    }

    public static bool IsErrorResponse(byte[] frame, byte node) => frame.Length >= 9 && frame[0] == 0xAA &&
        BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == frame.Length &&
        frame[3] == 1 && frame[4] == node && frame[5] == 0xF7 &&
        Checksum(frame.AsSpan(0, frame.Length - 1)) == frame[^1];

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        return crc;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        return ~crc;
    }
}
