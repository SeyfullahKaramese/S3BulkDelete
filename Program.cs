using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using NpgsqlTypes;

var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var settingsPath = Path.GetFullPath(args.FirstOrDefault() ?? "settings.json");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var ct = cancellation.Token;
try
{
    var cfg = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(settingsPath, ct), json)
        ?? throw new InvalidOperationException("Settings are empty.");
    cfg.Validate();
    var stateDir = Path.GetFullPath(cfg.StateDirectory, Path.GetDirectoryName(settingsPath)!);
    Directory.CreateDirectory(stateDir);
    using var runLock = new FileStream(Path.Combine(stateDir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    using var source = CreateClient(cfg.Source);
    using var target = CreateClient(cfg.Target);
    await using var db = NpgsqlDataSource.Create(cfg.Database.ConnectionString);
    var scope = Hash($"{cfg.Source.Endpoint}|{cfg.Source.Bucket}|{cfg.Target.Endpoint}|{cfg.Target.Bucket}|{cfg.Database.ConnectionString}|{cfg.Database.UpdateSql}");
    var failures = 0;
    var completed = 0;
    var resumed = new HashSet<string>();
    // Finish journal entries even when the SELECT no longer includes an updated row.
    foreach (var path in Directory.EnumerateFiles(stateDir, "*.json"))
    {
        var entry = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path, ct), json)!;
        if (entry.Scope != scope) throw new InvalidOperationException("Journal belongs to another configuration. Use a separate StateDirectory.");
        resumed.Add(entry.Id);
        await Process(entry, path);
    }
    var rows = new List<Entry>();
    await using (var command = db.CreateCommand(cfg.Database.SelectSql))
    await using (var reader = await command.ExecuteReaderAsync(ct))
    {
        var id = reader.GetOrdinal("Id");
        var key = reader.GetOrdinal("ObjectKey");
        var targetOrdinal = Enumerable.Range(0, reader.FieldCount).FirstOrDefault(i => reader.GetName(i).Equals("TargetKey", StringComparison.OrdinalIgnoreCase), -1);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(id) || reader.IsDBNull(key)) throw new InvalidOperationException("Id and ObjectKey cannot be null.");
            var rowId = Convert.ToString(reader.GetValue(id), System.Globalization.CultureInfo.InvariantCulture)!;
            var objectKey = reader.GetString(key);
            rows.Add(new Entry(scope, rowId, objectKey, targetOrdinal < 0 ? objectKey : reader.GetString(targetOrdinal), "", "Copied"));
        }
    }
    if (rows.Select(r => r.Id).Distinct().Count() != rows.Count || rows.Select(r => r.SourceKey).Distinct().Count() != rows.Count || rows.Select(r => r.TargetKey).Distinct().Count() != rows.Count)
        throw new InvalidOperationException("SELECT must return unique Id, ObjectKey and TargetKey values.");
    foreach (var row in rows.Where(r => !resumed.Contains(r.Id)))
        await Process(row, Path.Combine(stateDir, Hash(scope + "|" + row.Id) + ".json"));
    Console.WriteLine($"Finished. Completed={completed}; Failed={failures}; DryRun={cfg.DryRun}; Selected={rows.Count}");
    Environment.ExitCode = failures == 0 ? 0 : 1;

    async Task Save(Entry entry, string path)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(entry, json), ct);
        File.Move(temp, path, true);
    }
    async Task Process(Entry entry, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entry.SourceKey) || string.IsNullOrWhiteSpace(entry.TargetKey)) throw new InvalidOperationException("Empty object key.");
            if (cfg.DryRun) { Console.WriteLine($"DRY RUN: row {entry.Id}: copy, verify, update, delete. No remote changes."); return; }
            if (!File.Exists(path))
            {
                var temp = Path.Combine(stateDir, Guid.NewGuid() + ".download");
                try
                {
                    string? contentType;
                    var metadata = new Dictionary<string, string>();
                    using (var response = await source.GetObjectAsync(cfg.Source.Bucket, entry.SourceKey, ct))
                    {
                        contentType = response.Headers.ContentType;
                        foreach (var key in response.Metadata.Keys) metadata[key] = response.Metadata[key];
                        await using var download = File.Create(temp);
                        await response.ResponseStream.CopyToAsync(download, ct);
                    }
                    await using (var file = File.OpenRead(temp)) entry = entry with { Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)) };
                    var existingHash = await RemoteHash(target, cfg.Target.Bucket, entry.TargetKey, ct);
                    if (existingHash is not null && existingHash != entry.Sha256) throw new InvalidOperationException("Target exists with different content; refusing overwrite.");
                    if (existingHash is null)
                    {
                        await using var file = File.OpenRead(temp);
                        var upload = new PutObjectRequest { BucketName = cfg.Target.Bucket, Key = entry.TargetKey, InputStream = file, AutoCloseStream = false, IfNoneMatch = "*", ContentType = contentType };
                        foreach (var item in metadata) upload.Metadata[item.Key] = item.Value;
                        await target.PutObjectAsync(upload, ct);
                    }
                    if (await RemoteHash(target, cfg.Target.Bucket, entry.TargetKey, ct) != entry.Sha256) throw new InvalidOperationException("Target SHA256 verification failed.");
                    await Save(entry, path);
                }
                finally { File.Delete(temp); }
            }
            if (await RemoteHash(target, cfg.Target.Bucket, entry.TargetKey, ct) != entry.Sha256) throw new InvalidOperationException("Journal target verification failed.");
            if (entry.Phase == "Copied")
            {
                await using var connection = await db.OpenConnectionAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = cfg.Database.UpdateSql;
                command.Transaction = transaction;
                object value = cfg.Database.IdType switch { "bigint" => long.Parse(entry.Id), "integer" => int.Parse(entry.Id), "uuid" => Guid.Parse(entry.Id), _ => entry.Id };
                var type = cfg.Database.IdType switch { "bigint" => NpgsqlDbType.Bigint, "integer" => NpgsqlDbType.Integer, "uuid" => NpgsqlDbType.Uuid, _ => NpgsqlDbType.Text };
                command.Parameters.AddWithValue("Id", type, value);
                command.Parameters.AddWithValue("TargetKey", entry.TargetKey);
                command.Parameters.AddWithValue("TargetUrl", $"{cfg.Target.Endpoint.TrimEnd('/')}/{Uri.EscapeDataString(cfg.Target.Bucket)}/{string.Join('/', entry.TargetKey.Split('/').Select(Uri.EscapeDataString))}");
                if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("UPDATE must affect exactly one row; transaction rolled back.");
                await transaction.CommitAsync(ct);
                entry = entry with { Phase = "Updated" };
                await Save(entry, path);
            }
            if (entry.Phase != "Updated") throw new InvalidOperationException("Unknown journal phase.");
            var sourceHash = await RemoteHash(source, cfg.Source.Bucket, entry.SourceKey, ct);
            if (sourceHash is not null && sourceHash != entry.Sha256) throw new InvalidOperationException("Source changed since copy; refusing delete.");
            if (sourceHash is not null) await source.DeleteObjectAsync(cfg.Source.Bucket, entry.SourceKey, ct);
            if (await RemoteHash(source, cfg.Source.Bucket, entry.SourceKey, ct) is not null) throw new InvalidOperationException("Source still exists after delete.");
            File.Delete(path);
            completed++;
            Console.WriteLine($"Moved row {entry.Id}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { failures++; Console.Error.WriteLine($"Row {entry.Id} failed ({Describe(ex)}). Source deletion is gated by verification and committed UPDATE; inspect configuration/server logs and rerun."); }
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled; pending journal entries retained."); Environment.ExitCode = 130; }
catch (Exception ex) { Console.Error.WriteLine($"Startup failed ({Describe(ex)}). Check settings, connectivity and journal configuration."); Environment.ExitCode = 1; }

static string Describe(Exception ex) => ex switch
{
    InvalidOperationException => ex.Message,
    PostgresException pg => $"PostgreSQL SQLSTATE {pg.SqlState}",
    AmazonS3Exception s3 => $"S3 HTTP {(int)s3.StatusCode}, code {s3.ErrorCode}",
    _ => ex.GetType().Name
};
static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
static AmazonS3Client CreateClient(Storage s) => new(new BasicAWSCredentials(s.AccessKey, s.SecretKey), new AmazonS3Config { ServiceURL = s.Endpoint, ForcePathStyle = true, AuthenticationRegion = s.Region });
static async Task<string?> RemoteHash(IAmazonS3 client, string bucket, string key, CancellationToken ct)
{
    try { using var response = await client.GetObjectAsync(bucket, key, ct); return Convert.ToHexString(await SHA256.HashDataAsync(response.ResponseStream, ct)); }
    catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
}
record Entry(string Scope, string Id, string SourceKey, string TargetKey, string Sha256, string Phase);
record Storage(string Endpoint, string Region, string Bucket, string AccessKey, string SecretKey);
record Database(string ConnectionString, string IdType, string SelectSql, string UpdateSql);
record Settings([property: System.Text.Json.Serialization.JsonRequired] bool DryRun, string StateDirectory, Database Database, Storage Source, Storage Target)
{
    public void Validate()
    {
        if (Database is null || Source is null || Target is null || string.IsNullOrWhiteSpace(StateDirectory) || string.IsNullOrWhiteSpace(Database.ConnectionString) || string.IsNullOrWhiteSpace(Database.SelectSql) || string.IsNullOrWhiteSpace(Database.UpdateSql)) throw new InvalidOperationException("Missing settings.");
        if (Database.IdType is not ("bigint" or "integer" or "uuid" or "text")) throw new InvalidOperationException("Unsupported IdType.");
        foreach (var s in new[] { Source, Target })
            if (!Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(s.Bucket) || string.IsNullOrWhiteSpace(s.Region) || string.IsNullOrWhiteSpace(s.AccessKey) || string.IsNullOrWhiteSpace(s.SecretKey)) throw new InvalidOperationException("Invalid storage settings.");
        if (Source.Endpoint.TrimEnd('/') == Target.Endpoint.TrimEnd('/') && Source.Bucket == Target.Bucket) throw new InvalidOperationException("Source and target buckets must differ.");
    }
}
