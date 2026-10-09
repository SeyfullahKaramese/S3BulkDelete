using System.Text.Json;

namespace S3BulkDelete.Configuration;

public static class SettingsLoader
{
    public static async Task<AppSettings> LoadAsync(string path, CancellationToken cancellationToken)
    {
        // Bağlantı bilgileri ve SQL sorguları okunur; işlemlerden önce ayarlar doğrulanır.
        var contents = await File.ReadAllTextAsync(path, cancellationToken);
        var settings = JsonSerializer.Deserialize<AppSettings>(contents,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Settings are empty.");
        settings.Validate();
        return settings;
    }
}
