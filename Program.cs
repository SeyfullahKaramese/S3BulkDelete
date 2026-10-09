using S3BulkDelete.Configuration;
using S3BulkDelete.Infrastructure;
using S3BulkDelete.Services;

// Komut satırında dosya yolu verilmezse settings.json kullanılır.
var settingsPath = Path.GetFullPath(args.FirstOrDefault() ?? "settings.json");
using var cancellation = new CancellationTokenSource();
// Ctrl+C, devam eden işlemlere iptal sinyali gönderir; bekleyen günlükler korunur.
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    // Bağlantıları ve işlem günlüğünü aynı ayarlardan oluşturup taşıma akışını başlatır.
    var settings = await SettingsLoader.LoadAsync(settingsPath, cancellation.Token);
    using var journal = new MigrationJournal(settings, settingsPath);
    using var source = new S3ObjectStorage(settings.Source);
    using var target = new S3ObjectStorage(settings.Target);
    await using var repository = new PostgresFileRepository(settings.Database);
    var migration = new FileMigrationService(settings, repository, source, target, journal);
    Environment.ExitCode = await migration.RunAsync(cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled; pending journal entries retained.");
    Environment.ExitCode = 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Startup failed ({ErrorDescription.Describe(exception)}). Check settings, connectivity and journal configuration.");
    Environment.ExitCode = 1;
}
