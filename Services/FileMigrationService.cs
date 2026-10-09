using S3BulkDelete.Configuration;
using S3BulkDelete.Infrastructure;
using S3BulkDelete.Models;

namespace S3BulkDelete.Services;

public sealed class FileMigrationService(
    AppSettings settings,
    IFileRepository repository,
    IObjectStorage source,
    IObjectStorage? target,
    MigrationJournal journal)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var completed = 0;
        var resumed = new HashSet<string>();

        // Güncellenmiş kayıtlar SELECT sonucunda görünmeyebilir; önce yarım kalan işlemler tamamlanır.
        await foreach (var pending in journal.ReadPendingAsync(cancellationToken))
        {
            resumed.Add(pending.Entry.Id);
            await ProcessAndReportAsync(pending.Entry, pending.Path);
        }

        // Günlükten işlenen kayıtlar tekrar taşınmaz; kalan belgeler sırayla işlenir.
        var rows = await repository.SelectAsync(cancellationToken);
        foreach (var row in rows.Where(row => !resumed.Contains(row.Id)))
        {
            var entry = journal.CreateEntry(row.Id, row.SourceKey, row.TargetKey);
            await ProcessAndReportAsync(entry, journal.GetPath(entry));
        }

        Console.WriteLine($"Finished. Completed={completed}; Failed={failures}; DryRun={settings.DryRun}; TransferEnabled={settings.TransferEnabled}; Selected={rows.Count}");
        return failures == 0 ? 0 : 1;

        async Task ProcessAndReportAsync(MigrationEntry entry, string path)
        {
            try
            {
                await ProcessAsync(entry, path, cancellationToken);
                if (!settings.DryRun)
                {
                    completed++;
                    Console.WriteLine(settings.TransferEnabled
                        ? $"Moved row {entry.Id}: target SHA256 verified; database UPDATE committed; source deletion verified."
                        : $"Deleted row {entry.Id}: source deletion verified; database UPDATE committed.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                // Bir kaydın hatası diğer kayıtları durdurmaz; bekleyen günlük yeniden deneme için kalır.
                failures++;
                var recovery = settings.TransferEnabled
                    ? "Source deletion is gated by verification and committed UPDATE"
                    : "Database marking requires verified source absence; pending journal entries retain deletion progress";
                Console.Error.WriteLine($"Row {entry.Id} failed ({ErrorDescription.Describe(exception)}). {recovery}; inspect configuration/server logs and rerun.");
            }
        }
    }

    private async Task ProcessAsync(MigrationEntry entry, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.SourceKey) || (settings.TransferEnabled && string.IsNullOrWhiteSpace(entry.TargetKey)))
            throw new InvalidOperationException("Empty object key.");
        // Kuru çalışmada SQL UPDATE, S3 yükleme ve silme yapılmaz.
        if (settings.DryRun)
        {
            var plan = settings.TransferEnabled ? "copy, verify, update, delete" : "delete source, verify absence, update database";
            Console.WriteLine($"DRY RUN: row {entry.Id}: {plan}. No remote changes.");
            return;
        }

        if (!settings.TransferEnabled)
        {
            await DeleteAndMarkAsync(entry, path, cancellationToken);
            return;
        }

        Console.WriteLine($"Row {entry.Id}: source={settings.Source.Bucket}/{entry.SourceKey}; target={settings.Target!.Bucket}/{entry.TargetKey}; phase={entry.Phase}");

        if (!File.Exists(path))
            entry = await CopyAndJournalAsync(entry, path, cancellationToken);

        // Kaynaktan silmeden önce hedef kopya, günlükten devam edilen işlemlerde de doğrulanır.
        await VerifyTargetAsync(entry, "Journal target verification failed.", cancellationToken);
        Console.WriteLine($"Row {entry.Id}: target SHA256 verified.");
        if (entry.Phase == MigrationEntry.Copied)
        {
            // UPDATE commit edildikten sonra aşama kaydedilir. Arada kesinti olursa UPDATE tekrar çalışabilir.
            await repository.MarkTransferredAsync(entry, settings.Target!, cancellationToken);
            Console.WriteLine($"Row {entry.Id}: database UPDATE committed.");
            entry = entry with { Phase = MigrationEntry.Updated };
            await journal.SaveAsync(entry, path, cancellationToken);
        }
        if (entry.Phase != MigrationEntry.Updated)
            throw new InvalidOperationException("Unknown journal phase.");

        // Kaynak yalnızca hedef doğrulaması ve başarılı UPDATE sonrasında silinir.
        await DeleteVerifiedSourceAsync(entry, cancellationToken);
        journal.Complete(path);
    }

    private async Task DeleteAndMarkAsync(MigrationEntry entry, string path, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Row {entry.Id}: delete only; source={settings.Source.Bucket}/{entry.SourceKey}; phase={entry.Phase}");
        if (!File.Exists(path))
        {
            // Silme niyeti S3 değişikliğinden önce kaydedilir; DB hatası sonrası yeniden devam edilir.
            entry = entry with { Sha256 = await source.GetHashAsync(entry.SourceKey, cancellationToken) ?? "" };
            await journal.SaveAsync(entry, path, cancellationToken);
        }
        if (entry.Phase == MigrationEntry.DeletePrepared)
        {
            await DeleteVerifiedSourceAsync(entry, cancellationToken);
            entry = entry with { Phase = MigrationEntry.SourceDeleted };
            await journal.SaveAsync(entry, path, cancellationToken);
        }
        if (entry.Phase != MigrationEntry.SourceDeleted)
            throw new InvalidOperationException("Unknown delete-only journal phase.");
        // Daha önce silinmiş anahtarda yeni bir nesne oluştuysa kayıt işaretlenmez.
        if (await source.GetHashAsync(entry.SourceKey, cancellationToken) is not null)
            throw new InvalidOperationException("Source exists again; refusing database marking.");
        Console.WriteLine($"Row {entry.Id}: source absence verified.");
        await repository.MarkDeletedAsync(entry, cancellationToken);
        journal.Complete(path);
    }

    private async Task<MigrationEntry> CopyAndJournalAsync(MigrationEntry entry, string path, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(journal.DirectoryPath, Guid.NewGuid() + ".download");
        try
        {
            DownloadedObject downloaded;
            try
            {
                downloaded = await source.DownloadAsync(entry.SourceKey, temporaryPath, cancellationToken);
            }
            catch (Amazon.S3.AmazonS3Exception exception) when (
                exception.StatusCode == System.Net.HttpStatusCode.NotFound && exception.ErrorCode == "NoSuchKey")
            {
                throw new InvalidOperationException(
                    $"Source object not found: {settings.Source.Bucket}/{entry.SourceKey}. Check SELECT object keys and source environment; no UPDATE or deletion performed.", exception);
            }
            entry = entry with { Sha256 = downloaded.Sha256 };
            // Hedefte farklı içerik varsa işlem durur; aynı içerik varsa tekrar yüklemek gerekmez.
            var existingHash = await target!.GetHashAsync(entry.TargetKey, cancellationToken);
            if (existingHash is not null && existingHash != entry.Sha256)
                throw new InvalidOperationException("Target exists with different content; refusing overwrite.");
            if (existingHash is null)
                await target.UploadIfMissingAsync(entry.TargetKey, temporaryPath, downloaded, cancellationToken);

            // Hedef içeriği doğrulanmadan veritabanı güncelleme aşamasına geçilmez.
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
        if (await target!.GetHashAsync(entry.TargetKey, cancellationToken) != entry.Sha256)
            throw new InvalidOperationException(error);
    }

    private async Task DeleteVerifiedSourceAsync(MigrationEntry entry, CancellationToken cancellationToken)
    {
        // Kopyalamadan sonra kaynak değişmişse yeni içeriğin yanlışlıkla silinmesi engellenir.
        // Kontrol ile silme arasında değişiklik olabileceğinden diğer yazıcılar durdurulmalıdır.
        var sourceHash = await source.GetHashAsync(entry.SourceKey, cancellationToken);
        if (sourceHash is not null && sourceHash != entry.Sha256)
            throw new InvalidOperationException("Source changed since preparation; refusing delete.");
        if (sourceHash is not null)
        {
            Console.WriteLine($"Row {entry.Id}: deleting verified source.");
            await source.DeleteAsync(entry.SourceKey, cancellationToken);
        }
        // DELETE yanıtı tek başına yeterli sayılmaz; nesnenin artık okunamadığı (404) doğrulanır.
        if (await source.GetHashAsync(entry.SourceKey, cancellationToken) is not null)
            throw new InvalidOperationException("Source still exists after delete.");
    }
}
