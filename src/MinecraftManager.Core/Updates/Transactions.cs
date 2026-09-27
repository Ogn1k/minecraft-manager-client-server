using MinecraftManager.Core.Manifests;

namespace MinecraftManager.Core.Updates;

public enum TransactionPhase { Created, Staging, Staged, BackingUp, Applying, Validating, Committed, RollingBack, RolledBack, RecoveryRequired }
public enum TransactionOperationState { Pending, BackedUp, Applied, Restored }

public sealed record TransactionOperation(
    FileChangeKind Kind, string Path, string? BeforeSha256, string? AfterSha256,
    long Size, TransactionOperationState State = TransactionOperationState.Pending);

public sealed record TransactionJournal(
    int SchemaVersion, Guid TransactionId, Guid InstanceId, string RootPath,
    string ManifestSha256, string SourceIdentity, TransactionPhase Phase,
    IReadOnlyList<TransactionOperation> Operations, DateTimeOffset StartedAtUtc,
    string? FailureCode = null);

public interface IUpdateExecutor
{
    Task<UpdateResult> ExecuteAsync(
        ConfirmedUpdatePlan confirmedPlan,
        ValidatedManifest manifest,
        IUpdateSource source,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IRecoveryService
{
    Task<IReadOnlyList<TransactionJournal>> FindIncompleteAsync(CancellationToken cancellationToken);
    Task<UpdateResult> RollbackAsync(Guid transactionId, CancellationToken cancellationToken);
}

public sealed class UpdateStateMachine
{
    private static readonly IReadOnlyDictionary<UpdateStage, UpdateStage[]> Allowed =
        new Dictionary<UpdateStage, UpdateStage[]>
        {
            [UpdateStage.Idle] = [UpdateStage.Checking],
            [UpdateStage.Checking] = [UpdateStage.Planning, UpdateStage.Failed, UpdateStage.Cancelled],
            [UpdateStage.Planning] = [UpdateStage.AwaitingConfirmation, UpdateStage.Failed, UpdateStage.Cancelled],
            [UpdateStage.AwaitingConfirmation] = [UpdateStage.Downloading, UpdateStage.Cancelled],
            [UpdateStage.Downloading] = [UpdateStage.Verifying, UpdateStage.Failed, UpdateStage.Cancelled],
            [UpdateStage.Verifying] = [UpdateStage.BackingUp, UpdateStage.Failed],
            [UpdateStage.BackingUp] = [UpdateStage.Applying, UpdateStage.RollingBack],
            [UpdateStage.Applying] = [UpdateStage.Validating, UpdateStage.RollingBack],
            [UpdateStage.Validating] = [UpdateStage.Completed, UpdateStage.RollingBack],
            [UpdateStage.RollingBack] = [UpdateStage.Failed, UpdateStage.RecoveryRequired],
            [UpdateStage.Completed] = [UpdateStage.Idle],
            [UpdateStage.Failed] = [UpdateStage.Idle],
            [UpdateStage.Cancelled] = [UpdateStage.Idle],
            [UpdateStage.RecoveryRequired] = [UpdateStage.RollingBack]
        };

    public UpdateStage Current { get; private set; } = UpdateStage.Idle;
    public void TransitionTo(UpdateStage next)
    {
        if (!Allowed.TryGetValue(Current, out var targets) || !targets.Contains(next))
            throw new InvalidOperationException($"Invalid update transition {Current} -> {next}.");
        Current = next;
    }
}

public static class UpdateConfirmation
{
    public static ConfirmedUpdatePlan Confirm(UpdatePlan plan)
    {
        if (plan.Warnings.Any(x => x.IsBlocking)) throw new InvalidOperationException("The plan contains blocking warnings.");
        var text = $"{plan.PlanId:N}|{plan.InstanceId:N}|{plan.ManifestSha256}|{plan.FilesToAdd.Count}|{plan.FilesToReplace.Count}|{plan.FilesToDelete.Count}";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new(plan, digest, DateTimeOffset.UtcNow);
    }
}
