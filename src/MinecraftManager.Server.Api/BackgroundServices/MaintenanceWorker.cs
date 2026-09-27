using Microsoft.EntityFrameworkCore;
using MinecraftManager.Server.Domain.Entities;
using MinecraftManager.Server.Infrastructure.Persistence;

namespace MinecraftManager.Server.Api.BackgroundServices;

public sealed class MaintenanceWorker(IServiceScopeFactory scopeFactory, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
                var now = DateTimeOffset.UtcNow;
                await db.Set<RegistrationCode>().Where(x => x.ExpiresAtUtc < now.AddDays(-1)).ExecuteDeleteAsync(stoppingToken);
                await db.Set<ClientSession>().Where(x => x.ExpiresAtUtc < now.AddDays(-1)).ExecuteDeleteAsync(stoppingToken);
                await db.Set<IdempotencyRecord>().Where(x => x.ExpiresAtUtc < now).ExecuteDeleteAsync(stoppingToken);
                await db.Set<OutboxMessage>().Where(x => x.PublishedAtUtc < now.AddDays(-7)).ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Maintenance batch failed"); }
        }
    }
}
