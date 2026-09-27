using System.Security.Cryptography;
using System.Text;
using MinecraftManager.Core.Persistence;

namespace MinecraftManager.Infrastructure.Sources;

internal static class LocalArchiveCache
{
    public static string GetRoot(IApplicationPaths paths, FileInfo archive)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{archive.FullName}\n{archive.Length}\n{archive.LastWriteTimeUtc.Ticks}"))).ToLowerInvariant();
        return Path.Combine(paths.DataDirectory, "cache", "archives", fingerprint);
    }

    public static bool IsComplete(string root) => File.Exists(Path.Combine(root, ".complete"));
}
