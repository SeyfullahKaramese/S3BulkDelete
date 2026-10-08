using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

// Uses disposable local PostgreSQL and an S3-compatible test server; never production settings.
var app = Path.GetFullPath(args[0]);
var root = Path.Combine(Path.GetTempPath(), "s3bulk-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var connection = "Host=localhost;Port=15432;Database=postgres;Username=postgres";
await using var db = NpgsqlDataSource.Create(connection);
using var s3 = new AmazonS3Client(new BasicAWSCredentials("testadmin", "testadmin-secret"), new AmazonS3Config { ServiceURL = "http://localhost:19000", ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
await s3.PutBucketAsync("public"); await s3.PutBucketAsync("private");
await Sql("CREATE TABLE files(id bigint PRIMARY KEY, object_key text NOT NULL, is_transfer boolean NOT NULL DEFAULT false)");
await Seed(1, "normal");
Check(await Run(1, true) == 0 && !await Marked(1) && await Exists("public", "normal") && !await Exists("private", "normal"), "dry run preserves state");
Check(await Run(1) == 0 && await Marked(1) && !await Exists("public", "normal") && await Body("private", "normal") == "content-normal", "copy / verify / update / delete");
await Seed(2, "collision"); await Put("private", "collision", "different");
Check(await Run(2) == 1 && !await Marked(2) && await Exists("public", "collision") && await Body("private", "collision") == "different", "collision protects both objects");
await Seed(3, "db-failure");
await Sql("CREATE FUNCTION reject_three() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.id = 3 THEN RAISE EXCEPTION 'test rejection'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_three BEFORE UPDATE ON files FOR EACH ROW EXECUTE FUNCTION reject_three()");
Check(await Run(3) == 1 && !await Marked(3) && await Exists("public", "db-failure") && await Exists("private", "db-failure"), "failed update retains source and journal");
await Sql("DROP TRIGGER reject_three ON files");
Check(await Run(3, selectNothing: true) == 0 && await Marked(3) && !await Exists("public", "db-failure"), "journal resumes independently of SELECT");
await Sql("INSERT INTO files VALUES(4,'missing',false)");
Check(await Run(4) == 1 && !await Marked(4), "missing source never updates database");
await Seed(5, "identical"); await Put("private", "identical", "content-identical");
Check(await Run(5) == 0 && await Marked(5) && !await Exists("public", "identical"), "identical target can be resumed");
await Seed(6, "delete-failure");
await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName="public", Policy="""
{"Version":"2012-10-17","Statement":[{"Effect":"Deny","Principal":"*","Action":"s3:DeleteObject","Resource":"arn:aws:s3:::public/*"}]}
""" });
Check(await Run(6) == 1 && await Marked(6) && await Exists("public", "delete-failure"), "delete denial retains journal after committed update");
await s3.DeleteBucketPolicyAsync("public");
Check(await Run(6, selectNothing: true) == 0 && !await Exists("public", "delete-failure") && await Marked(6), "committed update resumes deletion without SELECT row");
Console.WriteLine("9 integration checks passed.");
Directory.Delete(root, true);
async Task Sql(string sql) { await using var c = db.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }
async Task Seed(int id,string key) { await Sql($"INSERT INTO files VALUES({id},'{key}',false)"); await Put("public",key,"content-"+key); }
async Task Put(string bucket,string key,string body) => await s3.PutObjectAsync(new PutObjectRequest { BucketName=bucket, Key=key, ContentBody=body });
async Task<string> Body(string bucket,string key) { using var o = await s3.GetObjectAsync(bucket,key); using var r = new StreamReader(o.ResponseStream); return await r.ReadToEndAsync(); }
async Task<bool> Exists(string bucket,string key) { try { await Body(bucket,key); return true; } catch(AmazonS3Exception e) when(e.StatusCode==HttpStatusCode.NotFound) { return false; } }
async Task<bool> Marked(int id) { await using var c=db.CreateCommand($"SELECT is_transfer FROM files WHERE id={id}"); return (bool)(await c.ExecuteScalarAsync())!; }
async Task<int> Run(int id,bool dry=false,bool selectNothing=false)
{
    var settings = new {
        DryRun=dry, StateDirectory=Path.Combine(root,"state-"+id),
        Database=new { ConnectionString=connection, IdType="bigint", SelectSql=$"SELECT id AS \"Id\",object_key AS \"ObjectKey\" FROM files WHERE id={id} AND is_transfer=false"+(selectNothing?" AND false":""), UpdateSql="UPDATE files SET is_transfer=true WHERE id=@Id" },
        Source=new { Endpoint="http://localhost:19000", Region="us-east-1", Bucket="public", AccessKey="testadmin", SecretKey="testadmin-secret" },
        Target=new { Endpoint="http://localhost:19000", Region="us-east-1", Bucket="private", AccessKey="testadmin", SecretKey="testadmin-secret" }
    };
    var path=Path.Combine(root,"settings.json"); await File.WriteAllTextAsync(path,JsonSerializer.Serialize(settings));
    using var process=Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? throw new Exception("Set DOTNET_ROOT to SDK directory"), "dotnet")) { ArgumentList={app,path} })!;
    await process.WaitForExitAsync(); return process.ExitCode;
}
static void Check(bool result,string name) { if(!result) throw new Exception("FAIL: "+name); Console.WriteLine("PASS: "+name); }
