namespace S3BulkDelete.Infrastructure;

public interface IObjectStorage
{
    Task<DownloadedObject> DownloadAsync(string key, string path, CancellationToken cancellationToken);
    Task UploadIfMissingAsync(string key, string path, DownloadedObject downloaded, CancellationToken cancellationToken);
    Task<string?> GetHashAsync(string key, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
