using System.Collections.Concurrent;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public sealed class InMemoryPaymentAuditSink : IPaymentAuditSink
{
    private readonly ConcurrentQueue<PaymentAuditEvent> _events = new();

    public IReadOnlyList<PaymentAuditEvent> Snapshot() => _events.ToArray();

    public ValueTask RecordAsync(PaymentAuditEvent auditEvent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _events.Enqueue(auditEvent);
        return ValueTask.CompletedTask;
    }
}
