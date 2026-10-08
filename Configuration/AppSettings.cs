using System.Text.Json.Serialization;

namespace S3BulkDelete.Configuration;

public sealed record AppSettings(
    [property: JsonRequired] bool DryRun,
    string StateDirectory,
    DatabaseSettings Database,
    StorageSettings Source,
    StorageSettings Target)
{
    public void Validate()
    {
        if (Database is null || Source is null || Target is null
            || string.IsNullOrWhiteSpace(StateDirectory)
            || string.IsNullOrWhiteSpace(Database.ConnectionString)
            || string.IsNullOrWhiteSpace(Database.SelectSql)
            || string.IsNullOrWhiteSpace(Database.UpdateSql))
            throw new InvalidOperationException("Missing settings.");

        if (Database.IdType is not ("bigint" or "integer" or "uuid" or "text"))
            throw new InvalidOperationException("Unsupported IdType.");

        Source.Validate();
        Target.Validate();
        if (Source.Endpoint.TrimEnd('/') == Target.Endpoint.TrimEnd('/') && Source.Bucket == Target.Bucket)
            throw new InvalidOperationException("Source and target buckets must differ.");
    }
}
