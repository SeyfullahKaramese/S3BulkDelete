namespace S3BulkDelete.Models;

public sealed record MigrationEntry(
    string Scope,
    string Id,
    string SourceKey,
    string TargetKey,
    string Sha256,
    string Phase)
{
    // Daha önce kaydedilmiş günlüklerle uyumluluk için aşama metinleri korunur.
    public const string Copied = "Copied";
    public const string Updated = "Updated";
}
