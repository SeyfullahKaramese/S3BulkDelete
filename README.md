# S3BulkDelete

.NET 10 konsol uygulaması. PostgreSQL'den seçilen dosyaları public S3/MinIO bucket'ından private bucket'a taşır ve ilgili kaydın `is_transfer` alanını işaretler.

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

SELECT, null olmayan ve benzersiz `Id` ile `ObjectKey` kolonlarını döndürmelidir. İsteğe bağlı `TargetKey` farklı hedef anahtarı sağlar; yoksa anahtar korunur. Anahtar tam URL değil, bucket içindeki dosya yoludur. Örnek sorgu her çalıştırmada en fazla 100 kayıt işler. Aynı dosyaya birden fazla veritabanı kaydı referans veriyorsa sorguları ve veri modelini uyarlamadan kullanmayın.

```sql
SELECT id AS "Id", object_key AS "ObjectKey"
FROM files WHERE is_transfer = false ORDER BY id LIMIT 100;

UPDATE files SET is_transfer = true WHERE id = @Id;
```

`IdType`: `bigint`, `integer`, `uuid` veya `text`. UPDATE parametreleri: `@Id`, `@TargetKey`, `@TargetUrl`. UPDATE tam bir kaydı etkilemeli ve tekrar çalıştırılabilir olmalıdır. Örneğin `WHERE is_transfer = false` koşulunu UPDATE'e eklemeyin: commit sonrasında süreç kesilirse UPDATE yeniden çalışabilir. TargetUrl yalnızca kalıcı nesne adresidir; private dosyaya erişim için imzalı URL gerekir.

## İşlem sırası ve hata kurtarma

1. Kaynak indirilir; disk üzerinde geçici kopyanın SHA-256 özeti alınır.
2. Hedefte farklı içerik varsa üzerine yazılmaz. Yoksa koşullu yükleme yapılır; hedef tekrar indirilip SHA-256 doğrulanır.
3. İşlem günlüğü kalıcılaştırılır. UPDATE bir PostgreSQL transaction'ında çalıştırılıp commit edilir.
4. Kaynak içerik tekrar kontrol edilir, silinir ve artık bulunmadığı doğrulanır.

Veritabanı ve S3 arasında ortak transaction yoktur. Bu nedenle UPDATE başarısız olursa kaynak korunur, hedef kopya kalabilir. Silme başarısız olursa veritabanı işaretlenmiş olabilir; sonraki çalıştırma SELECT'ten bağımsız olarak işlem günlüğündeki silmeyi tamamlar. `StateDirectory` dosyalarını bekleyen işlemler varken silmeyin; yapılandırmayı değiştirmeyin. Aynı state dizininde eşzamanlı çalıştırma engellenir. Farklı makinelerde/dizinlerde aynı kayıtları eşzamanlı işlemeyin.

Taşıma sırasında ilgili nesneleri yazan diğer süreçleri durdurun: içerik kontrolü ile silme arasında başka istemci nesneyi değiştirebilir. Hedef bucket'ın private erişim politikası yönetici tarafından önceden kurulmalıdır; uygulama bucket/policy oluşturmaz. Kaynak versioning açıksa silme bir delete marker oluşturabilir; eski sürümler kalıcı olarak temizlenmez. Nesne ACL, sürüm geçmişi ve retention politikaları taşınmaz. Dosya başına geçici disk alanı ve doğrulama için ek ağ trafiği gerekir.

Kaynak yetkileri: GetObject, DeleteObject; hedef: GetObject, PutObject. PostgreSQL: SELECT ve UPDATE. Hatalar sır/parola sızdırmamak için türleriyle raporlanır. Başarılı çalıştırma çıkış kodu 0, hata 1, iptal 130'dur. Ctrl+C güvenli iptal ister.

## Entegrasyon testleri

Docker, Python 3/venv ve .NET SDK ile `bash tests/run.sh` çalıştırın. 15432 ve 19000 loopback portları boş olmalıdır. Test betiği geçici PostgreSQL ve Moto S3 emülatörü başlatır, dokuz kontrolü çalıştırır ve kendi servislerini kaldırır. Gerçek MinIO uyumluluğu ve üretim erişim yetkileri ayrıca doğrulanmalıdır. Testler kuru çalışma, içerik doğrulama, çakışma koruması, UPDATE hatası, kayıp kaynak, aynı hedef içerik ve silme hatası sonrasında yeniden devam etmeyi kapsar.
