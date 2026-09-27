using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftManager.Server.Application.Abstractions;
using MinecraftManager.Server.Contracts.V1;
using MinecraftManager.Server.Domain.Common;
using MinecraftManager.Server.Domain.Entities;

namespace MinecraftManager.Server.Application.Services;

public sealed record DeviceRegistrationResult(Machine Machine, string RefreshCredential);

public sealed class ClientRegistry(IServerDataStore data, ISystemClock clock)
{
    public async Task<(string Code, RegistrationCode Entity)> CreateCodeAsync(Guid ownerId, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var entity = new RegistrationCode { OwnerUserId = ownerId, CodeHash = Hash(code), ExpiresAtUtc = clock.UtcNow.Add(lifetime) };
        data.Add(entity); await data.SaveChangesAsync(cancellationToken); return (code, entity);
    }

    public async Task<DeviceRegistrationResult> RegisterAsync(string code, string displayName, CancellationToken cancellationToken)
    {
        var hash = Hash(code);
        var registration = await data.SingleOrDefaultAsync(data.RegistrationCodes.Where(x => x.CodeHash == hash), cancellationToken) ?? throw new DomainRuleException("registration_invalid", "The registration code is invalid or expired.");
        if (registration.ConsumedAtUtc is not null || registration.ExpiresAtUtc <= clock.UtcNow) throw new DomainRuleException("registration_invalid", "The registration code is invalid or expired.");
        registration.ConsumedAtUtc = clock.UtcNow; registration.ConcurrencyToken = Guid.NewGuid();
        var machine = new Machine { OwnerUserId = registration.OwnerUserId, DisplayName = Bounded(displayName, 120) };
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        data.Add(machine); data.Add(new DeviceCredential { MachineId = machine.Id, SecretHash = Hash(secret), FamilyId = Guid.NewGuid(), ExpiresAtUtc = clock.UtcNow.AddDays(30) });
        await data.SaveChangesAsync(cancellationToken); return new(machine, secret);
    }

    public async Task<(Machine Machine, string RefreshCredential)> RotateAsync(string credential, CancellationToken cancellationToken)
    {
        var hash = Hash(credential);
        var current = await data.SingleOrDefaultAsync(data.DeviceCredentials.Where(x => x.SecretHash == hash), cancellationToken) ?? throw new DomainRuleException("credential_invalid", "The device credential is invalid.");
        var machine = await data.SingleOrDefaultAsync(data.Machines.Where(x => x.Id == current.MachineId), cancellationToken) ?? throw new DomainRuleException("credential_invalid", "The device credential is invalid.");
        if (current.ReplacedById is not null)
        {
            foreach (var member in await data.ToListAsync(data.DeviceCredentials.Where(x => x.FamilyId == current.FamilyId && x.RevokedAtUtc == null), cancellationToken)) member.RevokedAtUtc = clock.UtcNow;
            await data.SaveChangesAsync(cancellationToken);
            throw new DomainRuleException("credential_replay", "The device credential family was revoked after replay detection.");
        }
        if (current.RevokedAtUtc is not null || current.ExpiresAtUtc <= clock.UtcNow || machine.RevokedAtUtc is not null) throw new DomainRuleException("credential_invalid", "The device credential is invalid.");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var replacement = new DeviceCredential { MachineId = machine.Id, SecretHash = Hash(secret), FamilyId = current.FamilyId, ExpiresAtUtc = clock.UtcNow.AddDays(30) };
        current.RevokedAtUtc = clock.UtcNow; current.ReplacedById = replacement.Id; current.ConcurrencyToken = Guid.NewGuid(); data.Add(replacement); await data.SaveChangesAsync(cancellationToken); return (machine, secret);
    }

    public async Task<MinecraftInstance> UpsertInstanceAsync(Guid machineId, Guid clientInstanceId, UpsertInstanceRequest request, CancellationToken cancellationToken)
    {
        var machine = await data.SingleOrDefaultAsync(data.Machines.Where(x => x.Id == machineId && x.RevokedAtUtc == null), cancellationToken) ?? throw new DomainRuleException("machine_revoked", "The machine is not active.");
        var instance = await data.SingleOrDefaultAsync(data.Instances.Where(x => x.MachineId == machineId && x.ClientInstanceId == clientInstanceId), cancellationToken);
        if (instance is null) { instance = new MinecraftInstance { MachineId = machine.Id, ClientInstanceId = clientInstanceId, DisplayName = Bounded(request.DisplayName, 120) }; data.Add(instance); }
        instance.DisplayName = Bounded(request.DisplayName, 120); instance.InstalledPackVersion = Optional(request.InstalledPackVersion, 128); instance.LastSeenAtUtc = clock.UtcNow;
        machine.Os = Bounded(request.Os, 32); machine.Architecture = Bounded(request.Architecture, 32); machine.ClientVersion = Bounded(request.ClientVersion, 64); machine.LastSeenAtUtc = clock.UtcNow;
        await data.SaveChangesAsync(cancellationToken); return instance;
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Bounded(string value, int max) => string.IsNullOrWhiteSpace(value) || value.Length > max ? throw new DomainRuleException("invalid_value", "A supplied value is invalid.") : value.Trim();
    private static string? Optional(string? value, int max) => value is null ? null : Bounded(value, max);
}

public sealed class DeploymentService(IServerDataStore data, ISystemClock clock)
{
    public async Task<PackAssignment> AssignAsync(Guid actorId, Guid instanceId, Guid packVersionId, CancellationToken cancellationToken)
    {
        var version = await data.SingleOrDefaultAsync(data.PackVersions.Where(x => x.Id == packVersionId && x.State == PackVersionState.Published), cancellationToken) ?? throw new DomainRuleException("version_not_published", "The pack version is not published.");
        if (!await data.AnyAsync(data.Instances.Where(x => x.Id == instanceId), cancellationToken)) throw new DomainRuleException("instance_not_found", "The instance was not found.");
        var active = await data.ToListAsync(data.Assignments.Where(x => x.InstanceId == instanceId && x.PackId == version.PackId && x.IsActive), cancellationToken);
        foreach (var old in active) old.IsActive = false;
        var assignment = new PackAssignment { InstanceId = instanceId, PackId = version.PackId, PackVersionId = version.Id, Revision = (active.Count == 0 ? 0 : active.Max(x => x.Revision)) + 1, AssignedAtUtc = clock.UtcNow };
        data.Add(assignment);
        data.Add(new OutboxMessage { Type = "PackAssignmentChanged", GroupName = $"instance:{instanceId}", PayloadJson = JsonSerializer.Serialize(new PackAssignmentChangedEvent(Guid.NewGuid(), instanceId, assignment.Revision, clock.UtcNow), ContractJson.Options) });
        await data.SaveChangesAsync(cancellationToken); return assignment;
    }

