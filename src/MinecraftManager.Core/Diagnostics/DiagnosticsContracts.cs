namespace MinecraftManager.Core.Diagnostics;

public interface IDiagnosticsService
{
    Task<string> ExportAsync(CancellationToken cancellationToken);
}
