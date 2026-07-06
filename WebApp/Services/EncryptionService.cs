using System.Security.Cryptography;
using System.Text;

namespace DeployManager.Services;

public interface IEncryptionService
{
    string Protect(string plaintext);
    string Unprotect(string value);
    bool IsProtected(string value);
}

/// <summary>
/// AES-256-GCM authenticated encryption for secrets stored on disk.
/// Key is generated once at first startup and stored at %ProgramData%\DeployManager\data\enc.key.
/// Supports transparent migration: Unprotect accepts legacy plaintext values.
/// </summary>
public class EncryptionService : IEncryptionService
{
    private const string Prefix = "AES256GCM:";
    private readonly byte[] _key;

    public EncryptionService(IConfiguration config, ILogger<EncryptionService> logger)
    {
        var dataPath = Environment.ExpandEnvironmentVariables(
                           config["DeployManager:DataPath"] ?? @"%ProgramData%\DeployManager\data");
        Directory.CreateDirectory(dataPath);
        var keyFile = Path.Combine(dataPath, "enc.key");

        if (File.Exists(keyFile))
        {
            _key = File.ReadAllBytes(keyFile);
            if (_key.Length != 32)
                throw new InvalidOperationException(
                    "enc.key is corrupt (expected 32 bytes). Delete it to regenerate — this will invalidate all stored secrets and require re-entering passwords in Settings.");
            logger.LogInformation("Encryption key loaded from {KeyFile}", keyFile);
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyFile, _key);
            File.SetAttributes(keyFile, FileAttributes.Hidden | FileAttributes.ReadOnly);
            logger.LogWarning("Generated new AES-256 encryption key at {KeyFile}. Back this file up securely — losing it means stored secrets cannot be decrypted.", keyFile);
        }
    }

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext)) return plaintext;

        var pt    = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);        // GCM standard nonce size
        var ct    = new byte[pt.Length];
        var tag   = new byte[16];                              // GCM authentication tag

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, pt, ct, tag);

        // Wire format: nonce(12) || tag(16) || ciphertext(n)
        var blob = new byte[28 + ct.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, 12);
        ct.CopyTo(blob, 28);

        return Prefix + Convert.ToBase64String(blob);
    }

    public string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!IsProtected(value)) return value; // Legacy plaintext — return as-is for migration

        var blob = Convert.FromBase64String(value[Prefix.Length..]);
        if (blob.Length < 28)
            throw new CryptographicException("Encrypted value is malformed — blob too short.");

        var nonce = blob[..12];
        var tag   = blob[12..28];
        var ct    = blob[28..];
        var pt    = new byte[ct.Length];

        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, ct, tag, pt); // Throws CryptographicException if tag is invalid

        return Encoding.UTF8.GetString(pt);
    }

    public bool IsProtected(string value) => value?.StartsWith(Prefix) == true;
}
