using S3BulkDelete.Configuration;
using S3BulkDelete.Models;

namespace S3BulkDelete.Infrastructure;

public interface IFileRepository
{
    Task<List<FileRecord>> SelectAsync(CancellationToken cancellationToken);
    Task MarkTransferredAsync(MigrationEntry entry, StorageSettings target, CancellationToken cancellationToken);
    Task MarkDeletedAsync(MigrationEntry entry, CancellationToken cancellationToken);
}
