using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using S3BulkDelete.Configuration;

namespace S3BulkDelete.Infrastructure;

public sealed record DownloadedObject(string Sha256, string? ContentType, IReadOnlyDictionary<string, string> Metadata);

public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly StorageSettings settings;
    private readonly AmazonS3Client client;

    public S3ObjectStorage(StorageSettings settings)
    {
        this.settings = settings;
        // MinIO bağlantısında bucket adı istek yolunda kullanılır (path-style erişim).
        client = new AmazonS3Client(new BasicAWSCredentials(settings.AccessKey, settings.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = settings.Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = settings.Region,
                // Bazı S3 uyumlu sunucular SDK'nın otomatik checksum trailer biçimini desteklemez.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED
            });
    }

    public async Task<DownloadedObject> DownloadAsync(string key, string path, CancellationToken cancellationToken)
    {
        string? contentType;
        var metadata = new Dictionary<string, string>();
        // Nesne geçici dosyaya indirilirken içerik tipi ve kullanıcı metadata bilgileri korunur.
        using (var response = await client.GetObjectAsync(settings.Bucket, key, cancellationToken))
        {
            contentType = response.Headers.ContentType;
            foreach (var name in response.Metadata.Keys)
                metadata[name] = response.Metadata[name];
            await using var file = File.Create(path);
            await response.ResponseStream.CopyToAsync(file, cancellationToken);
        }
        // Kopyanın hedefteki içerikle karşılaştırılması için SHA-256 özeti hesaplanır.
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
            // Tam dosya gövdesi imzalanır; aws-chunked/trailer dönüşümü kullanılmaz.
            UseChunkEncoding = false,
            DisablePayloadSigning = false,
            // Aynı anahtarda nesne varsa yükleme reddedilir; mevcut dosyanın üzerine yazılmaz.
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
            // Uzak nesne yeniden okunur; yalnızca ETag veya dosya boyutuna güvenilmez.
            using var response = await client.GetObjectAsync(settings.Bucket, key, cancellationToken);
            return await ContentHash.FromStreamAsync(response.ResponseStream, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound && exception.ErrorCode == "NoSuchKey")
        {
            // NoSuchBucket ve proxy 404 yanıtları silme doğrulaması olarak kabul edilmez.
            return null;
        }
    }

    // Kaynak silindikten sonra yokluk kontrolü FileMigrationService tarafından yapılır.
    // Versioning açıksa bu istek eski sürümleri temizlemek yerine delete marker oluşturabilir.
    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await client.DeleteObjectAsync(settings.Bucket, key, cancellationToken);

    public void Dispose() => client.Dispose();
}
