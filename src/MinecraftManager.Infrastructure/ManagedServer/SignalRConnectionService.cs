using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Security;

namespace MinecraftManager.Infrastructure.ManagedServer;

public sealed class SignalRConnectionService(IConfigurationStore configuration, ISecureCredentialStore credentials) : IServerConnectionService
{
    private readonly ConcurrentDictionary<Guid, HubConnection> connections = new();
    private readonly ConcurrentDictionary<Guid, ServerConnectionStatus> statuses = new();
    public event EventHandler<Guid>? RefreshRequested;
    public IReadOnlyDictionary<Guid, ServerConnectionStatus> Statuses => statuses;

    public async Task ConnectAsync(Guid profileId, CancellationToken ct)
    {
        if (connections.ContainsKey(profileId)) return;
        var config = await configuration.LoadAsync(ct);
        var profile = config.ServerProfiles.Single(x => x.Id == profileId);
        statuses[profileId] = ServerConnectionStatus.Connecting;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(profile.BaseUri, "/hubs/client-events"), options =>
            {
                options.AccessTokenProvider = async () => (await credentials.GetAsync(profileId, CancellationToken.None))?.AccessToken;
                options.ApplicationMaxBufferSize = 16 * 1024;
                options.TransportMaxBufferSize = 16 * 1024;
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();
        void Refresh() => RefreshRequested?.Invoke(this, profileId);
        connection.On<UpdateAvailableEvent>("UpdateAvailable", message => { if (Valid(message.EventId, message.OccurredAtUtc)) Refresh(); });
        connection.On<PackAssignmentChangedEvent>("PackAssignmentChanged", message => { if (Valid(message.EventId, message.OccurredAtUtc)) Refresh(); });
        connection.On<DeploymentCancelledEvent>("DeploymentCancelled", message => { if (Valid(message.EventId, message.OccurredAtUtc)) Refresh(); });
        connection.On<RefreshRequestedEvent>("RefreshRequested", message => { if (Valid(message.EventId, message.OccurredAtUtc) && message.Reason.Length <= 256) Refresh(); });
        connection.Reconnecting += _ => { statuses[profileId] = ServerConnectionStatus.Offline; return Task.CompletedTask; };
        connection.Reconnected += _ => { statuses[profileId] = ServerConnectionStatus.Online; Refresh(); return Task.CompletedTask; };
        connection.Closed += _ => { statuses[profileId] = ServerConnectionStatus.Offline; return Task.CompletedTask; };
        if (!connections.TryAdd(profileId, connection)) { await connection.DisposeAsync(); return; }
        try { await connection.StartAsync(ct); statuses[profileId] = ServerConnectionStatus.Online; }
        catch { statuses[profileId] = ServerConnectionStatus.Offline; connections.TryRemove(profileId, out _); await connection.DisposeAsync(); throw; }
    }
    public async Task DisconnectAsync(Guid id, CancellationToken ct)
    {
        if (!connections.TryRemove(id, out var connection)) return;
        await connection.StopAsync(ct); await connection.DisposeAsync(); statuses[id] = ServerConnectionStatus.Offline;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var pair in connections.ToArray()) await pair.Value.DisposeAsync();
        connections.Clear();
    }

    private static bool Valid(Guid eventId, DateTimeOffset occurredAtUtc) => eventId != Guid.Empty && occurredAtUtc > DateTimeOffset.UtcNow.AddDays(-30) && occurredAtUtc < DateTimeOffset.UtcNow.AddMinutes(5);
}
