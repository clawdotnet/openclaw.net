using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenClaw.Core.Models;

namespace OpenClaw.Tests;

internal sealed class WorkflowDelegationTestServer : IAsyncDisposable
{
    private readonly RSA _signing = RSA.Create(2048);
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _leaf;
    private readonly string _directory = Path.Join(Path.GetTempPath(), "workflow-https-tests", Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    public Uri Authority { get; private set; } = null!;
    public Uri TokenEndpoint => new(Authority, "token");
    public Uri BackendBaseUrl => Authority;
    public string TrustBundlePath => Path.Join(_directory, "root.pem");
    public RsaSecurityKey SigningKey => new(_signing) { KeyId = "workflow-test-key" };
    public ConcurrentQueue<WorkflowCapturedBackendRequest> BackendRequests { get; } = new();
    public ConcurrentQueue<WorkflowCapturedExchangeRequest> ExchangeRequests { get; } = new();
    public string? OAuthError { get; set; }
    public int BackendStatusCode { get; set; } = 200;

    private WorkflowDelegationTestServer()
    {
        Directory.CreateDirectory(_directory);
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Workflow Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        _root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(TrustBundlePath, _root.ExportCertificatePem());
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], true));
        using var issued = leafRequest.Create(_root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(leafKey);
        _leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), password: null);
    }

    public static async Task<WorkflowDelegationTestServer> StartAsync(CancellationToken ct)
    {
        var server = new WorkflowDelegationTestServer();
        try
        {
            await server.StartCoreAsync(ct);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(_leaf)));
        _app = builder.Build();
        _app.MapGet("/.well-known/openid-configuration", () => Results.Text(
            $$"""{"issuer":"{{Authority}}","jwks_uri":"{{new Uri(Authority, "jwks")}}","token_endpoint":"{{TokenEndpoint}}"}""", "application/json"));
        _app.MapGet("/jwks", () =>
        {
            var parameters = _signing.ExportParameters(false);
            return Results.Text($$"""{"keys":[{"kty":"RSA","use":"sig","kid":"workflow-test-key","alg":"RS256","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"}]}""", "application/json");
        });
        _app.MapPost("/token", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            ExchangeRequests.Enqueue(new(form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()), ctx.Request.Headers.Authorization.ToString()));
            if (OAuthError is not null)
                return Results.Text($$"""{"error":"{{OAuthError}}","error_description":"private-provider-error"}""", "application/json", statusCode: 400);
            var assertion = form["assertion"].FirstOrDefault() ?? form["subject_token"].FirstOrDefault()!;
            var subject = new JsonWebToken(assertion).Subject;
            return Results.Text($$"""{"access_token":"delegated-{{subject}}","token_type":"Bearer","expires_in":300,"issued_token_type":"urn:ietf:params:oauth:token-type:access_token"}""", "application/json");
        });
        _app.MapMethods("/api/workflows/{workflow}/{operation}/{runId?}", ["GET", "POST"], async (HttpContext ctx, string operation) =>
        {
            AgentWorkflowRequest? request = null;
            AgentWorkflowResponse? response = null;
            if (operation == "run")
                request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.AgentWorkflowRequest, ctx.RequestAborted);
            if (operation == "respond")
                response = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.AgentWorkflowResponse, ctx.RequestAborted);
            BackendRequests.Enqueue(new(operation, ctx.Request.Headers.Authorization.ToString(), request, response));
            return Results.Text("""{"workflowId":"review","runId":"run-1","status":"completed","events":[],"pendingInputs":[]}""",
                "application/json", statusCode: BackendStatusCode);
        });
        await _app.StartAsync(ct);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Authority = new(address.TrimEnd('/') + "/");
        using var probe = new HttpClient(CreateTrustedHandler());
        using var readiness = await probe.GetAsync(new Uri(Authority, ".well-known/openid-configuration"), ct);
        readiness.EnsureSuccessStatusCode();
    }

    public string IssueUserToken(string subject, bool eligible) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Authority.ToString(), Audience = "gateway-api",
        Claims = new Dictionary<string, object>
        {
            ["sub"] = subject, ["scp"] = eligible ? "workflow.read" : "",
            ["roles"] = new[] { "admin" }, ["typ"] = "Bearer", ["actor_kind"] = eligible ? "user" : "service"
        },
        NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new(SigningKey, SecurityAlgorithms.RsaSha256)
    });

    public HttpClientHandler CreateTrustedHandler() => new()
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate?.GetCertHashString() == _leaf.GetCertHashString()
    };

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _signing.Dispose();
        _leaf.Dispose();
        _root.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

internal sealed record WorkflowCapturedBackendRequest(
    string Operation, string Authorization, AgentWorkflowRequest? StartRequest, AgentWorkflowResponse? Response);
internal sealed record WorkflowCapturedExchangeRequest(IReadOnlyDictionary<string, string> Form, string? Authorization);
