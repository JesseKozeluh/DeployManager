using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace DeployManager.Services;

/// <summary>
/// Holds the live TLS certificate. Kestrel's ServerCertificateSelector reads Current on
/// every new connection, so installing a renewed certificate takes effect immediately
/// without a process/container restart.
/// </summary>
public static class CertificateHolder
{
    private static X509Certificate2? _current;
    public static X509Certificate2? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}

public record CertInfo(
    string   Subject,
    string   Issuer,
    string[] SubjectAltNames,
    DateTime NotBefore,
    DateTime NotAfter,
    int      DaysRemaining,
    string   Thumbprint,
    bool     IsSelfSigned,
    bool     HasPendingRequest);

/// <summary>
/// Manages the web server's TLS certificate: self-signed bootstrap, CSR generation for an
/// external CA, and installation of the signed certificate. CA-agnostic by design — the
/// signing happens outside the app, so it works with AD CS, any internal CA, or a public CA.
/// The pending private key is held encrypted at rest (AES-256) until the signed cert arrives.
/// </summary>
public class CertificateService
{
    private readonly IEncryptionService _enc;
    private readonly IAuditService _audit;
    private readonly ILogger<CertificateService> _log;
    private readonly string _dataPath;
    private readonly string _serverIp;
    private readonly object _lock = new();

    private string PfxPath        => Path.Combine(_dataPath, "deploymgr.pfx");
    private string PfxKeyPath     => Path.Combine(_dataPath, "deploymgr.pfx.key");
    private string PendingKeyPath => Path.Combine(_dataPath, "csr-pending.key.enc");
    private string PendingCsrPath => Path.Combine(_dataPath, "csr-pending.csr");

    // MachineKeySet is required: the service runs as LocalSystem and SChannel
    // must find the private key in the machine store to complete TLS
    // handshakes. With user-profile key storage the listener accepts TCP but
    // every handshake fails ("connection closed unexpectedly").
    private const X509KeyStorageFlags LoadFlags =
        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet;

    public CertificateService(IConfiguration config, IEncryptionService enc, IAuditService audit,
                              ILogger<CertificateService> log)
    {
        _enc = enc; _audit = audit; _log = log;
        _dataPath = Environment.ExpandEnvironmentVariables(
                        config["DeployManager:DataPath"] ?? @"%ProgramData%\DeployManager\data");
        _serverIp = config["DeployManager:ServerIp"] ?? "";
        Directory.CreateDirectory(_dataPath);
        CertificateHolder.Current = LoadOrCreateActive();
    }

    // ── Active certificate ──────────────────────────────────────────────────────
    private X509Certificate2 LoadOrCreateActive()
    {
        if (File.Exists(PfxPath) && File.Exists(PfxKeyPath))
        {
            try
            {
                var pass = File.ReadAllText(PfxKeyPath).Trim();
                var c = new X509Certificate2(PfxPath, pass, LoadFlags);
                _log.LogInformation("Loaded TLS certificate: subject={Subject}, issuer={Issuer}, expires={Expiry:yyyy-MM-dd}",
                    c.Subject, c.Issuer, c.NotAfter);
                return c;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to load existing TLS certificate — regenerating a self-signed one.");
            }
        }
        return GenerateSelfSigned();
    }

    private X509Certificate2 GenerateSelfSigned()
    {
        var pass = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=OSDeploy DeployManager,O=DeployManager,OU=IT",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // TLS server auth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        if (!string.IsNullOrEmpty(_serverIp) && IPAddress.TryParse(_serverIp, out var ip)) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var pfx  = cert.Export(X509ContentType.Pfx, pass);
        WriteBytes(PfxPath, pfx);
        WriteHidden(PfxKeyPath, pass);
        return new X509Certificate2(pfx, pass, LoadFlags);
    }

    // ── Status ──────────────────────────────────────────────────────────────────
    public CertInfo GetInfo()
    {
        var c = CertificateHolder.Current!;
        var selfSigned = c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData);
        var days = (int)Math.Floor((c.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays);
        return new CertInfo(c.Subject, c.Issuer, ExtractSans(c), c.NotBefore, c.NotAfter, days,
                            c.Thumbprint, selfSigned, File.Exists(PendingKeyPath));
    }

    public string? GetPendingCsr() => File.Exists(PendingCsrPath) ? File.ReadAllText(PendingCsrPath) : null;

    private static string[] ExtractSans(X509Certificate2 c)
    {
        foreach (var ext in c.Extensions)
        {
            if (ext.Oid?.Value == "2.5.29.17")
            {
                try
                {
                    return ext.Format(true)
                              .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                }
                catch { }
            }
        }
        return Array.Empty<string>();
    }

