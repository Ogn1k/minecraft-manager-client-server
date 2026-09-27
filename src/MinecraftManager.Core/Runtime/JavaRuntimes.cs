using MinecraftManager.Core.Models;

namespace MinecraftManager.Core.Runtime;

public enum JavaRuntimeOrigin { Managed, JavaHome, Path, KnownLocation, Custom }
public sealed record JavaRuntime(string ExecutablePath, int MajorVersion, string Vendor, CpuArchitecture Architecture, JavaRuntimeOrigin Origin);
public sealed record JavaRuntimeValidationResult(bool IsCompatible, string? ErrorCode = null, string? Message = null);

public interface IJavaCompatibilityPolicy
{
    JavaRuntimeValidationResult Validate(JavaRuntime runtime, JavaRuntimeRequirement requirement, RuntimePlatform platform);
}

public interface IJavaRuntimeService
{
    Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(CancellationToken cancellationToken);
    Task<JavaRuntimeValidationResult> ValidateAsync(JavaRuntime runtime, JavaRuntimeRequirement requirement, RuntimePlatform platform, CancellationToken cancellationToken);
    Task<JavaRuntime?> SelectAsync(JavaSelection selection, JavaRuntimeRequirement requirement, RuntimePlatform platform, CancellationToken cancellationToken);
    Task<JavaRuntime?> ProvisionAsync(JavaRuntimeRequirement requirement, RuntimePlatform platform, CancellationToken cancellationToken);
}

public sealed class JavaCompatibilityPolicy : IJavaCompatibilityPolicy
{
    public JavaRuntimeValidationResult Validate(JavaRuntime runtime, JavaRuntimeRequirement requirement, RuntimePlatform platform)
    {
        if (runtime.MajorVersion != requirement.MajorVersion) return new(false, "incompatible_java", $"Java {requirement.MajorVersion} is required.");
        if (runtime.Architecture != platform.Architecture) return new(false, "java_architecture_mismatch", "Java architecture does not match the runtime platform.");
        return new(true);
    }
}
