using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using S3BulkDelete.Configuration;
using S3BulkDelete.Models;

namespace S3BulkDelete.Infrastructure;

public sealed class PostgresFileRepository : IAsyncDisposable
{
    private readonly DatabaseSettings settings;
    private readonly NpgsqlDataSource dataSource;

    public PostgresFileRepository(DatabaseSettings settings)
    {
        this.settings = settings;
        dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
    }

    public async Task<List<FileRecord>> SelectAsync(CancellationToken cancellationToken)
    {
        var rows = new List<FileRecord>();
        await using var command = dataSource.CreateCommand(settings.SelectSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var idOrdinal = reader.GetOrdinal("Id");
        var keyOrdinal = FindColumn(reader, "ObjectKey");
        var fileIdOrdinal = keyOrdinal < 0 ? FindColumn(reader, "FileId") : -1;
        if (keyOrdinal < 0 && fileIdOrdinal < 0)
            throw new InvalidOperationException("SELECT must return ObjectKey or FileId.");
        var targetOrdinal = Enumerable.Range(0, reader.FieldCount).FirstOrDefault(
            index => reader.GetName(index).Equals("TargetKey", StringComparison.OrdinalIgnoreCase), -1);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(idOrdinal) || reader.IsDBNull(keyOrdinal >= 0 ? keyOrdinal : fileIdOrdinal))
                throw new InvalidOperationException("Id and ObjectKey/FileId cannot be null.");
            var id = Convert.ToString(reader.GetValue(idOrdinal), CultureInfo.InvariantCulture)!;
            var sourceKey = keyOrdinal >= 0
                ? reader.GetString(keyOrdinal)
                : Convert.ToString(reader.GetValue(fileIdOrdinal), CultureInfo.InvariantCulture) + ".pdf";
            var targetKey = targetOrdinal < 0 ? sourceKey : reader.GetString(targetOrdinal);
            rows.Add(new FileRecord(id, sourceKey, targetKey));
        }

        ValidateUniqueRows(rows);
        return rows;
    }

    public async Task MarkTransferredAsync(
        MigrationEntry entry, StorageSettings target, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = settings.UpdateSql;
        command.Transaction = transaction;

        var (id, idType) = ParseId(entry.Id);
        command.Parameters.AddWithValue("Id", idType, id);
        command.Parameters.AddWithValue("TargetKey", entry.TargetKey);
        command.Parameters.AddWithValue("TargetUrl", BuildTargetUrl(target, entry.TargetKey));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("UPDATE must affect exactly one row; transaction rolled back.");
        await transaction.CommitAsync(cancellationToken);
    }

    private static int FindColumn(NpgsqlDataReader reader, string name) =>
        Enumerable.Range(0, reader.FieldCount).FirstOrDefault(
            index => reader.GetName(index).Equals(name, StringComparison.OrdinalIgnoreCase), -1);

    private (object Value, NpgsqlDbType Type) ParseId(string id) => settings.IdType switch
    {
        "bigint" => (long.Parse(id), NpgsqlDbType.Bigint),
        "integer" => (int.Parse(id), NpgsqlDbType.Integer),
        "uuid" => (Guid.Parse(id), NpgsqlDbType.Uuid),
        _ => (id, NpgsqlDbType.Text)
    };

    private static string BuildTargetUrl(StorageSettings target, string key)
    {
        var escapedKey = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        return $"{target.Endpoint.TrimEnd('/')}/{Uri.EscapeDataString(target.Bucket)}/{escapedKey}";
    }

    private static void ValidateUniqueRows(List<FileRecord> rows)
    {
        if (rows.Select(row => row.Id).Distinct().Count() != rows.Count
            || rows.Select(row => row.SourceKey).Distinct().Count() != rows.Count
            || rows.Select(row => row.TargetKey).Distinct().Count() != rows.Count)
            throw new InvalidOperationException("SELECT must return unique Id, ObjectKey and TargetKey values.");
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}
