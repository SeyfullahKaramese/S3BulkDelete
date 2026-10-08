namespace S3BulkDelete.Configuration;

public sealed record StorageSettings(
    string Endpoint,
    string Region,
    string Bucket,
    string AccessKey,
    string SecretKey)
{
    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(Bucket)
            || string.IsNullOrWhiteSpace(Region)
            || string.IsNullOrWhiteSpace(AccessKey)
            || string.IsNullOrWhiteSpace(SecretKey))
            throw new InvalidOperationException("Invalid storage settings.");
    }
}
