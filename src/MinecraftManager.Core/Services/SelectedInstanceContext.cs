namespace MinecraftManager.Core.Services;

public sealed record SelectedInstanceChangedEventArgs(Guid? PreviousId, Guid? CurrentId);
public interface ISelectedInstanceContext
{
    Guid? InstanceId { get; }
    event EventHandler<SelectedInstanceChangedEventArgs>? Changed;
    void Select(Guid? instanceId);
}

public sealed class SelectedInstanceContext : ISelectedInstanceContext
{
    public Guid? InstanceId { get; private set; }
    public event EventHandler<SelectedInstanceChangedEventArgs>? Changed;
    public void Select(Guid? instanceId)
    {
        if (InstanceId == instanceId) return;
        var previous = InstanceId;
        InstanceId = instanceId;
        Changed?.Invoke(this, new(previous, instanceId));
    }
}
