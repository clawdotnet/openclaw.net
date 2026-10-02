using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.Mcp;

public sealed class McpDelegatedToolInvoker(
    IMcpDelegatedCredentialProvider credentialProvider,
    McpDelegatedHttpClientFactory clientFactory) : IMcpDelegatedToolInvoker
{
    public async Task<McpDelegatedToolCallResult> InvokeAsync(McpDelegatedToolCallRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = request.CallerCredentialContext
            ?? throw Failure("MCP_DELEGATED_CALLER_MISSING");
        EnsureNotExpired(caller.ExpiresAtUtc, "MCP_DELEGATED_CALLER_EXPIRED");
        if (!request.Policy.Enabled)
            throw Failure("MCP_DELEGATED_POLICY_DISABLED");

        McpDelegatedCredential credential;
        try
        {
            credential = await credentialProvider.GetCredentialAsync(request.Policy, caller, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw Failure("MCP_DELEGATED_CREDENTIAL_PROVIDER_FAILED");
        }

        EnsureNotExpired(caller.ExpiresAtUtc, "MCP_DELEGATED_CALLER_EXPIRED");
        if (string.IsNullOrWhiteSpace(credential.AccessToken))
            throw Failure("MCP_DELEGATED_CREDENTIAL_EMPTY");
        EnsureNotExpired(credential.ExpiresAtUtc, "MCP_DELEGATED_CREDENTIAL_EXPIRED");

        McpClient client;
        try
        {
            client = await clientFactory.CreateAsync(
                request.EndpointId,
                request.Endpoint,
                request.StaticHeaders,
                request.RequestTimeoutSeconds,
                credential.AccessToken,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (GetAuthorizationFailureCode(ex) is { } failureCode)
        {
            throw Failure(failureCode);
        }
        catch
        {
            throw Failure("MCP_DELEGATED_CLIENT_FAILED");
        }

        await using (client.ConfigureAwait(false))
        {
            using var argumentsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
            if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("MCP tool arguments must be a JSON object.", nameof(request));

            var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in argumentsDocument.RootElement.EnumerateObject())
                arguments[property.Name] = property.Value.Clone();

            EnsureNotExpired(caller.ExpiresAtUtc, "MCP_DELEGATED_CALLER_EXPIRED");
            EnsureNotExpired(credential.ExpiresAtUtc, "MCP_DELEGATED_CREDENTIAL_EXPIRED");

            CallToolResult response;
            try
            {
                response = await client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
                    RequestMethods.ToolsCall,
                    new CallToolRequestParams { Name = request.RemoteToolName, Arguments = arguments },
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (GetAuthorizationFailureCode(ex) is { } failureCode)
            {
                throw Failure(failureCode);
            }
            catch
            {
                throw Failure("MCP_DELEGATED_UPSTREAM_FAILED");
            }

            return new McpDelegatedToolCallResult
            {
                ResponseText = FormatResponseContent(response, request.SuppressStructuredContent),
                IsError = response.IsError ?? false
            };
        }
    }

    private static void EnsureNotExpired(DateTimeOffset expiresAtUtc, string failureCode)
    {
        if (expiresAtUtc <= DateTimeOffset.UtcNow)
            throw Failure(failureCode);
    }

    private static string? GetAuthorizationFailureCode(HttpRequestException exception)
        => exception.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "MCP_DELEGATED_UPSTREAM_UNAUTHORIZED",
            HttpStatusCode.Forbidden => "MCP_DELEGATED_UPSTREAM_FORBIDDEN",
            _ => null
        };

    private static McpDelegatedToolInvocationException Failure(string failureCode) => new(failureCode);

    private static string FormatResponseContent(CallToolResult response, bool suppressStructuredContent)
    {
        var parts = new List<string>();
        foreach (var item in response.Content ?? [])
        {
            switch (item)
            {
                case TextContentBlock textBlock when !string.IsNullOrEmpty(textBlock.Text):
                    parts.Add(textBlock.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource } when !string.IsNullOrEmpty(resource.Text):
                    parts.Add(resource.Text);
                    break;
                default:
                    parts.Add(JsonSerializer.Serialize(item, McpDelegatedToolSerializerContext.Default.ContentBlock));
                    break;
            }
        }

        if (!suppressStructuredContent &&
            response.StructuredContent is { } structuredContent &&
            structuredContent.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            parts.Add(structuredContent.GetRawText());
        }

        return string.Join("\n\n", parts);
    }
}

[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(TextContentBlock))]
[JsonSerializable(typeof(ImageContentBlock))]
[JsonSerializable(typeof(AudioContentBlock))]
[JsonSerializable(typeof(EmbeddedResourceBlock))]
[JsonSerializable(typeof(ResourceLinkBlock))]
[JsonSerializable(typeof(ToolUseContentBlock))]
[JsonSerializable(typeof(ToolResultContentBlock))]
[JsonSerializable(typeof(ResourceContents))]
[JsonSerializable(typeof(TextResourceContents))]
[JsonSerializable(typeof(BlobResourceContents))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal sealed partial class McpDelegatedToolSerializerContext : JsonSerializerContext;