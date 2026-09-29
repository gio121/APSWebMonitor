using System.Buffers.Binary;
using ApsMonitor.Models;

namespace ApsMonitor.Services;

public static class ControlCommandProtocol
{
    public static byte[] Build(ScadaCommand command, Signal? signal)
    {
        if (command.FrameType is not (0x6A or 0x7A))
            throw new ArgumentException("Configure el comando como 6A o 7A antes de enviarlo.");
        if (command.Subcommand is null or < 0 or > ushort.MaxValue)
            throw new ArgumentException("El subcomando debe estar entre 0000 y FFFF hexadecimal.");
        byte[] payload;
        if (command.FrameType == 0x7A)
        {
            payload = new byte[8];
        }
        else
        {
            if (signal is null || signal.Id != command.SignalId || signal.IsDeleted || signal.NodoNumero != 2)
                throw new ArgumentException("Seleccione una variable de control disponible (nodo 2).");
            if (signal.IsAscii || command.VariableValue is not { } value || !double.IsFinite(value))
                throw new ArgumentException("El comando 6A requiere un valor numérico finito y una variable numérica.");
            byte[] data = EncodeValue(signal.TipoVariable, value);
            payload = new byte[2 + data.Length];
            data.CopyTo(payload, 2);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)command.Subcommand.Value);
        return ReprogrammingProtocol.Frame(2, (byte)command.FrameType.Value, payload);
    }

    public static byte[] EncodeValue(string type, double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("Valor no válido.");
        if (type == "FLOAT32")
        {
            float number = (float)value;
            if (!float.IsFinite(number)) throw new ArgumentException("Valor fuera del rango FLOAT32.");
            var result = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(result, number);
            return result;
        }
        if (value != Math.Truncate(value)) throw new ArgumentException("La variable requiere un valor entero.");
        try
        {
            var result = new byte[type is "UINT8" or "INT8" or "BCD_BYTE" ? 1 : type is "UINT16" or "INT16" ? 2 : 4];
            switch (type)
            {
                case "UINT8": result[0] = checked((byte)value); break;
                case "INT8": result[0] = unchecked((byte)checked((sbyte)value)); break;
                case "UINT16": BinaryPrimitives.WriteUInt16LittleEndian(result, checked((ushort)value)); break;
                case "INT16": BinaryPrimitives.WriteInt16LittleEndian(result, checked((short)value)); break;
                case "UINT32": BinaryPrimitives.WriteUInt32LittleEndian(result, checked((uint)value)); break;
                case "INT32": BinaryPrimitives.WriteInt32LittleEndian(result, checked((int)value)); break;
                case "BCD_BYTE" when value is >= 0 and <= 99:
                    result[0] = (byte)(((int)value / 10 << 4) | (int)value % 10); break;
                default: throw new ArgumentException($"Tipo de variable o valor no admitido: {type}.");
            }
            return result;
        }
        catch (OverflowException) { throw new ArgumentException($"Valor fuera del rango {type}."); }
    }
}
