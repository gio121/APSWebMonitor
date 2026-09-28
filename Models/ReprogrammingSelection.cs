using ApsMonitor.Services;

namespace ApsMonitor.Models;

public sealed record ReprogrammingSoftware(string Id, string Description, string Kind, string Signature);

public sealed record ReprogrammingSelection(
    ReprogrammingTarget Target, string Signature, int BlockSize, uint FlashOffset,
    string FileName, byte[]? Image, IReadOnlyList<CommunicationsFile> Files,
    int SftpPort, string HostKey)
{
    public string FileSummary => Image is not null ? FileName : string.Join(", ", Files.Select(f => f.Name));
}
