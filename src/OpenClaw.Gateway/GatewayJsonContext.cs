using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace OpenClaw.Gateway;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OperatorAccountService.StoreState), TypeInfoPropertyName = "OperatorAccountStoreState")]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(Workflows.WorkflowOAuthTokenResponse))]
internal partial class GatewayJsonContext : JsonSerializerContext;
