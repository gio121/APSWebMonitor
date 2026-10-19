namespace ApsMonitor.Models;

public sealed record ControlEventValue(string Key, string Name, string Type, double Scale, double Offset,
    string Units, bool IsBitfield, double Min, double Max, IReadOnlyDictionary<int, string> Labels);
public sealed record ControlEventReference(string Key, int Position);
public sealed record ControlEventBlock(string Name, bool IsTrace, int Offset, int RegisterSize,
    int RegisterCount, IReadOnlyList<ControlEventReference> References);
public sealed record ControlEventFpgaReference(string Key, string Kind, int Index, int Mask1, int Mask2);
public sealed record ControlEventConfig(string Name, string Signature, byte Node, string DownloadType,
    int DownloadStart, int DownloadEnd, int BlockSize, int EventStart, int EventSize, int EventCount,
    IReadOnlyDictionary<string, ControlEventValue> Values, IReadOnlyList<ControlEventBlock> Blocks,
    IReadOnlyList<ControlEventFpgaReference> FpgaReferences);
public sealed record ControlEventEntry(int Slot, ulong? Number, DateTime? UtcTime, string Failures, bool IsValid);
public sealed record ControlEventBit(int Index, string Name, bool Active);
public sealed record ControlEventDetail(string Key, string Name, string Block, string Value, string Units,
    bool IsTrace, IReadOnlyList<ControlEventBit> Bits);
public sealed record ControlEventSeries(string Key, string Name, string Units, double[] X, double[] Y);
