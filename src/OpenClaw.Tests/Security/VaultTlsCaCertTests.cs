using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultTlsCaCertTests : IDisposable
{
    private readonly string _tempDir;

    public VaultTlsCaCertTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("openclaw-cacert-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_ValidPemFile_ReturnsCert()
    {
        var cert = CreateSelfSigned();
        var pemPath = Path.Combine(_tempDir, "ca.pem");
        File.WriteAllText(pemPath, cert.ExportCertificatePem());

        var bundle = VaultCaCertLoader.Load(pemPath);

        Assert.Single(bundle);
        Assert.Equal(cert.Thumbprint, bundle[0].Thumbprint);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        Assert.Throws<SecretResolutionException>(() =>
            VaultCaCertLoader.Load(Path.Combine(_tempDir, "nope.pem")));
    }

    [Fact]
    public void Load_EmptyDirectory_Throws()
    {
        Assert.Throws<SecretResolutionException>(() => VaultCaCertLoader.Load(_tempDir));
    }

    [Fact]
    public void Load_Directory_LoadsAllPems()
    {
        var c1 = CreateSelfSigned();
        var c2 = CreateSelfSigned();
        File.WriteAllText(Path.Combine(_tempDir, "a.pem"), c1.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_tempDir, "b.pem"), c2.ExportCertificatePem());

        var bundle = VaultCaCertLoader.Load(_tempDir);

        Assert.Equal(2, bundle.Count);
    }

    [Fact]
    public void Load_MalformedPem_Throws()
    {
        var bad = Path.Combine(_tempDir, "bad.pem");
        File.WriteAllText(bad, "this is not a certificate");

        Assert.Throws<SecretResolutionException>(() => VaultCaCertLoader.Load(bad));
    }

    [Fact]
    public void Validator_TrustsCertChainingToBundleRoot()
    {
        var root = CreateSelfSigned();
        var roots = new X509Certificate2Collection(root);

        var validator = VaultCaCertLoader.BuildServerCertificateValidator(roots);

        var ok = validator(null!, root, null, SslPolicyErrors.RemoteCertificateChainErrors);
        Assert.True(ok);
    }

    [Fact]
    public void Validator_RejectsCertOutsideBundle()
    {
        var root = CreateSelfSigned();
        var stranger = CreateSelfSigned();
        var roots = new X509Certificate2Collection(root);

        var validator = VaultCaCertLoader.BuildServerCertificateValidator(roots);

        Assert.False(validator(null!, stranger, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Validator_RejectsNameMismatch()
    {
        var root = CreateSelfSigned();
        var validator = VaultCaCertLoader.BuildServerCertificateValidator(new X509Certificate2Collection(root));

        Assert.False(validator(null!, root, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    private X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=openclaw-test-ca", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), password: null);
    }

    [Fact]
    public void Validator_RejectsClientOnlyCertificateFromTrustedRoot()
    {
        using var root = CreateSelfSigned();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=vault", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var clientOnly = request.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), [1, 2, 3]);
        var validator = VaultCaCertLoader.BuildServerCertificateValidator(new(root));
        Assert.False(validator(null!, clientOnly, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