    public async Task<Deployment> CreateAsync(Guid actorId, Guid packVersionId, IReadOnlyCollection<Guid> targets, string? note, CancellationToken cancellationToken)
    {
        var version = await data.SingleOrDefaultAsync(data.PackVersions.Where(x => x.Id == packVersionId && x.State == PackVersionState.Published), cancellationToken) ?? throw new DomainRuleException("version_not_published", "The pack version is not published.");
        var pack = await data.SingleOrDefaultAsync(data.Packs.Where(x => x.Id == version.PackId), cancellationToken) ?? throw new DomainRuleException("pack_not_found", "Pack was not found.");
        var found = await data.ToListAsync(data.Instances.Where(x => targets.Contains(x.Id)), cancellationToken);
        if (found.Count != targets.Distinct().Count()) throw new DomainRuleException("target_not_found", "One or more deployment targets were not found.");
        var deployment = new Deployment { PackVersionId = packVersionId, CreatedByUserId = actorId, Note = note };
        foreach (var target in found) deployment.Targets.Add(new DeploymentTarget { InstanceId = target.Id });
        data.Add(deployment);
        foreach (var target in found) data.Add(new OutboxMessage { Type = "UpdateAvailable", GroupName = $"instance:{target.Id}", PayloadJson = JsonSerializer.Serialize(new UpdateAvailableEvent(Guid.NewGuid(), target.Id, deployment.Id, pack.Slug, version.Version, clock.UtcNow), ContractJson.Options) });
        await data.SaveChangesAsync(cancellationToken); return deployment;
    }

    public async Task<DeploymentTarget> ReportAsync(Guid machineId, Guid deploymentId, DeploymentStatusRequest report, CancellationToken cancellationToken)
    {
        var instance = await data.SingleOrDefaultAsync(data.Instances.Where(x => x.MachineId == machineId && x.ClientInstanceId == report.ClientInstanceId), cancellationToken) ?? throw new DomainRuleException("deployment_not_found", "The deployment was not found.");
        var target = await data.SingleOrDefaultAsync(data.DeploymentTargets.Where(x => x.DeploymentId == deploymentId && x.InstanceId == instance.Id), cancellationToken) ?? throw new DomainRuleException("deployment_not_found", "The deployment was not found.");
        if (!Enum.TryParse<DeploymentTargetStatus>(report.Status, true, out var status)) throw new DomainRuleException("invalid_status", "The deployment status is invalid.");
        target.Report(report.Sequence, status, report.Error?.Code, clock.UtcNow); await data.SaveChangesAsync(cancellationToken); return target;
    }

    public async Task CancelAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        var deployment = await data.SingleOrDefaultAsync(data.Deployments.Where(x => x.Id == deploymentId), cancellationToken) ?? throw new DomainRuleException("deployment_not_found", "Deployment was not found.");
        if (deployment.State is DeploymentState.Cancelled or DeploymentState.Completed) return;
        deployment.State = DeploymentState.Cancelled; deployment.CancelledAtUtc = clock.UtcNow;
        var targets = await data.ToListAsync(data.DeploymentTargets.Where(x => x.DeploymentId == deploymentId), cancellationToken);
        foreach (var target in targets.Where(x => x.Status is not DeploymentTargetStatus.Succeeded and not DeploymentTargetStatus.Failed and not DeploymentTargetStatus.Declined))
            data.Add(new OutboxMessage { Type = "DeploymentCancelled", GroupName = $"instance:{target.InstanceId}", PayloadJson = JsonSerializer.Serialize(new DeploymentCancelledEvent(Guid.NewGuid(), target.InstanceId, deploymentId, clock.UtcNow), ContractJson.Options) });
        await data.SaveChangesAsync(cancellationToken);
    }
}
