using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;

namespace ApsMonitor.Services;

public static class PasswordHasher
{
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Argon2.Hash(password, timeCost: 2, memoryCost: 19456,
            parallelism: 1, type: Argon2Type.HybridAddressing, hashLength: 32);
    }

    public static bool Verify(string password, string hash)
        => Verify(password, hash, out _);

    public static bool Verify(string password, string hash, out bool needsRehash)
    {
        needsRehash = false;
        if (password is null || string.IsNullOrEmpty(hash) || hash.Length > 512)
            return false;

        if (hash.StartsWith("$argon2id$", StringComparison.Ordinal))
        {
            try
            {
                // Inspect the encoded parameters before allocating Argon2 working memory.
                var config = new Argon2Config();
                if (!config.DecodeString(hash, out var decoded))
                    return false;
                using (decoded)
                {
                    if (decoded is null || config.Version != Argon2Version.Nineteen ||
                        config.MemoryCost < 8 || config.MemoryCost > 65536 ||
                        config.TimeCost < 1 || config.TimeCost > 10 ||
                        config.Lanes < 1 || config.Lanes > 4)
                        return false;
                }

                return Argon2.Verify(hash, password);
            }
            catch (FormatException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        // Temporary compatibility with the original SHA-256/Base64 passwords.
        if (hash.Length != 44)
            return false;
        Span<byte> expected = stackalloc byte[32];
        if (!Convert.TryFromBase64String(hash, expected, out var written) || written != 32)
            return false;

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            Span<byte> actual = stackalloc byte[32];
            SHA256.HashData(passwordBytes, actual);
            var valid = CryptographicOperations.FixedTimeEquals(actual, expected);
            needsRehash = valid;
            return valid;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
