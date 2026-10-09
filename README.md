# S3BulkDelete

.NET 10 konsol uygulaması. PostgreSQL'den seçilen dosyaları public S3/MinIO bucket'ından private bucket'a taşır ve `public."GysDocuments"` tablosundaki ilgili kaydın `IsDeleted` alanını `true` yapar. Dosya anahtarı `FileId.pdf` olarak oluşturulur.

## Kurulum ve çalıştırma

```bash
cp settings.example.json settings.json
# settings.json içindeki bağlantıları, bucket'ları ve SQL sorgularını düzenleyin.
dotnet restore --locked-mode
dotnet build --no-restore
dotnet run --no-build -- settings.json
```

İlk çalıştırmada `DryRun: true` kullanın. Bu mod SELECT sorgusunu çalıştırıp planı gösterir; S3 erişimini veya UPDATE yetkisini doğrulamaz. Gerçek işlem için `DryRun: false` yapın. Parolaları içeren `settings.json` Git tarafından yok sayılır; dosya erişimini sınırlandırın. Ayrı bir dosya yolu da verilebilir.

## SQL sözleşmesi

SELECT, null olmayan ve benzersiz `Id` ile `FileId` kolonlarını döndürebilir; bu durumda kaynak ve hedef dosya adı `<FileId>.pdf` olur. Genel kullanım için `ObjectKey` kolonu da desteklenir; birlikte döndürülürse `ObjectKey` önceliklidir. İsteğe bağlı `TargetKey` farklı hedef anahtarı sağlar; yoksa anahtar korunur. Anahtar tam URL değil, bucket içindeki dosya yoludur. Örnek sorgu her çalıştırmada en fazla 100 kayıt işler. Aynı dosyaya birden fazla veritabanı kaydı referans veriyorsa sorguları ve veri modelini uyarlamadan kullanmayın.

```sql
SELECT "Id", "TenantId", "FileId", "DocumentTypeId", "Description",
       "CreationTime", "CreatorId", "LastModificationTime", "LastModifierId",
       "IsDeleted", "DeleterId", "DeletionTime"
FROM public."GysDocuments"
WHERE "IsDeleted" = false
ORDER BY "Id" LIMIT 100;

UPDATE public."GysDocuments" SET "IsDeleted" = true WHERE "Id" = @Id;
```

`GysDocuments.Id` Guid olduğundan `IdType: uuid` kullanılır. Genel kullanımda `bigint`, `integer` ve `text` tipleri de desteklenir. UPDATE parametreleri: `@Id`, `@TargetKey`, `@TargetUrl`. UPDATE tam bir kaydı etkilemeli ve tekrar çalıştırılabilir olmalıdır. Örneğin `WHERE "IsDeleted" = false` koşulunu UPDATE'e eklemeyin: commit sonrasında süreç kesilirse UPDATE yeniden çalışabilir. TargetUrl yalnızca kalıcı nesne adresidir; private dosyaya erişim için imzalı URL gerekir.

Mevcut `settings.json` dosyanızdaki SELECT/UPDATE sorgularını ve `IdType` ayarını da güncelleyin; örnek dosyayı değiştirmek kişisel ayarları değiştirmez. Yalnızca `IsDeleted` güncellenir; diğer kolonlar korunur. SELECT içindeki `IsDeleted = false` koşulu zaten işaretlenmiş kayıtların yeniden seçilmesini önler.

## İşlem sırası ve hata kurtarma

1. Kaynak indirilir; disk üzerinde geçici kopyanın SHA-256 özeti alınır.
2. Hedefte farklı içerik varsa üzerine yazılmaz. Yoksa koşullu yükleme yapılır; hedef tekrar indirilip SHA-256 doğrulanır.
3. İşlem günlüğü kalıcılaştırılır. UPDATE bir PostgreSQL transaction'ında çalıştırılıp commit edilir.
4. Kaynak içerik tekrar kontrol edilir, silinir ve artık bulunmadığı doğrulanır.

Veritabanı ve S3 arasında ortak transaction yoktur. Bu nedenle UPDATE başarısız olursa kaynak korunur, hedef kopya kalabilir. Silme başarısız olursa veritabanı işaretlenmiş olabilir; sonraki çalıştırma SELECT'ten bağımsız olarak işlem günlüğündeki silmeyi tamamlar. `StateDirectory` dosyalarını bekleyen işlemler varken silmeyin; yapılandırmayı değiştirmeyin. Aynı state dizininde eşzamanlı çalıştırma engellenir. Farklı makinelerde/dizinlerde aynı kayıtları eşzamanlı işlemeyin.

Taşıma sırasında ilgili nesneleri yazan diğer süreçleri durdurun: içerik kontrolü ile silme arasında başka istemci nesneyi değiştirebilir. Hedef bucket'ın private erişim politikası yönetici tarafından önceden kurulmalıdır; uygulama bucket/policy oluşturmaz. Kaynak versioning açıksa silme bir delete marker oluşturabilir; eski sürümler kalıcı olarak temizlenmez. Nesne ACL, sürüm geçmişi ve retention politikaları taşınmaz. Dosya başına geçici disk alanı ve doğrulama için ek ağ trafiği gerekir.

Kaynak yetkileri: GetObject, DeleteObject; hedef: GetObject, PutObject. PostgreSQL: SELECT ve UPDATE. Hatalar sır/parola sızdırmamak için türleriyle raporlanır. Başarılı çalıştırma çıkış kodu 0, hata 1, iptal 130'dur. Ctrl+C güvenli iptal ister.

## Entegrasyon testleri

Docker, Python 3/venv ve .NET SDK ile `bash tests/run.sh` çalıştırın. 15432 ve 19000 loopback portları boş olmalıdır. Test betiği geçici PostgreSQL ve Moto S3 emülatörü başlatır, on kontrolü çalıştırır ve kendi servislerini kaldırır. Gerçek MinIO uyumluluğu ve üretim erişim yetkileri ayrıca doğrulanmalıdır. Testler kuru çalışma, içerik doğrulama, çakışma koruması, UPDATE hatası, kayıp kaynak, aynı hedef içerik ve silme hatası sonrasında yeniden devam etmeyi kapsar.

## Kod yapısı

- `Program.cs`: uygulamayı başlatır, bağımlılıkları oluşturur ve iptal/çıkış kodlarını yönetir.
- `Configuration/`: JSON ayarlarını okur ve doğrular.
- `Models/`: veritabanı kayıtlarını ve işlem günlüğü verilerini tanımlar.
- `Infrastructure/PostgresFileRepository.cs`: SELECT ve transaction içindeki UPDATE işlemlerini yürütür.
- `Infrastructure/S3ObjectStorage.cs`: indirme, koşullu yükleme, hash kontrolü ve silme işlemlerini sağlar.
- `Infrastructure/MigrationJournal.cs`: eşzamanlı çalıştırma kilidini ve yarım kalan işlemleri yönetir.
- `Services/FileMigrationService.cs`: kopyalama, doğrulama, veritabanı güncelleme ve silme adımlarını sıralar.

Ayar dosyasındaki alan adları ve mevcut işlem günlüklerinin biçimi korunmuştur.
