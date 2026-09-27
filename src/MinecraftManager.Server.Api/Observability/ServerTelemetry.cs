using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MinecraftManager.Server.Api.Observability;

public sealed class ServerTelemetry : IDisposable
{
    public const string Name = "MinecraftManager.Server";
    private readonly Meter meter = new(Name);
    public ActivitySource Activities { get; } = new(Name);
    public Counter<long> Requests { get; }
    public Counter<long> RequestFailures { get; }
    public Histogram<double> RequestDurationMilliseconds { get; }
    public Counter<long> OutboxDeliveries { get; }
    public Counter<long> OutboxFailures { get; }
    public ServerTelemetry()
    {
        Requests = meter.CreateCounter<long>("server.requests");
        RequestFailures = meter.CreateCounter<long>("server.request.failures");
        RequestDurationMilliseconds = meter.CreateHistogram<double>("server.request.duration", "ms");
        OutboxDeliveries = meter.CreateCounter<long>("server.outbox.deliveries");
        OutboxFailures = meter.CreateCounter<long>("server.outbox.failures");
    }
    public void Dispose() { Activities.Dispose(); meter.Dispose(); }
}
