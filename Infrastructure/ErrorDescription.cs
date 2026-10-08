using Amazon.S3;
using Npgsql;

namespace S3BulkDelete.Infrastructure;

public static class ErrorDescription
{
    // Avoid logging connection strings, credential values or server exception details.
    public static string Describe(Exception exception) => exception switch
    {
        InvalidOperationException => exception.Message,
        PostgresException postgres => $"PostgreSQL SQLSTATE {postgres.SqlState}",
        AmazonS3Exception s3 => $"S3 HTTP {(int)s3.StatusCode}, code {s3.ErrorCode}",
        _ => exception.GetType().Name
    };
}
