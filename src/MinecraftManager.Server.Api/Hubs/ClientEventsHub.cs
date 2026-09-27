using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MinecraftManager.Server.Application.Abstractions;
using MinecraftManager.Server.Contracts.V1;

namespace MinecraftManager.Server.Api.Hubs;

[Authorize(Policy = "Device")]
public sealed class ClientEventsHub(MinecraftManager.Server.Infrastructure.Persistence.ServerDbContext db) : Hub<IClientEvents>
{
    public override async Task OnConnectedAsync()
    {
        var machineId = Context.User?.FindFirst("machine_id")?.Value ?? throw new HubException("Device identity is unavailable.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"machine:{machineId}");
        if (Guid.TryParse(machineId, out var id))
            foreach (var instanceId in await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Set<MinecraftManager.Server.Domain.Entities.MinecraftInstance>().Where(x => x.MachineId == id).Select(x => x.Id)))
                await Groups.AddToGroupAsync(Context.ConnectionId, $"instance:{instanceId}");
        await base.OnConnectedAsync();
    }
}

public sealed class SignalRClientNotifier(IHubContext<ClientEventsHub, IClientEvents> hub) : IClientNotifier
{
    public async Task SendAsync(string groupName, string eventType, string payloadJson, CancellationToken cancellationToken)
    {
        var client = hub.Clients.Group(groupName);
        switch (eventType)
        {
            case "UpdateAvailable": await client.UpdateAvailable(System.Text.Json.JsonSerializer.Deserialize<UpdateAvailableEvent>(payloadJson)!); break;
            case "PackAssignmentChanged": await client.PackAssignmentChanged(System.Text.Json.JsonSerializer.Deserialize<PackAssignmentChangedEvent>(payloadJson)!); break;
            case "DeploymentCancelled": await client.DeploymentCancelled(System.Text.Json.JsonSerializer.Deserialize<DeploymentCancelledEvent>(payloadJson)!); break;
            case "RefreshRequested": await client.RefreshRequested(System.Text.Json.JsonSerializer.Deserialize<RefreshRequestedEvent>(payloadJson)!); break;
            default: throw new InvalidOperationException($"Unsupported outbox event type '{eventType}'.");
        }
    }
}
