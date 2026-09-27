using System.Text.Json;
using OpenClaw.LayaService.Protocol;

namespace OpenClaw.LayaService.Hosting;

public sealed record ServiceOptions(int Port, int MaxConnections, int MaxRequestBytes, TimeSpan RequestTimeout);

public interface IDecisionPredictor
{
    string Model { get; }

    JsonElement GetHealth();

    Task<JsonElement> PredictAsync(DecisionWireRequest request, CancellationToken cancellationToken);
}