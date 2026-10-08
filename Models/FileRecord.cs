namespace S3BulkDelete.Models;

public sealed record FileRecord(string Id, string SourceKey, string TargetKey);
