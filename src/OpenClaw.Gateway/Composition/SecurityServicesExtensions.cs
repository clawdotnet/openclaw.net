using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenClaw.Core.ExternalCli;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Security;
using OpenClaw.Gateway.Workflows;

namespace OpenClaw.Gateway.Composition;

internal static class SecurityServicesExtensions
{
    public static IServiceCollection AddOpenClawSecurityServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(sp => new WorkflowCallerContextFactory(startup.Config, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new MetaInvocationStore(
            startup.Config.Memory.StoragePath,
            startup.Config.MetaInvocations.RetentionDays));
        services.AddHostedService(sp => sp.GetRequiredService<MetaInvocationStore>());
        services.AddSingleton(sp => new DeviceEnrollmentService(sp.GetRequiredService<OperatorAccountService>(), TimeProvider.System));
        // Register OIDC/JWT Bearer authentication when an OIDC Authority is configured,
        // regardless of AuthMode. This allows JWT tokens (e.g. from the web chat's
        // OIDC login) to be validated even when AuthMode is "token".
        var hasOidcAuthority = !string.IsNullOrWhiteSpace(startup.Config.Security.Oidc.Authority);
        if (startup.Config.Security.IsOidcMode && !hasOidcAuthority)
        {
            // Program.cs calls UseAuthentication() whenever IsOidcMode is true, which
            // requires IAuthenticationSchemeProvider to be registered here. Without this
            // check the host fails deep inside the middleware pipeline with
            // "UnableToResolveService, ...IAuthenticationSchemeProvider" — an error that
            // points nowhere near the actual cause (an empty Oidc.Authority).
            throw new InvalidOperationException(
                "Security.AuthMode is \"oidc\" but Security.Oidc.Authority is empty. " +
                "Set it to the provider's issuer URL (for Keycloak: " +
                "https://<keycloak-host>/realms/<realm>), or switch AuthMode back to \"token\".");
        }

        if (hasOidcAuthority)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = startup.Config.Security.Oidc.Authority;
                    options.RequireHttpsMetadata = startup.Config.Security.Oidc.RequireHttpsMetadata;

                    var audience = startup.Config.Security.Oidc.Audience;
                    if (string.IsNullOrWhiteSpace(audience))
                    {
                        // OidcConfig.Audience documents "Leave empty to skip validation",
                        // but assigning a null Audience makes the handler throw
                        // IDX10208 ("Unable to validate audience ... ValidAudience is null")
                        // and reject every token. Clear the validator instead.
                        // Keycloak omits `aud` unless an audience mapper is added, so this
                        // is the common case for Keycloak deployments.
                        options.TokenValidationParameters.ValidateAudience = false;
                    }
                    else
                    {
                        options.Audience = audience;
                    }
                });
            services.AddAuthorization();
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var validated = options.Events.OnTokenValidated;
                var failed = options.Events.OnAuthenticationFailed;
                options.Events.OnTokenValidated = async context =>
                {
                    await validated(context);
                    await WorkflowCallerContextFactory.CaptureValidatedTokenAsync(context);
                };
                options.Events.OnAuthenticationFailed = async context =>
                {
                    WorkflowCallerContextFactory.ClearValidatedToken(context.HttpContext);
                    await failed(context);
                };
            });
        }

        services.AddHttpClient<IMcpDelegatedCredentialProvider, McpDelegatedCredentialProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient("workflow-token-exchange", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<WorkflowTokenExchangeClient>();
        services.AddSingleton<IWorkflowAccessTokenProvider, WorkflowAccessTokenProvider>();
        services.AddHttpClient("workflow-backend")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<ToolApprovalService>();
        services.AddSingleton(sp =>
            new PairingManager(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<PairingManager>>()));
        services.AddSingleton(sp => new BrowserSessionAuthService(startup.Config, sp.GetRequiredService<OperatorAccountService>()));
        services.AddSingleton(sp =>
            new OperatorAccountService(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<OperatorAccountService>>()));
        services.AddSingleton(sp =>
            new OrganizationPolicyService(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<OrganizationPolicyService>>()));
        services.AddSingleton(sp =>
            new AdminSettingsService(
                startup.Config,
                startup.BaseAdminSettings ?? AdminSettingsService.CreateSnapshot(startup.Config),
                AdminSettingsService.GetSettingsPath(startup.Config),
                sp.GetRequiredService<ILogger<AdminSettingsService>>()));
        services.AddSingleton(sp =>
            new PluginAdminSettingsService(
                startup.Config,
                sp.GetRequiredService<ILogger<PluginAdminSettingsService>>()));
        services.AddSingleton(sp =>
            new ApprovalAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ApprovalAuditStore>>()));
        services.AddSingleton(sp =>
            new RuntimeEventStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<RuntimeEventStore>>(),
                sp.GetRequiredService<OpenClaw.Core.Observability.RuntimeMetrics>()));
        services.AddSingleton(sp =>
            new ExternalCliAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ExternalCliAuditStore>>()));
        services.AddSingleton<IExternalCliAuditSink>(sp => sp.GetRequiredService<ExternalCliAuditStore>());
        services.AddSingleton<IExternalCliEventSink>(sp =>
            new ExternalCliRuntimeEventSink(sp.GetRequiredService<RuntimeEventStore>()));
        services.AddSingleton(sp =>
            new OperatorAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<OperatorAuditStore>>(),
                sp.GetRequiredService<OpenClaw.Core.Observability.RuntimeMetrics>()));
        services.AddSingleton(sp =>
            new ToolApprovalGrantStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ToolApprovalGrantStore>>()));
        services.AddSingleton(sp =>
            new WebhookDeliveryStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<WebhookDeliveryStore>>()));
        services.AddSingleton(sp =>
            new PluginHealthService(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<PluginHealthService>>(),
                startup.Config.Plugins));
        services.AddSingleton(sp =>
            new ContractStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ContractStore>>()));
        services.AddSingleton(sp =>
            new ContractGovernanceService(
                startup,
                sp.GetRequiredService<ContractStore>(),
                sp.GetRequiredService<RuntimeEventStore>(),
                sp.GetRequiredService<OpenClaw.Core.Observability.ProviderUsageTracker>(),
                sp.GetRequiredService<ILogger<ContractGovernanceService>>()));

        return services;
    }
}
