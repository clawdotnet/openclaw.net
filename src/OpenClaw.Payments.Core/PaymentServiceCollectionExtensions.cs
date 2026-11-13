using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenClaw.Core.Security;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public static class PaymentServiceCollectionExtensions
{
    public static IServiceCollection AddOpenClawPaymentCore(
        this IServiceCollection services,
        string defaultProviderId = "mock",
        string environment = PaymentEnvironments.Test,
        TimeSpan? secretTtl = null,
        bool allowTestModeWithoutApproval = true,
        bool denyLiveWithoutApprovalService = true,
        long? maxLiveAmountMinor = null,
        string mockProviderId = "mock",
        string mockFundingDisplay = "Mock Visa ending 4242",
        string? idempotencyStoragePath = null)
    {
        _ = PaymentEnvironments.Normalize(environment);
        services.TryAddSingleton<IPaymentSecretVault, InMemoryPaymentSecretVault>();
        services.TryAddSingleton<IPaymentIdempotencyStore>(_ => idempotencyStoragePath is null
            ? new InMemoryPaymentIdempotencyStore()
            : new FilePaymentIdempotencyStore(idempotencyStoragePath));
        services.TryAddSingleton<IPaymentAuditSink, InMemoryPaymentAuditSink>();
        services.TryAddSingleton<IPaymentPolicy>(_ => new DefaultPaymentPolicy(
            allowTestModeWithoutApproval,
            denyLiveWithoutApprovalService,
            maxLiveAmountMinor));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPaymentProvider>(
            new MockPaymentProvider(mockProviderId, mockFundingDisplay)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISensitiveDataRedactor, PaymentSensitiveDataRedactor>());
        services.TryAddSingleton<PaymentRuntimeService>(sp => new PaymentRuntimeService(
            sp.GetServices<IPaymentProvider>(),
            sp.GetRequiredService<IPaymentSecretVault>(),
            sp.GetRequiredService<IPaymentPolicy>(),
            sp.GetRequiredService<IPaymentAuditSink>(),
            sp.GetService<IPaymentApprovalService>(),
            defaultProviderId,
            secretTtl,
            sp.GetRequiredService<IPaymentIdempotencyStore>()));
        services.TryAddSingleton<PaymentSentinelSubstitutionService>();
        return services;
    }
}
