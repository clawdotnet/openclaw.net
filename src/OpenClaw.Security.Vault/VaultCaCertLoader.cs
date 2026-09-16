using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

/// <summary>
/// Loads a custom CA certificate bundle (single PEM file, or a directory of
/// .pem/.crt/.cer files) and builds a server-certificate validator that trusts
/// exactly those roots (X509Chain custom root trust, revocation check off).
/// </summary>
public static class VaultCaCertLoader
{
    private static readonly string[] CertificateExtensions = [".pem", ".crt", ".cer"];

    public static X509Certificate2Collection Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];

        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        string[] files;
        if (Directory.Exists(fullPath))
        {
            files = Directory.EnumerateFiles(fullPath)
                .Where(f => CertificateExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (files.Length == 0)
                throw new SecretResolutionException("Vault TLS CA bundle directory contains no certificate files.");
        }
        else
        {
            files = [fullPath];
        }

        var bundle = new X509Certificate2Collection();
        foreach (var file in files)
        {
            if (!File.Exists(file))
                throw new SecretResolutionException($"Vault TLS CA bundle file not found: '{file}'.");
            try
            {
                var before = bundle.Count;
                bundle.ImportFromPemFile(file);
                if (bundle.Count == before)
                    throw new SecretResolutionException(
                        $"Vault TLS CA bundle file contains no PEM certificates: '{file}'.");
            }
            catch (Exception ex) when (ex is not SecretResolutionException)
            {
                throw new SecretResolutionException($"Vault TLS CA bundle file is not a valid PEM certificate: '{file}'.", ex);
            }
        }
        return bundle;
    }

    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool> BuildServerCertificateValidator(
        X509Certificate2Collection roots)
    {
        return (_, serverCertificate, suppliedChain, errors) =>
        {
            // Hostname and availability stay delegated to the platform's own checks.
            if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
                return false;
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                return false;

            if (serverCertificate is not X509Certificate2 cert)
                return false;

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
            if (suppliedChain is not null)
                foreach (var element in suppliedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            return chain.Build(cert);
        };
    }
}
