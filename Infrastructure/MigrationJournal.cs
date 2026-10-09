using System.Text.Json;
using S3BulkDelete.Configuration;
using S3BulkDelete.Models;

namespace S3BulkDelete.Infrastructure;

public sealed class MigrationJournal : IDisposable
{
    private readonly FileStream runLock;
    private readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string scope;
    private readonly bool transferEnabled;

    public string DirectoryPath { get; }

    public MigrationJournal(AppSettings settings, string settingsPath)
    {
        DirectoryPath = Path.GetFullPath(settings.StateDirectory, Path.GetDirectoryName(settingsPath)!);
        Directory.CreateDirectory(DirectoryPath);
        // Aynı günlük dizininde iki taşıma sürecinin eşzamanlı çalışması engellenir.
        runLock = new FileStream(Path.Combine(DirectoryPath, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Günlüklerin farklı kaynak, hedef veya UPDATE ayarlarıyla devam ettirilmesi engellenir.
        transferEnabled = settings.TransferEnabled;
        scope = ContentHash.FromText(transferEnabled
            ? $"{settings.Source.Endpoint}|{settings.Source.Bucket}|{settings.Target!.Endpoint}|{settings.Target.Bucket}|{settings.Database.ConnectionString}|{settings.Database.UpdateSql}"
            : $"DeleteOnly|{settings.Source.Endpoint}|{settings.Source.Bucket}|{settings.Database.ConnectionString}|{settings.Database.UpdateSql}");
    }

    public MigrationEntry CreateEntry(string id, string sourceKey, string targetKey) =>
        new(scope, id, sourceKey, targetKey, "", transferEnabled ? MigrationEntry.Copied : MigrationEntry.DeletePrepared);

    public string GetPath(MigrationEntry entry) =>
        Path.Combine(DirectoryPath, ContentHash.FromText(scope + "|" + entry.Id) + ".json");

    public async IAsyncEnumerable<(MigrationEntry Entry, string Path)> ReadPendingAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // IsDeleted=true olan kayıtlar SELECT sonucundan çıkmış olsa da bekleyen işlemler okunur.
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            var entry = JsonSerializer.Deserialize<MigrationEntry>(await File.ReadAllTextAsync(path, cancellationToken), json)
                ?? throw new InvalidOperationException("Journal entry is empty.");
            if (entry.Scope != scope)
                throw new InvalidOperationException("Journal belongs to another configuration. Use a separate StateDirectory.");
            yield return (entry, path);
        }
    }

    public async Task SaveAsync(MigrationEntry entry, string path, CancellationToken cancellationToken)
    {
        // Önce geçici dosya yazılır, ardından günlük dosyası değiştirilir; yarım JSON okunması önlenir.
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(entry, json), cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    // Günlük, kaynak yokluğu ve veritabanı güncellemesi tamamlandıktan sonra kaldırılır.
    public void Complete(string path) => File.Delete(path);
    public void Dispose() => runLock.Dispose();
}
