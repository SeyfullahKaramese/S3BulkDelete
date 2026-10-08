namespace S3BulkDelete.Models;

public sealed record MigrationEntry(
    string Scope,
    string Id,
    string SourceKey,
    string TargetKey,
    string Sha256,
    string Phase)
{
    // Keep the existing string values for compatibility with saved journals.
    public const string Copied = "Copied";
    public const string Updated = "Updated";
}
