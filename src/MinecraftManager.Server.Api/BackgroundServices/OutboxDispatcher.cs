using Microsoft.EntityFrameworkCore;
using MinecraftManager.Server.Application.Abstractions;
using MinecraftManager.Server.Infrastructure.Persistence;

namespace MinecraftManager.Server.Api.BackgroundServices;

public sealed class OutboxDispatcher(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcher> logger, MinecraftManager.Server.Api.Observability.ServerTelemetry telemetry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await DispatchBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Outbox dispatch batch failed"); }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        var notifier = scope.ServiceProvider.GetRequiredService<IClientNotifier>();
        var now = DateTimeOffset.UtcNow;
        var messages = await db.Set<Domain.Entities.OutboxMessage>().Where(x => x.PublishedAtUtc == null && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now)).OrderBy(x => x.CreatedAtUtc).Take(50).ToListAsync(cancellationToken);
        foreach (var message in messages)
        {
            try { await notifier.SendAsync(message.GroupName, message.Type, message.PayloadJson, cancellationToken); message.PublishedAtUtc = now; message.LastError = null; telemetry.OutboxDeliveries.Add(1); }
            catch (Exception ex) { message.Attempts++; message.LastError = ex.GetType().Name; message.NextAttemptAtUtc = now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(message.Attempts, 8)))); telemetry.OutboxFailures.Add(1); }
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
