namespace OpenClaw.Core.Abstractions;

public interface IToolResultInterceptor
{
    int Order { get; }
    string Name { get; }

    ValueTask<string> InterceptAsync(ReductionContext context, CancellationToken ct);
}
