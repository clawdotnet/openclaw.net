using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;

namespace OpenClaw.Gateway;

internal sealed partial class AdminSettingsService
{
    private readonly string _configurationEpoch = Guid.NewGuid().ToString("N");
    // Credentials and complex worker configuration stay in the dedicated secure setup flow.
    private static readonly HashSet<string> PrivateConfigurationFields = new(StringComparer.Ordinal)
    {
        "whatsappWebhookVerifyToken", "whatsappWebhookAppSecret", "whatsappCloudApiToken",
        "whatsappBridgeToken", "whatsappFirstPartyWorker", "modelProvider"
    };

    public ConfigurationState DescribeConfiguration()
    {
        lock (_gate) return DescribeConfigurationCore();
    }

    private ConfigurationState DescribeConfigurationCore()
    {
        var snapshot = GetSnapshot();
        var restart = GetRestartRequiredChanges(_runningSnapshot, snapshot);
        var json = JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.AdminSettingsSnapshot);
        using var doc = JsonDocument.Parse(json);
        return new()
        {
            Success = true,
            RestartRequired = restart.Count > 0,
            RestartRequiredFields = restart.ToArray(),
            Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_configurationEpoch + json))),
            Message = "Settings loaded. Provider credentials and worker accounts use dedicated setup. Model name edits the base model; named profiles keep their own model settings.",
            Values = doc.RootElement.EnumerateObject().Where(p => !PrivateConfigurationFields.Contains(p.Name))
                .ToDictionary(p => p.Name, p => p.Value.Clone())
        };
    }

    public ConfigurationState ChangeConfiguration(ConfigurationRequest request, bool apply)
    {
        lock (_gate)
        {
            var current = DescribeConfigurationCore();
            ConfigurationState Fail(string message)
            {
                current.Success = false;
                current.Errors = [message];
                current.Message = message;
                return current;
            }
            if (request.Revision != current.Revision) return Fail("Settings changed. Reload and review a fresh proposal before applying.");
            if (request.Changes is null || request.Changes.Count is < 1 or > 64) return Fail("Choose between 1 and 64 settings to change.");
            var node = JsonSerializer.SerializeToNode(GetSnapshot(), CoreJsonContext.Default.AdminSettingsSnapshot)!.AsObject();
            foreach (var (key, value) in request.Changes)
            {
                if (key == "modelName" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MODEL_PROVIDER_MODEL")))
                    return Fail("The base model is controlled by MODEL_PROVIDER_MODEL. Update that environment variable and restart instead.");
                if (!current.Values.TryGetValue(key, out var existing)) return Fail($"Unknown or protected setting: {key}.");
                if (value.ValueKind != existing.ValueKind && !(existing.ValueKind is JsonValueKind.True or JsonValueKind.False && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    && !(existing.ValueKind == JsonValueKind.Null && value.ValueKind == JsonValueKind.String))
                    return Fail($"The value for {key} has the wrong type.");
                var choices = ConfigurationSettingChoices.For(key);
                if (choices.Count > 0 && (value.ValueKind != JsonValueKind.String || !choices.Contains(value.GetString(), StringComparer.Ordinal)))
                    return Fail($"Choose a supported value for {key}: {string.Join(", ", choices)}.");
                if (value.GetRawText().Length > 4096) return Fail($"The value for {key} is too long.");
                node[key] = JsonNode.Parse(value.GetRawText());
            }
            AdminSettingsSnapshot proposed;
            try { proposed = node.Deserialize(CoreJsonContext.Default.AdminSettingsSnapshot)!; }
            catch (JsonException) { return Fail("A setting contains an invalid value."); }
            var clone = CloneConfig(_config);
            ApplySnapshot(clone, proposed);
            var errors = ConfigValidator.Validate(clone).ToList();
            try
            {
                OpenClaw.Gateway.Extensions.GatewaySecurityExtensions.EnforcePublicBindHardening(clone,
                    !GatewaySecurity.IsLoopbackBind(clone.BindAddress));
            }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); }
            if (errors.Count > 0)
            {
                current.Success = false;
                current.Errors = errors.ToArray();
                current.Message = "Please correct the settings before applying.";
                return current;
            }
            var restart = GetRestartRequiredChanges(_runningSnapshot, proposed);
            if (apply)
            {
                var result = Update(proposed);
                if (!result.Success) return Fail(string.Join(" ", result.Errors));
                current = DescribeConfigurationCore();
                restart = result.RestartRequiredFields.ToList();
            }
            current.Changes = request.Changes;
            current.RestartRequired = restart.Count > 0;
            current.RestartRequiredFields = restart.ToArray();
            current.Message = apply
                ? restart.Count > 0 ? "Settings saved. Restart the gateway to activate the listed changes." : "Settings saved and applied."
                : "Review these changes. Nothing has been saved yet.";
            return current;
        }
    }
}
