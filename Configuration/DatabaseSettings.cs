using Npgsql;

namespace S3BulkDelete.Configuration;

public sealed record DatabaseSettings(string ConnectionString, string IdType, string SelectSql, string UpdateSql)
{
    public void ValidateConnectionString()
    {
        NpgsqlConnectionStringBuilder connection;
        try
        {
            connection = new NpgsqlConnectionStringBuilder(ConnectionString);
        }
        catch (ArgumentException exception)
        {
            // Ham bağlantı dizesi ve sağlayıcının hata metni kimlik bilgileri içerebilir.
            throw new InvalidOperationException("Invalid Database.ConnectionString. Check PostgreSQL option names and values.", exception);
        }

        if (!connection.Pooling)
            return;
        if (connection.MinPoolSize > connection.MaxPoolSize)
            throw new InvalidOperationException("Database.ConnectionString: Minimum Pool Size must not exceed Maximum Pool Size.");
        if (connection.ConnectionIdleLifetime < connection.ConnectionPruningInterval)
            throw new InvalidOperationException(
                $"Database.ConnectionString: ConnectionIdleLifetime ({connection.ConnectionIdleLifetime}s) must be at least ConnectionPruningInterval ({connection.ConnectionPruningInterval}s). Reduce ConnectionPruningInterval or increase ConnectionIdleLifetime.");
    }
}
