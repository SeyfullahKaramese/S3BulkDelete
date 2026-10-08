using S3BulkDelete.Configuration;
using S3BulkDelete.Infrastructure;
using S3BulkDelete.Models;

namespace S3BulkDelete.Services;

public sealed class FileMigrationService(
    AppSettings settings,
    PostgresFileRepository repository,
    S3ObjectStorage source,
    S3ObjectStorage target,
    MigrationJournal journal)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var completed = 0;
        var resumed = new HashSet<string>();

        // Updated rows may no longer be selected, so finish journal entries first.
        await foreach (var pending in journal.ReadPendingAsync(cancellationToken))
        {
            resumed.Add(pending.Entry.Id);
            await ProcessAndReportAsync(pending.Entry, pending.Path);
        }

        var rows = await repository.SelectAsync(cancellationToken);
        foreach (var row in rows.Where(row => !resumed.Contains(row.Id)))
        {
            var entry = journal.CreateEntry(row.Id, row.SourceKey, row.TargetKey);
            await ProcessAndReportAsync(entry, journal.GetPath(entry));
        }

        Console.WriteLine($"Finished. Completed={completed}; Failed={failures}; DryRun={settings.DryRun}; Selected={rows.Count}");
        return failures == 0 ? 0 : 1;

        async Task ProcessAndReportAsync(MigrationEntry entry, string path)
        {
            try
            {
                await ProcessAsync(entry, path, cancellationToken);
                if (!settings.DryRun)
                {
                    completed++;
                    Console.WriteLine($"Moved row {entry.Id}.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"Row {entry.Id} failed ({ErrorDescription.Describe(exception)}). Source deletion is gated by verification and committed UPDATE; inspect configuration/server logs and rerun.");
            }
        }
    }

    private async Task ProcessAsync(MigrationEntry entry, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.SourceKey) || string.IsNullOrWhiteSpace(entry.TargetKey))
            throw new InvalidOperationException("Empty object key.");
        if (settings.DryRun)
        {
            Console.WriteLine($"DRY RUN: row {entry.Id}: copy, verify, update, delete. No remote changes.");
            return;
        }

        if (!File.Exists(path))
            entry = await CopyAndJournalAsync(entry, path, cancellationToken);

        await VerifyTargetAsync(entry, "Journal target verification failed.", cancellationToken);
        if (entry.Phase == MigrationEntry.Copied)
        {
            await repository.MarkTransferredAsync(entry, settings.Target, cancellationToken);
            entry = entry with { Phase = MigrationEntry.Updated };
            await journal.SaveAsync(entry, path, cancellationToken);
        }
        if (entry.Phase != MigrationEntry.Updated)
            throw new InvalidOperationException("Unknown journal phase.");

        await DeleteVerifiedSourceAsync(entry, cancellationToken);
        journal.Complete(path);
    }

    private async Task<MigrationEntry> CopyAndJournalAsync(MigrationEntry entry, string path, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(journal.DirectoryPath, Guid.NewGuid() + ".download");
        try
        {
            var downloaded = await source.DownloadAsync(entry.SourceKey, temporaryPath, cancellationToken);
            entry = entry with { Sha256 = downloaded.Sha256 };
            var existingHash = await target.GetHashAsync(entry.TargetKey, cancellationToken);
            if (existingHash is not null && existingHash != entry.Sha256)
                throw new InvalidOperationException("Target exists with different content; refusing overwrite.");
            if (existingHash is null)
                await target.UploadIfMissingAsync(entry.TargetKey, temporaryPath, downloaded, cancellationToken);

            await VerifyTargetAsync(entry, "Target SHA256 verification failed.", cancellationToken);
            await journal.SaveAsync(entry, path, cancellationToken);
            return entry;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private async Task VerifyTargetAsync(MigrationEntry entry, string error, CancellationToken cancellationToken)
    {
        if (await target.GetHashAsync(entry.TargetKey, cancellationToken) != entry.Sha256)
            throw new InvalidOperationException(error);
    }

    private async Task DeleteVerifiedSourceAsync(MigrationEntry entry, CancellationToken cancellationToken)
    {
        var sourceHash = await source.GetHashAsync(entry.SourceKey, cancellationToken);
        if (sourceHash is not null && sourceHash != entry.Sha256)
            throw new InvalidOperationException("Source changed since copy; refusing delete.");
        if (sourceHash is not null)
            await source.DeleteAsync(entry.SourceKey, cancellationToken);
        if (await source.GetHashAsync(entry.SourceKey, cancellationToken) is not null)
            throw new InvalidOperationException("Source still exists after delete.");
    }
}
