using MinecraftManager.Core.Filesystem;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.Infrastructure.Updates;

public sealed class RecoveryService(IApplicationPaths appPaths, ITransactionJournalStore journals, ISafePathResolver paths, IHashService hashes) : IRecoveryService
{
    public Task<IReadOnlyList<TransactionJournal>> FindIncompleteAsync(CancellationToken ct) => journals.FindIncompleteAsync(ct);

    public async Task<UpdateResult> RollbackAsync(Guid transactionId, CancellationToken ct)
    {
        var journal = (await journals.FindIncompleteAsync(ct)).SingleOrDefault(x => x.TransactionId == transactionId);
        if (journal is null) return new(false, new("recovery_not_found", "The recovery transaction was not found.", false), transactionId);
        var backup = Path.Combine(GetTransactionRoot(transactionId), "backup");
        var root = TrustedRoot.Create(journal.RootPath);
        try
        {
            if (journal.Phase is TransactionPhase.Created or TransactionPhase.Staging or TransactionPhase.Staged or TransactionPhase.BackingUp)
            {
                await journals.SaveAsync(journal with { Phase = TransactionPhase.RolledBack }, ct);
                return new(true, null, transactionId);
            }
            await journals.SaveAsync(journal with { Phase = TransactionPhase.RollingBack }, ct);
            foreach (var op in journal.Operations.Reverse())
            {
                var target = paths.ResolveFile(root, RelativeManifestPath.Parse(op.Path));
                paths.VerifyNoLinks(root, target);
                if (op.Kind == FileChangeKind.Add)
                {
                    if (!File.Exists(target.FullPath)) continue;
                    if (op.State is not TransactionOperationState.Applied && journal.Phase is not TransactionPhase.Applying) continue;
                    if (op.AfterSha256 is null || !await MatchesAsync(target.FullPath, op.AfterSha256, ct)) throw new IOException($"Added file changed after interruption: {op.Path}");
                    File.Delete(target.FullPath);
                    continue;
                }
                var saved = Path.Combine(backup, op.Path.Replace('/', Path.DirectorySeparatorChar));
                if (op.State is TransactionOperationState.Pending && !File.Exists(saved)) continue;
                if (!File.Exists(saved) || op.BeforeSha256 is null || !await MatchesAsync(saved, op.BeforeSha256, ct)) throw new IOException($"Verified backup unavailable: {op.Path}");
                if (File.Exists(target.FullPath) && op.AfterSha256 is { } after && !await MatchesAsync(target.FullPath, after, ct) && !await MatchesAsync(target.FullPath, op.BeforeSha256, ct))
                    throw new IOException($"Target changed after interruption: {op.Path}");
                Directory.CreateDirectory(Path.GetDirectoryName(target.FullPath)!);
                File.Copy(saved, target.FullPath, true);
            }
            await journals.SaveAsync(journal with { Phase = TransactionPhase.RolledBack }, ct);
            return new(true, null, transactionId);
        }
        catch
        {
            await journals.SaveAsync(journal with { Phase = TransactionPhase.RecoveryRequired, FailureCode = "rollback_failed" }, CancellationToken.None);
            return new(false, new("recovery_required", "Automatic recovery could not safely restore every file. Recovery data was preserved.", false), transactionId);
        }
    }

    private async Task<bool> MatchesAsync(string file, string expected, CancellationToken ct) => string.Equals(await hashes.ComputeFileSha256Async(file, ct), expected, StringComparison.OrdinalIgnoreCase);
    private string GetTransactionRoot(Guid id)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appPaths.TransactionDirectory));
        var result = Path.GetFullPath(Path.Combine(root, id.ToString("N")));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("Unsafe recovery path.");
        return result;
    }
}
