using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.Infrastructure.Updates;

public sealed class UpdateExecutor(
    IApplicationPaths appPaths,
    ISafePathResolver paths,
    IHashService hashes,
    IConfigurationStore configuration,
    IInstanceStateStore states,
    ITransactionJournalStore journals,
    IHistoryStore history) : IUpdateExecutor
{
    public async Task<UpdateResult> ExecuteAsync(ConfirmedUpdatePlan confirmed, ValidatedManifest manifest, IUpdateSource source,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        var plan = confirmed.Plan;
        if (!plan.ManifestSha256.Equals(manifest.CanonicalSha256, StringComparison.Ordinal))
            return new(false, new("stale_plan", "The manifest changed; review the update again.", false));
        var tx = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var currentConfiguration = await configuration.LoadAsync(cancellationToken);
        var instance = currentConfiguration.Instances.SingleOrDefault(x => x.Id == plan.InstanceId)
            ?? throw new InvalidOperationException("The selected instance no longer exists.");
        var root = TrustedRoot.Create(instance.Location.GameDirectory);
        var txRoot = EnsureContainedTransactionPath(tx);
        var staging = Path.Combine(txRoot, "staging");
        var backup = Path.Combine(txRoot, "backup");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backup);
        var operations = BuildOperations(plan);
        var journal = new TransactionJournal(1, tx, plan.InstanceId, root.FullPath, plan.ManifestSha256, source.Identity, TransactionPhase.Created, operations, started);
        await journals.SaveAsync(journal, cancellationToken);
        try
        {
            journal = journal with { Phase = TransactionPhase.Staging };
            await journals.SaveAsync(journal, cancellationToken);
            await StageAsync(plan, manifest, source, staging, Math.Clamp(currentConfiguration.Settings.MaxConcurrentDownloads, 1, 8), progress, cancellationToken);
            journal = journal with { Phase = TransactionPhase.Staged };
            await journals.SaveAsync(journal, cancellationToken);
            EnsureSpace(root, plan.TotalDownloadSize + plan.EstimatedBackupSize);
            progress?.Report(new(UpdateStage.Verifying, null, 0,
                plan.FilesToReplace.Count + plan.FilesToDelete.Count, 0, plan.EstimatedBackupSize, null,
                "Revalidating installed files"));
            await RevalidateTargetsAsync(root, plan, cancellationToken);

            journal = journal with { Phase = TransactionPhase.BackingUp };
            await journals.SaveAsync(journal, CancellationToken.None);
            var backupTotal = operations.Count(x => x.Kind is FileChangeKind.Replace or FileChangeKind.Delete);
            var backupCompleted = 0;
            for (var i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                if (op.Kind is not (FileChangeKind.Replace or FileChangeKind.Delete)) continue;
                var target = paths.ResolveFile(root, RelativeManifestPath.Parse(op.Path));
                paths.VerifyNoLinks(root, target);
                var destination = Path.Combine(backup, op.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(target.FullPath, destination, false);
                operations[i] = op with { State = TransactionOperationState.BackedUp };
                progress?.Report(new(UpdateStage.BackingUp, op.Path, ++backupCompleted, backupTotal,
                    0, plan.EstimatedBackupSize, backupTotal == 0 ? 100 : backupCompleted * 100d / backupTotal,
                    "Backing up existing files"));
            }
            journal = journal with { Operations = operations.ToArray() };
            await journals.SaveAsync(journal, CancellationToken.None);

            journal = journal with { Phase = TransactionPhase.Applying };
            await journals.SaveAsync(journal, CancellationToken.None);
            for (var i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                var target = paths.ResolveFile(root, RelativeManifestPath.Parse(op.Path));
                paths.VerifyNoLinks(root, target);
                if (op.Kind == FileChangeKind.Delete) File.Delete(target.FullPath);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target.FullPath)!);
                    var staged = Path.Combine(staging, op.Path.Replace('/', Path.DirectorySeparatorChar));
                    var sibling = target.FullPath + ".mm-" + tx.ToString("N") + ".tmp";
                    File.Copy(staged, sibling, true);
                    File.Move(sibling, target.FullPath, true);
                }
                operations[i] = op with { State = TransactionOperationState.Applied };
                progress?.Report(new(UpdateStage.Applying, op.Path, i + 1, operations.Count, 0, 0, (i + 1d) / operations.Count * 100, "Applying verified files"));
            }

            journal = journal with { Phase = TransactionPhase.Validating, Operations = operations.ToArray() };
            await journals.SaveAsync(journal, CancellationToken.None);
            for (var i = 0; i < manifest.Value.Files.Count; i++)
            {
                var file = manifest.Value.Files[i];
                var target = paths.ResolveFile(root, RelativeManifestPath.Parse(file.Path));
                if (!File.Exists(target.FullPath) || !string.Equals(await hashes.ComputeFileSha256Async(target.FullPath, CancellationToken.None), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Final validation failed for {file.Path}.");
                progress?.Report(new(UpdateStage.Validating, file.Path, i + 1, manifest.Value.Files.Count,
                    0, 0, (i + 1d) / manifest.Value.Files.Count * 100, "Validating installed files"));
            }
            var state = new InstanceState(new(plan.PackId, plan.TargetVersion, plan.ManifestSha256, DateTimeOffset.UtcNow),
                manifest.Value.Files.Select(x => new ManagedFileRecord(x.Path, x.Sha256.ToLowerInvariant(), x.Size)).ToArray(), DateTimeOffset.UtcNow, null, false);
            await states.SaveAsync(plan.InstanceId, state, CancellationToken.None);
            journal = journal with { Phase = TransactionPhase.Committed };
            await journals.SaveAsync(journal, CancellationToken.None);
            await history.AppendAsync(new(tx, plan.InstanceId, plan.PackId, plan.TargetVersion, started, DateTimeOffset.UtcNow,
                plan.FilesToAdd.Count, plan.FilesToReplace.Count, plan.FilesToDelete.Count, source.DisplayName, true, null, false), CancellationToken.None);
            progress?.Report(new(UpdateStage.Completed, null, operations.Count, operations.Count, plan.TotalDownloadSize, plan.TotalDownloadSize, 100, "Update completed"));
            return new(true, null, tx);
        }
        catch (OperationCanceledException) when (journal.Phase is TransactionPhase.Created or TransactionPhase.Staging or TransactionPhase.Staged)
        {
            return new(false, new("cancelled", "The update was cancelled before files were changed.", true), tx);
        }
        catch (Exception ex)
        {
            var rolledBack = await RollbackAsync(root, backup, operations, journal, CancellationToken.None);
            await history.AppendAsync(new(tx, plan.InstanceId, plan.PackId, plan.TargetVersion, started, DateTimeOffset.UtcNow,
                plan.FilesToAdd.Count, plan.FilesToReplace.Count, plan.FilesToDelete.Count, source.DisplayName, false, Classify(ex), rolledBack), CancellationToken.None);
            return new(false, new(rolledBack ? Classify(ex) : "recovery_required",
                rolledBack ? "The update failed and previous files were restored." : "Recovery is required; update files were preserved.", rolledBack), tx);
        }
    }

    private async Task StageAsync(UpdatePlan plan, ValidatedManifest manifest, IUpdateSource source, string staging, int concurrency, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var needed = manifest.Value.Files.Where(file => plan.FilesToAdd.Any(x => x.Path == file.Path) || plan.FilesToReplace.Any(x => x.Path == file.Path)).ToArray();
        long processed = 0;
        var completed = 0;
        using var limiter = new SemaphoreSlim(concurrency, concurrency);
        var tasks = needed.Select(async file =>
        {
            await limiter.WaitAsync(ct);
            try
            {
                var destination = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = await source.OpenFileAsync(file, ct);
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    await input.CopyToAsync(output, 81920, ct);
                var info = new FileInfo(destination);
                if (info.Length != file.Size || !string.Equals(await hashes.ComputeFileSha256Async(destination, ct), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Hash or size mismatch for {file.Path}.");
                var bytes = Interlocked.Add(ref processed, file.Size);
                var count = Interlocked.Increment(ref completed);
                progress?.Report(new(UpdateStage.Downloading, file.Path, count, needed.Length, bytes, plan.TotalDownloadSize,
                    plan.TotalDownloadSize == 0 ? 100 : bytes * 100d / plan.TotalDownloadSize, "Staging and verifying files"));
            }
            finally { limiter.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private async Task RevalidateTargetsAsync(TrustedRoot root, UpdatePlan plan, CancellationToken ct)
    {
        foreach (var item in plan.FilesToReplace)
        {
            var target = paths.ResolveFile(root, RelativeManifestPath.Parse(item.Path)); paths.VerifyNoLinks(root, target);
            if (!File.Exists(target.FullPath) || !string.Equals(await hashes.ComputeFileSha256Async(target.FullPath, ct), item.ExistingSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The installation changed after review.");
        }
        foreach (var item in plan.FilesToDelete)
        {
            var target = paths.ResolveFile(root, RelativeManifestPath.Parse(item.Path)); paths.VerifyNoLinks(root, target);
            if (!File.Exists(target.FullPath) || !string.Equals(await hashes.ComputeFileSha256Async(target.FullPath, ct), item.ExistingSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A deletion target changed after review.");
        }
    }

    private async Task<bool> RollbackAsync(TrustedRoot root, string backup, List<TransactionOperation> operations, TransactionJournal journal, CancellationToken ct)
    {
        journal = journal with { Phase = TransactionPhase.RollingBack };
        await journals.SaveAsync(journal, ct);
        try
        {
            foreach (var op in operations.Where(x => x.State == TransactionOperationState.Applied).Reverse())
            {
                var target = paths.ResolveFile(root, RelativeManifestPath.Parse(op.Path));
                if (op.Kind == FileChangeKind.Add) { if (File.Exists(target.FullPath)) File.Delete(target.FullPath); }
                else
                {
                    var saved = Path.Combine(backup, op.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target.FullPath)!);
                    File.Copy(saved, target.FullPath, true);
                }
            }
            await journals.SaveAsync(journal with { Phase = TransactionPhase.RolledBack }, ct);
            return true;
        }
        catch
        {
            await journals.SaveAsync(journal with { Phase = TransactionPhase.RecoveryRequired, FailureCode = "rollback_failed" }, ct);
            return false;
        }
    }

    private List<TransactionOperation> BuildOperations(UpdatePlan plan) =>
        [.. plan.FilesToAdd.Select(x => new TransactionOperation(FileChangeKind.Add, x.Path, null, x.Sha256, x.Size)),
         .. plan.FilesToReplace.Select(x => new TransactionOperation(FileChangeKind.Replace, x.Path, x.ExistingSha256, x.Sha256, x.Size)),
         .. plan.FilesToDelete.Select(x => new TransactionOperation(FileChangeKind.Delete, x.Path, x.ExistingSha256, null, x.Size))];

    private string EnsureContainedTransactionPath(Guid tx)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appPaths.TransactionDirectory));
        var result = Path.GetFullPath(Path.Combine(root, tx.ToString("N")));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Unsafe transaction path.");
        return result;
    }
    private static void EnsureSpace(TrustedRoot root, long required)
    {
        var drive = new DriveInfo(Path.GetPathRoot(root.FullPath)!);
        if (drive.AvailableFreeSpace < required + 64L * 1024 * 1024) throw new InsufficientDiskSpaceException();
    }
    private static string Classify(Exception ex) => ex switch
    {
        InsufficientDiskSpaceException => "insufficient_disk_space",
        UnauthorizedAccessException => "filesystem_permission",
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "file_in_use",
        InvalidDataException => "integrity_error",
        IOException => "filesystem_error",
        _ => "update_failed"
    };

    private sealed class InsufficientDiskSpaceException : IOException
    {
        public InsufficientDiskSpaceException() : base("There is not enough free disk space for staging and backups.") { }
    }

}
