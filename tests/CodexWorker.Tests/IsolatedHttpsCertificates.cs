using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CodexWorker;

namespace CodexWorker.Tests;

/// <summary>Per-client trust only; platform chain, time and hostname validation remain enabled.</summary>
internal sealed class IsolatedHttpsCertificates : IDisposable
{
    public X509Certificate2 Root { get; }
    public X509Certificate2 Server { get; }

    public IsolatedHttpsCertificates(string scenario = "trusted")
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Isolated Worker Test CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        Root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Worker Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(scenario == "wrong-host" ? IPAddress.Parse("192.0.2.1") : IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var issued = request.Create(Root, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddHours(scenario == "expired" ? -1 : 1), RandomNumberGenerator.GetBytes(16));
        Server = issued.CopyWithPrivateKey(key);
    }

    public HttpClient CreateClient(bool trustRoot = true)
    {
        var handler = WorkerServerHttpTransport.CreateHandler();
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck
        };
        if (trustRoot) handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(Root);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public void Dispose()
    {
        Server.Dispose();
        Root.Dispose();
    }
}
