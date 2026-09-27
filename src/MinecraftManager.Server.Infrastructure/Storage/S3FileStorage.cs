using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using MinecraftManager.Server.Application.Abstractions;
using MinecraftManager.Server.Domain.Common;
using MinecraftManager.Server.Domain.Entities;

namespace MinecraftManager.Server.Infrastructure.Storage;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";
    public required string Bucket { get; init; }
    public string? ServiceUrl { get; init; }
    public string Region { get; init; } = "us-east-1";
    public bool ForcePathStyle { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public int DownloadTicketMinutes { get; init; } = 5;
}

public sealed class S3FileStorage(IAmazonS3 s3, IOptions<ObjectStorageOptions> options, IServerDataStore data, ISystemClock clock) : IFileStorage
{
    private readonly ObjectStorageOptions settings = options.Value;

    public async Task<StoredObject> StoreVerifiedAsync(Stream content, long maximumBytes, string? expectedSha256, long? expectedSize, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"minecraft-manager-{Guid.NewGuid():N}.upload");
        try
        {
            long size = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[131072];
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    size = checked(size + read);
                    if (size > maximumBytes) throw new DomainRuleException("file_too_large", "The uploaded file exceeds the configured limit.");
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (expectedSize is not null && expectedSize != size) throw new DomainRuleException("size_mismatch", "The uploaded length does not match the declaration.");
            if (expectedSha256 is not null && !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(new Sha256Digest(expectedSha256).Value), System.Text.Encoding.ASCII.GetBytes(digest))) throw new DomainRuleException("hash_mismatch", "The uploaded content failed verification.");
            var existing = await data.SingleOrDefaultAsync(data.FileBlobs.Where(x => x.Sha256 == digest && x.Size == size && x.State == BlobState.Verified), cancellationToken);
            if (existing is not null) return new(existing.Id, existing.Sha256, existing.Size, existing.StorageKey);
            var key = $"blobs/sha256/{digest[..2]}/{digest.Substring(2, 2)}/{digest}";
            if (!await ExistsAsync(key, cancellationToken))
            {
                await using var upload = File.OpenRead(temporaryPath);
                await s3.PutObjectAsync(new PutObjectRequest { BucketName = settings.Bucket, Key = key, InputStream = upload, AutoCloseStream = false, ContentType = "application/octet-stream" }, cancellationToken);
            }
            var blob = new FileBlob { Sha256 = digest, Size = size, StorageKey = key, State = BlobState.Verified, VerifiedAtUtc = clock.UtcNow };
            data.Add(blob);
            try { await data.SaveChangesAsync(cancellationToken); }
            catch
            {
                existing = await data.SingleOrDefaultAsync(data.FileBlobs.Where(x => x.Sha256 == digest && x.Size == size && x.State == BlobState.Verified), cancellationToken);
                if (existing is null) throw;
                return new(existing.Id, existing.Sha256, existing.Size, existing.StorageKey);
            }
            return new(blob.Id, digest, size, key);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    public Task<DownloadTicket> CreateDownloadTicketAsync(string storageKey, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expires = clock.UtcNow.Add(lifetime);
        var url = s3.GetPreSignedURL(new GetPreSignedUrlRequest { BucketName = settings.Bucket, Key = storageKey, Expires = expires.UtcDateTime, Verb = HttpVerb.GET });
        return Task.FromResult(new DownloadTicket(new Uri(url), expires));
    }

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken)
    {
        try { await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = settings.Bucket, Key = storageKey }, cancellationToken); return true; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
    }

    public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) => s3.DeleteObjectAsync(settings.Bucket, storageKey, cancellationToken);
}
