using System.Security.Cryptography;
using Renci.SshNet;

namespace ApsMonitor.Services;

public interface ISftpFirmwareUploader
{
    Task UploadAsync(string address, int port, string user, string password, string hostKeySha256,
        IReadOnlyList<CommunicationsFile> files, Action<string, double> progress, CancellationToken token);
}

public sealed class SftpFirmwareUploader : ISftpFirmwareUploader
{
    public async Task UploadAsync(string address, int port, string user, string password, string hostKeySha256,
        IReadOnlyList<CommunicationsFile> files, Action<string, double> progress, CancellationToken token)
    {
        using var sftp = new SftpClient(address, port, user, password);
        sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
        sftp.OperationTimeout = TimeSpan.FromSeconds(30);
        sftp.HostKeyReceived += (_, e) =>
        {
            string actual = Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
            string expected = hostKeySha256.Trim().Replace("SHA256:", "", StringComparison.Ordinal).TrimEnd('=');
            e.CanTrust = string.Equals(actual, expected, StringComparison.Ordinal);
        };
        await sftp.ConnectAsync(token);
        long total = files.Sum(f => (long)f.Content.Length), completed = 0;
        foreach (var file in files)
        {
            progress($"Subiendo {file.Name} por SFTP…", 90.0 * completed / total);
            using var stream = new MemoryStream(file.Content, writable: false);
            await sftp.UploadFileAsync(stream, "./" + file.Name, token);
            completed += file.Content.Length;
            progress($"Transferido {file.Name}", 90.0 * completed / total);
        }
        sftp.Disconnect();
    }
}
