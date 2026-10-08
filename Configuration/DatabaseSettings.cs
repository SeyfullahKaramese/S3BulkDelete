namespace S3BulkDelete.Configuration;

public sealed record DatabaseSettings(string ConnectionString, string IdType, string SelectSql, string UpdateSql);
