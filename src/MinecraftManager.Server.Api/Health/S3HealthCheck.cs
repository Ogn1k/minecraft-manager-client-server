using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MinecraftManager.Server.Infrastructure.Storage;

namespace MinecraftManager.Server.Api.Health;

public sealed class S3HealthCheck(IAmazonS3 client, IOptions<ObjectStorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Value.Bucket, MaxKeys = 1 }, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) { return HealthCheckResult.Unhealthy("Object storage is unavailable.", exception); }
    }
}
