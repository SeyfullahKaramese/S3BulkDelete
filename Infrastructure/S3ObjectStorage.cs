using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using S3BulkDelete.Configuration;

namespace S3BulkDelete.Infrastructure;

public sealed record DownloadedObject(string Sha256, string? ContentType, IReadOnlyDictionary<string, string> Metadata);

public sealed class S3ObjectStorage : IDisposable
{
    private readonly StorageSettings settings;
    private readonly AmazonS3Client client;

    public S3ObjectStorage(StorageSettings settings)
    {
        this.settings = settings;
        client = new AmazonS3Client(new BasicAWSCredentials(settings.AccessKey, settings.SecretKey),
            new AmazonS3Config { ServiceURL = settings.Endpoint, ForcePathStyle = true, AuthenticationRegion = settings.Region });
    }

    public async Task<DownloadedObject> DownloadAsync(string key, string path, CancellationToken cancellationToken)
    {
        string? contentType;
        var metadata = new Dictionary<string, string>();
        using (var response = await client.GetObjectAsync(settings.Bucket, key, cancellationToken))
        {
            contentType = response.Headers.ContentType;
            foreach (var name in response.Metadata.Keys)
                metadata[name] = response.Metadata[name];
            await using var file = File.Create(path);
            await response.ResponseStream.CopyToAsync(file, cancellationToken);
        }
        await using var downloadedFile = File.OpenRead(path);
        var hash = await ContentHash.FromStreamAsync(downloadedFile, cancellationToken);
        return new DownloadedObject(hash, contentType, metadata);
    }

    public async Task UploadIfMissingAsync(string key, string path, DownloadedObject downloaded, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        var request = new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = key,
            InputStream = file,
            AutoCloseStream = false,
            IfNoneMatch = "*",
            ContentType = downloaded.ContentType
        };
        foreach (var item in downloaded.Metadata)
            request.Metadata[item.Key] = item.Value;
        await client.PutObjectAsync(request, cancellationToken);
    }

    public async Task<string?> GetHashAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetObjectAsync(settings.Bucket, key, cancellationToken);
            return await ContentHash.FromStreamAsync(response.ResponseStream, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await client.DeleteObjectAsync(settings.Bucket, key, cancellationToken);

    public void Dispose() => client.Dispose();
}
