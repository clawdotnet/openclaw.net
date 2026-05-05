using OpenClaw.Payments.Abstractions;
using OpenClaw.Payments.Core;

namespace OpenClaw.Plugins.Payment;

public static class PaymentPluginRegistration
{
    public static PaymentTool CreateTool(
        PaymentRuntimeService runtime,
        string providerId,
        string environment = PaymentEnvironments.Test)
        => new(runtime, providerId, environment);
}
