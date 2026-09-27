using System.Security.Cryptography;
using System.Text.Json;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Security;

namespace MinecraftManager.Infrastructure.Security;

public sealed class PlatformCredentialStore(IApplicationPaths paths) : ISecureCredentialStore
{
    public bool IsAvailable => OperatingSystem.IsWindows();
    public async Task<DeviceCredential?> GetAsync(Guid id, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("No supported OS secure credential store is available. Managed mode is disabled.");
        var path = GetPath(id);
        if (!File.Exists(path)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(path, ct);
        var clear = ProtectedData.Unprotect(protectedBytes, GetEntropy(id), DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<DeviceCredential>(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public async Task SetAsync(Guid id, DeviceCredential credential, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("No supported OS secure credential store is available. Managed mode is disabled.");
        var clear = JsonSerializer.SerializeToUtf8Bytes(credential);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, GetEntropy(id), DataProtectionScope.CurrentUser);
            var path = GetPath(id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, protectedBytes, ct);
            File.Move(temporary, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public Task RemoveAsync(Guid id, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("No supported OS secure credential store is available. Managed mode is disabled.");
        ct.ThrowIfCancellationRequested();
        var path = GetPath(id); if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
    private string GetPath(Guid id) => Path.Combine(paths.DataDirectory, "credentials", id.ToString("N") + ".bin");
    private static byte[] GetEntropy(Guid id) => id.ToByteArray();
}