    // ── CSR generation ──────────────────────────────────────────────────────────
    public string GenerateCsr(string commonName, IEnumerable<string> dnsNames, IEnumerable<string> ipAddresses,
                              int keySize, string? org, string? ou, string? locality, string? state,
                              string? country, string actor)
    {
        lock (_lock)
        {
            var subject = BuildSubject(commonName, org, ou, locality, state, country);
            using var rsa = RSA.Create(keySize is 2048 or 4096 ? keySize : 4096);

            var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

            var san = new SubjectAlternativeNameBuilder();
            var sanCount = 0;
            foreach (var d in dnsNames.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                san.AddDnsName(d.Trim()); sanCount++;
            }
            foreach (var ipStr in ipAddresses.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (IPAddress.TryParse(ipStr.Trim(), out var ip)) { san.AddIpAddress(ip); sanCount++; }
            }
            if (sanCount > 0) req.CertificateExtensions.Add(san.Build());

            var csrDer = req.CreateSigningRequest();
            var csrPem = PemEncoding.WriteString("CERTIFICATE REQUEST", csrDer);

            // Persist the pending private key, encrypted at rest, until the signed cert is uploaded.
            var keyB64 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
            WriteHidden(PendingKeyPath, _enc.Protect(keyB64));
            File.WriteAllText(PendingCsrPath, csrPem);

            _audit.Log(new AuditEvent("CERT_CSR_GENERATED", actor, true, $"Subject={subject}; KeySize={keySize}"));
            return csrPem;
        }
    }

    // ── Install signed certificate ──────────────────────────────────────────────
    public (bool Ok, string Message) InstallSignedCert(string certPem, string? chainPem, string actor)
    {
        lock (_lock)
        {
            if (!File.Exists(PendingKeyPath))
                return (false, "No pending signing request. Generate a CSR first, sign it, then upload here.");

            X509Certificate2 leaf;
            try { leaf = X509Certificate2.CreateFromPem(certPem); }
            catch (Exception ex) { return (false, "Could not parse the certificate (expected PEM / Base64 .cer): " + ex.Message); }

            RSA rsa;
            try
            {
                var keyB64 = _enc.Unprotect(File.ReadAllText(PendingKeyPath).Trim());
                rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(keyB64), out _);
            }
            catch (Exception ex) { return (false, "Could not load the pending private key: " + ex.Message); }

            using (rsa)
            using (leaf)
            {
                using var certPub = leaf.GetRSAPublicKey();
                if (certPub == null ||
                    !certPub.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(rsa.ExportSubjectPublicKeyInfo()))
                {
                    return (false, "The uploaded certificate's public key does not match the pending request — " +
                                   "make sure you signed the CSR generated here (and didn't generate a new one since).");
                }

                using var certWithKey = leaf.CopyWithPrivateKey(rsa);
                var collection = new X509Certificate2Collection { certWithKey };
                foreach (var c in ParsePemCerts(chainPem)) collection.Add(c);

                var pass = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                var pfx  = collection.Export(X509ContentType.Pfx, pass)!;
                WriteBytes(PfxPath, pfx);
                WriteHidden(PfxKeyPath, pass);

                CertificateHolder.Current = new X509Certificate2(pfx, pass, LoadFlags);

                TryDelete(PendingKeyPath);
                TryDelete(PendingCsrPath);

                _audit.Log(new AuditEvent("CERT_INSTALLED", actor, true,
                    $"Subject={leaf.Subject}; Issuer={leaf.Issuer}; Expires={leaf.NotAfter:yyyy-MM-dd}"));
                return (true, $"Certificate installed — issued by {leaf.Issuer}, expires {leaf.NotAfter:yyyy-MM-dd}. " +
                              "New HTTPS connections use it immediately.");
            }
        }
    }

    public (bool Ok, string Message) RevertToSelfSigned(string actor)
    {
        lock (_lock)
        {
            TryDelete(PfxPath); TryDelete(PfxKeyPath);
            TryDelete(PendingKeyPath); TryDelete(PendingCsrPath);
            CertificateHolder.Current = GenerateSelfSigned();
            _audit.Log(new AuditEvent("CERT_REVERTED_SELFSIGNED", actor, true, null));
            return (true, "Reverted to a self-signed certificate. New HTTPS connections use it immediately.");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────
    private static List<X509Certificate2> ParsePemCerts(string? pem)
    {
        var list = new List<X509Certificate2>();
        if (string.IsNullOrWhiteSpace(pem)) return list;
        foreach (Match m in Regex.Matches(pem,
                     "-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----", RegexOptions.Singleline))
        {
            try { list.Add(X509Certificate2.CreateFromPem(m.Value)); } catch { }
        }
        return list;
    }

    private static string BuildSubject(string cn, string? o, string? ou, string? l, string? st, string? c)
    {
        var parts = new List<string> { $"CN={Escape(cn)}" };
        if (!string.IsNullOrWhiteSpace(ou)) parts.Add($"OU={Escape(ou!)}");
        if (!string.IsNullOrWhiteSpace(o))  parts.Add($"O={Escape(o!)}");
        if (!string.IsNullOrWhiteSpace(l))  parts.Add($"L={Escape(l!)}");
        if (!string.IsNullOrWhiteSpace(st)) parts.Add($"S={Escape(st!)}");
        if (!string.IsNullOrWhiteSpace(c))  parts.Add($"C={Escape(c!)}");
        return string.Join(", ", parts);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(",", "\\,").Replace("=", "\\=");

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    // Windows refuses File.WriteAllText/Bytes over an existing *Hidden* file
    // (UnauthorizedAccessException), so clear the attribute before overwriting.
    private static void WriteHidden(string path, string text)
    {
        if (File.Exists(path)) { try { File.SetAttributes(path, FileAttributes.Normal); } catch { } }
        File.WriteAllText(path, text);
        try { File.SetAttributes(path, FileAttributes.Hidden); } catch { }
    }

    private static void WriteBytes(string path, byte[] data)
    {
        if (File.Exists(path)) { try { File.SetAttributes(path, FileAttributes.Normal); } catch { } }
        File.WriteAllBytes(path, data);
    }
}
