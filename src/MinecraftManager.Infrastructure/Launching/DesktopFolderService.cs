using System.Diagnostics;
using MinecraftManager.Core.Launching;

namespace MinecraftManager.Infrastructure.Launching;

public sealed class DesktopFolderService : IDesktopFolderService
{
    public Task OpenAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("The requested folder does not exist.");
        var info = new ProcessStartInfo { FileName = OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open", UseShellExecute = false };
        info.ArgumentList.Add(fullPath);
        Process.Start(info)?.Dispose();
        return Task.CompletedTask;
    }
}
