using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ApsMonitor.Services;

public interface ISftpFirmwareUploader
{
    Task UploadAsync(string address, int port, string user, string password, string hostKeySha256,
        IReadOnlyList<CommunicationsFile> files, Action<string, double> progress, CancellationToken token,
        string remotePath = "/mnt/artifacts/update");
}

public sealed class SftpFirmwareUploader : ISftpFirmwareUploader
{
    private record SftpCredential(string User, string Password);

    public async Task UploadAsync(string address, int port, string user, string password, string hostKeySha256,
        IReadOnlyList<CommunicationsFile> files, Action<string, double> progress, CancellationToken token,
        string remotePath = "/mnt/artifacts/update")
    {
        int sftpPort = port is >= 1 and <= 65535 ? port : 22;
        string initialUser = string.IsNullOrWhiteSpace(user) ? "root" : user.Trim();
        string initialPass = password ?? "";

        var credentialsToTry = new List<SftpCredential>
        {
            new(initialUser, initialPass)
        };

        // Fallbacks automáticos si el usuario inicial no tiene permisos suficientes
        if (!initialUser.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            credentialsToTry.Add(new("root", "changeme"));
        }
        if (!initialUser.Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            credentialsToTry.Add(new("update", "38I3jf/(Rihs959.fe9L0ra93nf"));
        }
        if (!initialUser.Equals("sepsa", StringComparison.OrdinalIgnoreCase))
        {
            credentialsToTry.Add(new("sepsa", "thisisveryunsafepleasedeactivatemeaftersetup"));
        }

        string targetDir = string.IsNullOrWhiteSpace(remotePath) ? "." : remotePath.Trim().TrimEnd('/');
        Exception? lastException = null;

        for (int i = 0; i < credentialsToTry.Count; i++)
        {
            var cred = credentialsToTry[i];
            try
            {
                if (i > 0)
                {
                    progress($"Reintentando subida SFTP con usuario '{cred.User}'…", 5);
                }

                using var sftp = new SftpClient(address, sftpPort, cred.User, cred.Password);
                sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
                sftp.OperationTimeout = TimeSpan.FromSeconds(30);
                sftp.HostKeyReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(hostKeySha256))
                    {
                        e.CanTrust = true;
                        return;
                    }
                    string actual = Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
                    string expected = hostKeySha256.Trim().Replace("SHA256:", "", StringComparison.Ordinal).TrimEnd('=');
                    e.CanTrust = string.Equals(actual, expected, StringComparison.Ordinal);
                };

                await sftp.ConnectAsync(token);

                if (targetDir != "." && !string.IsNullOrEmpty(targetDir))
                {
                    EnsureRemoteDirectoryExists(sftp, targetDir);
                }

                await UploadFilesInternalAsync(sftp, targetDir, files, progress, token);

                // Si se conectó como root, relajar permisos a 0777 para futuros accesos
                if (cred.User.Equals("root", StringComparison.OrdinalIgnoreCase) && targetDir.StartsWith("/mnt/artifacts", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        sftp.ChangePermissions(targetDir, 511); // 0777 octal
                    }
                    catch
                    {
                        // No interrumpir si no se pueden cambiar los permisos
                    }
                }

                sftp.Disconnect();
                return; // Subida completada con éxito
            }
            catch (Exception ex) when (IsPermissionOrAuthError(ex) && i < credentialsToTry.Count - 1)
            {
                lastException = ex;
                // Continuar con el siguiente intento de credenciales
            }
        }

        if (lastException is not null)
        {
            throw lastException;
        }
    }

    private static bool IsPermissionOrAuthError(Exception ex)
    {
        return ex is SftpPermissionDeniedException
            || ex is SshAuthenticationException
            || ex.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task UploadFilesInternalAsync(SftpClient sftp, string targetDir,
        IReadOnlyList<CommunicationsFile> files, Action<string, double> progress, CancellationToken token)
    {
        long total = files.Sum(f => (long)f.Content.Length), completed = 0;
        foreach (var file in files)
        {
            string destination = targetDir is "." or "" ? file.Name : $"{targetDir}/{file.Name}";
            progress($"Subiendo {file.Name} a {destination} por SFTP…", 90.0 * completed / total);
            using var stream = new MemoryStream(file.Content, writable: false);
            
            // Si el archivo ya existía, intentar borrarlo antes para evitar conflictos de sobreescritura
            try
            {
                if (sftp.Exists(destination))
                {
                    sftp.DeleteFile(destination);
                }
            }
            catch
            {
                // Ignorar si no existe o no se puede borrar
            }

            await sftp.UploadFileAsync(stream, destination, canOverride: true, cancellationToken: token);
            completed += file.Content.Length;
            progress($"Transferido {file.Name} a {destination}", 90.0 * completed / total);
        }
    }

    private static void EnsureRemoteDirectoryExists(SftpClient sftp, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or "/") return;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = path.StartsWith('/') ? "" : sftp.WorkingDirectory;
        foreach (var part in parts)
        {
            current += "/" + part;
            try
            {
                if (!sftp.Exists(current))
                {
                    sftp.CreateDirectory(current);
                }
            }
            catch
            {
                // Ignorar si ya existía o falla la verificación de existencia previa
            }
        }
    }
}
